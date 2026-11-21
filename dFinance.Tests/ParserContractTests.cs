using dFinance.Parsing;
using dFinance.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace dFinance.Tests;

/// <summary>
/// 跨 parser 的輸出契約。每個銀行一支測試鎖的是「那份單解析得對不對」，
/// 這裡鎖的是「輸出像不像同一個 ParsedStatement」—— 欄位口徑不齐時（新 parser 忘了填 issuer、
/// 金額沒帶正負號、商家是空的、Date 留 default、餘額沒 as-of、幣別原文對不到 aliases）在這裡被抓，
/// 而不是等到匯入端悄悄少一筆。
/// </summary>
public sealed class ParserContractTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"dfinance-contract-{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _db;

    public ParserContractTests() => _db = Db.Open(_dbPath);

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_dbPath)!, Path.GetFileName(_dbPath) + "*"))
            File.Delete(f);
    }

    /// <summary>兩個樣本夾的所有 PDF（同檔名只取一份）；一個都沒有就整組跳過，不准空白通過。</summary>
    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();
        foreach (var f in Samples.Dirs.Where(Directory.Exists)
                     .SelectMany(d => Directory.GetFiles(d, "*.pdf"))
                     .GroupBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                     .Select(g => Path.GetFileName(g.First())))
            data.Add(f);
        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void 每份帳單的輸出都同一個口徑(string file)
    {
        var s = Samples.Parse(file);
        var at = $"[{file}] ";

        // 認不出來也要老實講（StatementParser 會補警告），並且不要把半份結果當解析成功。
        if (s.Parser.Length == 0)
        {
            Assert.NotEmpty(s.Warnings);
            Assert.Empty(s.Transactions);
            return;
        }

        // 單據種類是類名約定：匯入端 Classify 靠尾綴，拼錯就歸 Unknown（去重範圍跟著縮小）。
        Assert.True(new[] { "CardParser", "DepositParser", "BillParser" }.Any(s.Parser.EndsWith),
                    at + $"parser 類名尾綴不是 Card/Deposit/Bill：{s.Parser}");
        Assert.False(string.IsNullOrWhiteSpace(s.Issuer), at + "Issuer 是空的");
        if (s.Transactions.Count == 0)
            Assert.NotEmpty(s.Warnings);                       // 0 筆一定要有解釋（AGENTS：不准把警告拿掉讓測試變綠）
        if (s.StatementStartDate is { } start && s.StatementEndDate is { } end)
            Assert.False(start > end, at + $"帳期反了：{start} ~ {end}");

        Assert.All(s.Transactions, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Merchant), at + $"商家是空的：{t.RawLine}");
            Assert.False(string.IsNullOrWhiteSpace(t.RawLine), at + "沒有原始行，UI 上没法核對");
            Assert.True(t.Page >= 1, at + $"頁碼不对：{t.Page}");
            Assert.NotEqual(default, t.Date);                  // Date 不可為 null，0001-01-01 = 漏解析日期
            // 0 元是合法的（本期應繳被折抵掉），匯入端自己會跳過；卡號欄只能放卡號（遮罩的 * 也算）。
            Assert.True(t.Card.Length == 0 || t.Card.All(c => char.IsDigit(c) || c == '*'), at + $"卡號欄混進別的東西：{t.Card}");
        });

        // 餘額的價值全在 as-of：沒有它，匯入端不知該把哪些交易排除在期初校驗之外。
        Assert.All(s.Balances, b =>
        {
            Assert.False(b.AsOf == default, at + "餘額沒帶 as-of，不會進餘額校驗");
        });
    }

    /// <summary>
    /// 幣別口徑：parser 交出來的一定要是「單上的原文」或「三尺碼代碼」，而且必須對得到代碼。
    /// 對不到時匯入端會退回基準幣＋警告 —— 非台幣的錢被當成基準幣就是百倍等級的錯，所以這條要鎖住。
    /// </summary>
    [Theory]
    [MemberData(nameof(Files))]
    public void 交出來的幣別都對得到代碼(string file)
    {
        var s = Samples.Parse(file);
        var tokens = s.Transactions.Select(t => t.Currency).Concat(s.Balances.Select(b => b.Currency))
                                   .Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList();

        Assert.All(tokens, c => Assert.True(Resolvable(c), $"[{file}] 幣別「{c}」在 currency/aliases 裡對不到代碼"));
    }

    /// <summary>跟 StatementImporter.EnsureCurrency 同一個口徑：三尺碼大寫算代碼，其餘查 aliases。</summary>
    bool Resolvable(string token)
    {
        var text = token.Trim();
        if (text.Length == 3 && text.All(c => c >= 'A' && c <= 'Z'))
            return true;                                       // 會自動補 currency 列（有警告，但不會記錯錢）
        return Db.Query(_db, "SELECT aliases FROM currency WHERE aliases IS NOT NULL")
                 .Select(o => ((string)o!).Split('|'))
                 .Any(aliases => aliases.Any(a => a.Trim().Equals(text, StringComparison.OrdinalIgnoreCase)));
    }
}
