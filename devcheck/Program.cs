// 開發用（不在 sln 裡）：dotnet run -c Release -- <pdf> [password] [--rows]
// password 省略或寫 "-" 時，自動試 .env 裡所有密碼（逗號也算一組），抓到能開的那組。
// 匯入資料庫：dotnet run -c Release -- <pdf 或資料夾> - --import
// 標假分類（測報表用）：dotnet run -c Release -- - - --autocat
using dFinance.Parsing;
using dFinance.Data;

Console.OutputEncoding = System.Text.Encoding.UTF8;   // 重導到檔時要是 UTF-8，不然重看是亂碼

if (args.Length < 1) { Console.WriteLine("usage: devcheck <pdf> [password] [--rows|--words|--import|--rates|--autocat]"); return 1; }
bool dumpRows = args.Length > 2 && (args[2] == "--rows" || args[2] == "--words" || args[2] == "--ocr");
bool withX = args.Length > 2 && (args[2] == "--words" || args[2] == "--ocr");
bool withOcr = args.Length > 2 && args[2] == "--ocr";      // dump 時把 OCR 讀的字（标 ~）併進去

var passwords = new List<string>();
if (args.Length > 1 && args[1] != "-") passwords.Add(args[1]);
passwords.AddRange(EnvPasswords());
passwords = passwords.Distinct().ToList();

// 批次匯入：一份或一整夾的帳單進資料庫（同一份檔案重跑會被 sha256 擋掉）。
if (args.Length > 2 && args[2] == "--import")
{
    using var db = Db.Open();
    Db.Backup(db);
    Console.WriteLine($"data = {Db.DataDirectory}");
    var files = Directory.Exists(args[0])
        ? Directory.GetFiles(args[0], "*.pdf", SearchOption.AllDirectories)
        : [args[0]];
    foreach (var f in files.OrderBy(f => f, StringComparer.Ordinal))
        try { Console.WriteLine(new StatementImporter(db).ImportPdf(f, passwords).Summary); }
        catch (Exception e) { Console.WriteLine($"{Path.GetFileName(f)}：{e.GetType().Name} {e.Message.Split('\n')[0]}"); }
    return 0;
}

// 抓匯率（要連網，公司網就給 proxy）：dotnet run -c Release -- - - --rates [proxyUrl]
if (args.Length > 2 && args[2] == "--rates")
{
    using var db = Db.Open();
    var store = new LedgerStore(db);
    var proxy = args.Length > 3 && args[3] != "-" ? args[3] : store.RateProxy;
    Console.WriteLine($"data = {Db.DataDirectory}  基準幣 = {store.BaseCurrency}  proxy = {(string.IsNullOrWhiteSpace(proxy) ? "直連" : proxy)}");
    var source = new RateSource { ProxyUrl = proxy };
    Console.WriteLine("測試：" + source.Test(store));
    var (updated, errors) = source.Refresh(store);
    Console.WriteLine($"更新 {updated} 個幣對" + (errors.Count == 0 ? "" : "；" + string.Join("；", errors)));
    foreach (var r in store.RateRows())
        Console.WriteLine($"  {r.From} → {r.To} = {r.Rate}　{r.RateDate}　{r.Source}{(r.IsStale ? "　⚠過期" : "")}");
    return 0;
}

