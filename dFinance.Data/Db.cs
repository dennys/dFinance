using System.Reflection;
using System.Text;
using dFinance.Data.Localization;
using Microsoft.Data.Sqlite;

namespace dFinance.Data;

/// <summary>開庫、跑遷移、備份。資料庫與留底 PDF 一律放可攜帶的 `data\` 資料夾（整個拷走就是移機）。</summary>
public static class Db
{
    /// <summary>
    /// 資料夾位置。可設定就是以後做設定的介面（設完再存進 settings 表就行）；
    /// 沒設定時用環境變數 DFINANCE_DATA，再沒有就是 exe 旁邊的 data\（portable）。
    /// 注意：exe 放在需要系統管理員權限的資料夾（如 Program Files）時寫不進去，要用環境變數指到別處。
    /// </summary>
    public static string DataDirectory { get; set; } = ResolveDataDirectory();

    static string ResolveDataDirectory()
    {
        var fromEnv = Environment.GetEnvironmentVariable("DFINANCE_DATA");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;

        // 開發時 exe 在 bin\ 底下，把帳本放那裡会被 dotnet clean 砍掉、Debug/Release 還會各存一份；
        // 所以往上找到 plan/repo 根（有 .git 或 dFinance.sln）就放那裏。發佈後找不到標記 → 就是 exe 旁邊的 data\。
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, ".git", "HEAD")) || File.Exists(Path.Combine(dir.FullName, "dFinance.sln")))
                return Path.Combine(dir.FullName, "data");
        return Path.Combine(AppContext.BaseDirectory, "data");
    }

    public static string DatabasePath => Path.Combine(DataDirectory, "dfinance.db");
    public static string DocumentsDirectory => Path.Combine(DataDirectory, "documents");
    public static string BackupDirectory => Path.Combine(DataDirectory, "backups");

    /// <summary>
    /// 開庫（必要时建庫）並跑完遷移。Foreign Keys 一定要在連線字串上，SQLite 預設不啟用外鍵。
    /// <c>Default Timeout=5</c>：批次匯入故意另開一條連線（UI 那一條不能共享，不是執行緒安全的），
    /// 兩邊同時寫時預設 30 秒才會丢 "database is locked" —— 整個視窗凍在那裡看起來像當機。
    /// 這個庫的查詢全是毫秒級，5 秒足夠，搶到鎖的那個早就赢了。
    /// </summary>
    public static SqliteConnection Open(string? path = null)
    {
        path ??= DatabasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var c = new SqliteConnection($"Data Source={path};Foreign Keys=True;Default Timeout=5");
        c.Open();
        Scalar(c, "PRAGMA journal_mode=WAL;");
        Migrate(c);
        return c;
    }

    /// <summary>依編號順序套用尚未執行過的嵌入遷移腳本；每個腳本一個交易。</summary>
    public static void Migrate(SqliteConnection c)
    {
        var applied = TableExists(c, "migration")
            ? Query(c, "SELECT version FROM migration ORDER BY version").Select(o => Convert.ToInt32(o)).ToHashSet()
            : [];

        foreach (var (version, name, sql) in Scripts())
        {
            if (applied.Contains(version)) continue;
            using var tx = c.BeginTransaction();
            using (var cmd = c.CreateCommand()) { cmd.Transaction = tx; cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
            using (var ins = c.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO migration(version, name, applied_at) VALUES($v, $n, $t);";
                ins.Parameters.AddWithValue("$v", version);
                ins.Parameters.AddWithValue("$n", name);
                ins.Parameters.AddWithValue("$t", Now());
                ins.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>啟動時備份，留最近 5 份。用備份 API 而不是複製檔，WAL 的未落盤資料才會進副本。</summary>
    public static void Backup(SqliteConnection source)
    {
        if (Path.GetFullPath(source.DataSource) == ":memory:") return;

        // 只准真人開程式才算「啟動」：否則 --uismoke 跑幾輪就把那 5 份額度用完，
        // 出事那天翻備份夾全是測試跑的同一份。（devcheck 不算：它跑 --autocat 那種大量改資料前，
        // 就是要這份備份當救命索；真不想留就自己設 DFINANCE_NO_BACKUP=1。）
        if (Environment.GetEnvironmentVariable("DFINANCE_NO_BACKUP") is { Length: > 0 }) return;
        if (Environment.GetCommandLineArgs().Skip(1).Contains("--uismoke")) return;
        Directory.CreateDirectory(BackupDirectory);
        var target = Path.Combine(BackupDirectory, $"dfinance-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        using var dest = new SqliteConnection($"Data Source={target}");
        source.BackupDatabase(dest);

        foreach (var old in Directory.GetFiles(BackupDirectory, "dfinance-*.db")
                                     .OrderByDescending(f => f, StringComparer.Ordinal).Skip(5))
            File.Delete(old);
    }

    public static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    public static string Today() => DateTime.Now.ToString("yyyy-MM-dd");

    /// <summary>
    /// 基準幣：只決定「報表用哪種幣講」、「要哪些幣對的匯率」。
    /// <c>exchange_rate</c> 的列明記 (from_ccy, to_ccy)，不會因為換基準幣而改變意義，所以改它不用清匯率。
    /// </summary>
    public static string BaseCurrency(SqliteConnection c)
        => Scalar(c, "SELECT value FROM settings WHERE key='base_currency';") as string is string v && v.Trim().Length > 0
            ? NormalizeCurrencyCode(v) : "TWD";

    /// <summary>介面語系（存進 settings，跟著資料庫走；不用 AppData/登録檔 — AGENTS 可攜性硬規則）。</summary>
    public static string UiLanguage(SqliteConnection c)
        => Scalar(c, "SELECT value FROM settings WHERE key='ui_language';") as string is string v && LocalizationManager.IsSupported(v.Trim())
            ? v.Trim() : LocalizationManager.DefaultCulture;

    public static void SetUiLanguage(SqliteConnection c, string code)
        => Exec(c, "INSERT OR REPLACE INTO settings(key, value) VALUES('ui_language', $v);", ("$v", code));

    /// <summary>幣別代碼統一成大寫（SQL 的比较是 BINARY，`usd` 會被当成另一種幣）。空白回 TWD。</summary>
    public static string NormalizeCurrencyCode(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? "TWD" : raw.Trim().ToUpperInvariant();

    /// <summary>嵌入的遷移腳本名稱（依編號）。測試拿它跟 migration 表對照，加腳本就不會再弄爆斷言。</summary>
    public static List<string> MigrationNames() => Scripts().Select(s => s.Name).ToList();

    public static int Exec(SqliteConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        Bind(cmd, ps);
        return cmd.ExecuteNonQuery();
    }

    public static object? Scalar(SqliteConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        Bind(cmd, ps);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;                  // SQL NULL 統一成 C# null，呼叫端才好用 is null 判
    }

    public static long InsertId(SqliteConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql.EndsWith(';') ? sql.TrimEnd(';') + " RETURNING id;" : sql + " RETURNING id;";
        Bind(cmd, ps);
        return Convert.ToInt64(cmd.ExecuteScalar()!);
    }

    public static List<object?> Query(SqliteConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        Bind(cmd, ps);
        using var r = cmd.ExecuteReader();
        var list = new List<object?>();
        while (r.Read()) list.Add(r.IsDBNull(0) ? null : r.GetValue(0));
        return list;
    }

    static void Bind(SqliteCommand cmd, (string Name, object? Value)[] ps)
    {
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
    }

    static bool TableExists(SqliteConnection c, string name)
        => Scalar(c, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$n;", ("$n", name)) is long n && n > 0;

    /// <summary>嵌入的遷移腳本，檔名格式 &lt;編號&gt;_&lt;名稱&gt;.sql。</summary>
    static IEnumerable<(int Version, string Name, string Sql)> Scripts()
    {
        var asm = typeof(Db).Assembly;
        foreach (var res in asm.GetManifestResourceNames()
                               .Where(n => n.Contains(".migrations.", StringComparison.Ordinal)
                                        && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                               .OrderBy(n => n, StringComparer.Ordinal))
        {
            var file = res[(res.LastIndexOf(".migrations.", StringComparison.Ordinal) + ".migrations.".Length)..];
            int version = int.Parse(file.AsSpan(0, file.IndexOf('_')));
            using var stream = asm.GetManifestResourceStream(res)!;
            yield return (version, Path.GetFileNameWithoutExtension(file), new StreamReader(stream, Encoding.UTF8).ReadToEnd());
        }
    }
}
