// 開發用（不在 sln 裡）：dotnet run -c Release -- <pdf> [password] [--rows]
// password 省略或寫 "-" 時，自動試 .env 裡所有密碼（逗號也算一組），抓到能開的那組。
// 匯入資料庫：dotnet run -c Release -- <pdf 或資料夾> - --import
using dFinance.Parsing;
using dFinance.Data;

Console.OutputEncoding = System.Text.Encoding.UTF8;   // 重導到檔時要是 UTF-8，不然重看是亂碼

if (args.Length < 1) { Console.WriteLine("usage: devcheck <pdf> [password] [--rows|--words|--import]"); return 1; }
bool dumpRows = args.Length > 2 && (args[2] == "--rows" || args[2] == "--words");
bool withX = args.Length > 2 && args[2] == "--words";

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
        openedWith = pw;
        openedIndex = passwords.IndexOf(pw);
        statement = dumpRows ? new ParsedStatement() : StatementParser.Parse(args[0], pw);
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
        ? string.Join(" ~ ", r.Words.Select(w => $"{w.Text}[{w.Left:F0},{w.Right:F0}]"))
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