// 測試用髒標記：把尚未標分類的列全部標上分類（先照商家規則，比不到就在同 kind 的最底層分類裡輪流塞）。
// 這支故意不做成遷移：假標記不該進版控，真資料砍掉重練時它也不會跟著跑。
if (args.Length > 2 && args[2] == "--autocat")
{
    using var db = Db.Open();
    Db.Backup(db);
    var store = new LedgerStore(db);
    var leaves = new Dictionary<string, List<(long Id, string Path)>>
    {
        ["expense"] = store.Categories("expense", leavesOnly: true).Select(c => (c.Id, c.Name)).ToList(),
        ["income"] = store.Categories("income", leavesOnly: true).Select(c => (c.Id, c.Name)).ToList(),
    };
    Console.WriteLine($"data = {Db.DataDirectory}  可用最底層分類：expense {leaves["expense"].Count} 個、income {leaves["income"].Count} 個");

    var rows = new List<(long Id, string Kind, string? Merchant)>();
    using (var cmd = db.CreateCommand())
    {
        // 尚未標的 + 標在父類上的（舊版「建議分類」沒有 leaf 限制，會一批標在頂層）都要重塞。
        cmd.CommandText = """
            SELECT e.id, e.kind, e.merchant FROM entry e
            WHERE e.deleted_at IS NULL AND e.kind <> 'transfer'
              AND (e.category_id IS NULL OR EXISTS(SELECT 1 FROM category ch WHERE ch.parent_id = e.category_id))
            ORDER BY e.id;
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add((r.GetInt64(0), r.GetString(1), Str(r, 2)));
    }

    var byTop = new Dictionary<string, int>();
    int byRule = 0, i = 0;
    foreach (var (id, kind, merchant) in rows)
    {
        var side = kind == "income" ? leaves["income"] : leaves["expense"];         // refund/adjustment 算支出側
        var suggested = MerchantClassifier.Suggest(merchant);
        var hit = suggested is null ? [] : side.Where(c => c.Path == suggested || c.Path.StartsWith(suggested + "／")).ToList();
        if (hit.Count == 0) hit = side;                                             // 規則比不到 → 輪流塞，就是要讓報表每個格子都有數
        else byRule++;
        var pick = hit[(int)(Math.Abs(id) % hit.Count)];
        store.SetCategory(id, pick.Id);
        byTop[pick.Path.Split('／')[0]] = byTop.GetValueOrDefault(pick.Path.Split('／')[0]) + 1;
        i++;
    }
    Console.WriteLine($"標了 {i} 筆（商家規則命中 {byRule} 筆，其餘輪流塞）—— 這是假分類，不要當帳看");
    foreach (var (top, n) in byTop.OrderByDescending(kv => kv.Value)) Console.WriteLine($"  {top}　{n} 筆");
    return 0;
}

static string? Str(Microsoft.Data.Sqlite.SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

ParsedStatement? statement = null;
PageData? pageData = null;
string? openedWith = null;
int openedIndex = -1;
string lastError = "沒有可用的密碼";

foreach (var pw in passwords)
{
    try
    {
        pageData = PdfRows.Read(args[0], pw);   // 先探密碼（解密失敗在這裡抛）
        if (withOcr)
            pageData = pageData.WithOcr(OcrReader.Read(args[0], pw, Db.DataDirectory) ?? []);
        openedWith = pw;
        openedIndex = passwords.IndexOf(pw);
        statement = dumpRows ? new ParsedStatement() : StatementParser.Parse(args[0], pw, Db.DataDirectory);
        break;
    }
    catch (Exception e)
    {
        lastError = e.Message;
        // 只有密碼不對才換下一組；其他例外是 bug，直接爆出來看。
        if (e is not UglyToad.PdfPig.Exceptions.PdfDocumentEncryptedException) throw;
    }
}

if (statement is null)
{
    Console.WriteLine($"開不了：{lastError}（試過 {passwords.Count} 組密碼）");
    return 2;
}

if (!dumpRows) File.WriteAllText(Path.Combine(Path.GetTempPath(), Path.GetFileNameWithoutExtension(args[0]) + "_rawtext.txt"),
                  statement.RawText, System.Text.Encoding.UTF8);
Console.WriteLine($"opened with password #{openedIndex + 1} (len={openedWith!.Length})");

var s = statement;
Console.WriteLine($"issuer={s.Issuer} period={s.StatementStartDate}~{s.StatementEndDate} due={s.DueDate} total={s.TotalDue} min={s.MinimumDue} mask={s.AccountMask}");
Console.WriteLine($"txns={s.Transactions.Count} sum={s.Transactions.Sum(t => t.Amount)} cards={string.Join(",", s.Transactions.Select(t => t.Card).Distinct())}");
Console.WriteLine("byPage: " + string.Join(" ", s.Transactions.GroupBy(t => t.Page).Select(g => $"p{g.Key}={g.Count()}")));
Console.WriteLine("byCurrency: " + string.Join(" ", s.Transactions.GroupBy(t => t.Currency).Select(g => $"'{g.Key}'={g.Count()}")));
Console.WriteLine("credits: " + string.Join(" | ", s.Transactions.Where(t => t.Kind == "credit").Select(t => $"{t.Merchant} {t.Amount}")));
foreach (var w in s.Warnings) Console.WriteLine("WARN: " + w);
foreach (var t in s.Transactions.Take(5)) Console.WriteLine($"  {t.Date} {t.PostingDate} {t.Merchant} {t.Amount} {t.Currency} card={t.Card} p{t.Page}");
foreach (var t in s.Transactions.TakeLast(3)) Console.WriteLine($"  ...{t.Date} {t.Merchant} {t.Amount} {t.Kind}");

if (args.Length > 2 && args[2] == "--table")
{
    var t = StatementTable.Find(pageData!, ["臺幣金額", "台幣金額", "新臺幣金額", "金額"], ["消費日"]);
    if (t is null) { Console.WriteLine("table=null"); return 0; }
    string Box(WordBox? b) => b is null ? "-" : $"[{b.Left:F0},{b.Right:F0}]";
    Console.WriteLine($"table: header p{t.HeaderRow.Page} top={t.HeaderRow.Top:F1} date{Box(t.DateCol)} post{Box(t.PostCol)} card{Box(t.CardCol)} curr{Box(t.CurrencyCol)} amount{Box(t.AmountCol)} merchant[{t.MerchantLeft:F0},{t.MerchantRight:F0}]");
    return 0;
}

if (dumpRows)
{
    foreach (var r in pageData!.Rows) Console.WriteLine($"p{r.Page} top={r.Top:F1} " + (withX
        ? string.Join(" ~ ", r.Words.Select(w => $"{(w.FromOcr ? "~" : "")}{w.Text}[{w.Left:F0},{w.Right:F0}]"))
        : "[" + Row.Join(r.Words) + "]"));
    Console.WriteLine($"rows={pageData.Rows.Count} images={pageData.ImageCount}");
}
return 0;

/// <summary>.env 每行 KEY=VALUE，VALUE 用逗號分隔視為多組密碼。</summary>
static List<string> EnvPasswords()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, ".env"))) dir = dir.Parent;
    if (dir is null) return [];
    return File.ReadAllLines(Path.Combine(dir.FullName, ".env"))
               .Select(l => l.Split('=', 2))
               .Where(p => p.Length == 2)
               .SelectMany(p => p[1].Split(','))
               .Select(p => p.Trim().Trim('"'))
               .Where(p => p.Length > 0)
               .Distinct()
               .ToList();
}
