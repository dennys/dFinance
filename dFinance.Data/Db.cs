using System.Reflection;
using System.Text;
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

    /// <summary>開庫（必要时建庫）並跑完遷移。Foreign Keys 一定要在連線字串上，SQLite 預設不啟用外鍵。</summary>
    public static SqliteConnection Open(string? path = null)
    {
        path ??= DatabasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var c = new SqliteConnection($"Data Source={path};Foreign Keys=True");
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
