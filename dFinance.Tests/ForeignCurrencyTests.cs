using dFinance.Data;
using dFinance.Parsing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace dFinance.Tests;

/// <summary>
/// 「通用」的驗收：一個只有外幣、一筆台幣都沒有的資料庫也要能建帳、算淨值。
/// 基準幣、幣對匯率、一個帳戶一種幣別、依幣別的小數位 —— 這些以前都隱含假設台幣。
/// 不需要樣本 PDF：直接遞合成的 ParsedStatement。
/// </summary>
[Collection("DataVersion")]
public sealed class ForeignCurrencyTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"dfinance-fx-{Guid.NewGuid():N}.db");
    private readonly string _docsDir = Path.Combine(Path.GetTempPath(), $"dfinance-fx-docs-{Guid.NewGuid():N}");
    private readonly SqliteConnection _db;
    private readonly LedgerStore _store;

    public ForeignCurrencyTests()
    {
        _db = Db.Open(_dbPath);
        _store = new LedgerStore(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_dbPath)!, Path.GetFileName(_dbPath) + "*"))
            File.Delete(f);
        if (Directory.Exists(_docsDir)) Directory.Delete(_docsDir, true);
    }

    static LedgerStore.AccountEdit Edit(string name, string type, string currency, long opening = 0)
        => new(0, name, type, currency, null, null, opening, null, false, null, null, 0, null, null);

    ParsedStatement Statement(string mask, params (string Currency, decimal Amount, string Merchant)[] lines)
        => new()
        {
            Issuer = "兆豐國際商業銀行", Parser = "MegaDepositParser", AccountMask = mask,
            StatementEndDate = new DateOnly(2026, 10, 31),
            Transactions = lines.Select(l => new ParsedTransaction
            {
                Date = new DateOnly(2026, 10, 20), Amount = l.Amount, Merchant = l.Merchant,
                Currency = l.Currency, Account = mask, Page = 1, RawLine = $"{mask} {l.Merchant} {l.Amount}",
            }).ToList(),
        };

    // ---------------------------------------------------------------- 基準幣

    [Fact]
    public void 只有外幣帳戶的資料庫也算得出淨值()
    {
        _store.SetBaseCurrency("JPY");
        _store.CreateAccount(Edit("日元現金", "cash", "JPY", 100_000));                 // 1,000 JPY
        _store.CreateAccount(Edit("美金帳戶", "bank", "USD", 10_000));                  //   100 USD
        _store.SetRate("USD", 160m, "JPY");

        var nw = _store.NetWorth();

        Assert.Equal("JPY", nw.BaseCurrency);
        Assert.Equal(0, nw.UnconvertedAccounts);
        Assert.Equal(100_000 + 10_000 * 160, nw.AssetsMinor);                            // 換算只經 Money.Convert
    }

    [Fact]
    public void 缺匯率的幣別不計入淨值並講清楚缺哪一對()
    {
        _store.SetBaseCurrency("JPY");
        _store.CreateAccount(Edit("日元現金", "cash", "JPY", 100_000));
        _store.CreateAccount(Edit("美金帳戶", "bank", "USD", 10_000));                   // 沒抓 USD→JPY

        var nw = _store.NetWorth();

        Assert.Equal(100_000, nw.AssetsMinor);                                            // 寧可不計，不瞎算 1:1
        Assert.Equal(1, nw.UnconvertedAccounts);
        Assert.Equal(["USD → JPY"], nw.MissingPairs.ToArray());
    }

    [Fact]
    public void 匯率存的是幣對所以改基準幣不會弄髒舊數字()
    {
        _store.SetRate("USD", 32.9m);                                                     // 1 USD = 32.9 TWD
        Assert.Equal(32.9m, _store.Rates()["USD"]);

        _store.SetBaseCurrency("JPY");                                                    // 舊那列還在，也仍然是對台幣的
        Assert.Empty(_store.Rates());                                                     // 對日元查不到 → 不計入，不是拿台幣的價亂用
        Assert.Equal(32.9m, _store.RatesTo("TWD")["USD"]);

        _store.SetRate("USD", 160m, "JPY");
        Assert.Equal(160m, _store.Rates()["USD"]);
        Assert.Equal(32.9m, _store.RatesTo("TWD")["USD"]);
    }

    [Fact]
    public void 抓來的匯率蓋不掉人工填的()
    {
        _store.SetRate("USD", 31m);                                   // manual，今天
        _store.SetRate("USD", 99m, "TWD", "yahoo");                   // 同一天抓到的

        Assert.Equal(31m, _store.Rates()["USD"]);                     // 讀的時候 manual 優先
        Assert.Equal(2, _store.RateRows().Count(r => r.From == "USD"));   // 兩條都留著，來源分得開
    }

    // ---------------------------------------------------------------- 一個帳戶一種幣別

    [Fact]
    public void 同一帳號的不同幣別列會分到不同帳戶()
    {
        var result = new StatementImporter(_db, _docsDir).Import(
            Statement("20153085539", ("美金", 100m, "Ｆ匯入"), ("日圓", 500_000m, "網際轉")), "mega.pdf");

        Assert.Equal(2, result.Inserted);
        var accounts = _store.Accounts();
        Assert.Equal(["JPY", "USD"], accounts.Select(a => a.Currency).OrderBy(c => c).ToArray());
        Assert.All(accounts, a => Assert.Contains("20153085539", a.Name));              // 帳號沿用單子上的，只分幣別
        Assert.Equal(2L, Db.Scalar(_db, "SELECT count(DISTINCT account_id) FROM entry;"));
    }

    [Fact]
    public void 幣別與帳戶不同的舊列可以一键搬到對的帳戶()
    {
        var id = _store.CreateAccount(Edit("王道活存", "bank", "TWD"));
        _store.InsertManual(new LedgerStore.ManualEntry(id, "income", 311_550_00, "2026-11-01", null, null, null, null, "USD"));

        Assert.Equal(1, _store.SplitForeignCurrencyEntries());

        var home = _store.Accounts().Single(a => a.Currency == "USD");
        Assert.Equal(1, Count("AND account_id=$a", ("$a", home.Id)));
        Assert.Equal(0L, Db.Scalar(_db, """
            SELECT count(*) FROM entry e JOIN account a ON a.id = e.account_id
            WHERE e.deleted_at IS NULL AND e.currency <> a.currency;
            """));
    }

    int Count(string where, params (string, object?)[] ps)
        => Convert.ToInt32(Db.Scalar(_db, $"SELECT count(*) FROM entry WHERE deleted_at IS NULL {where};", ps));

    // ---------------------------------------------------------------- 幣別對映與顯示

    [Fact]
    public void 單子上的幣別原文靠別名對成代碼()
    {
        _store.SaveCurrency("EUR", "歐元", 2, "歐元|EU");

        new StatementImporter(_db, _docsDir).Import(
            Statement("02010660350", ("歐元", 100m, "海外消費"), ("新台幣", 300m, "薪資")), "euro.pdf");

        Assert.Equal(["EUR", "TWD"], _store.Accounts().Select(a => a.Currency).OrderBy(c => c).ToArray());
    }

    [Fact]
    public void 對不上代碼的幣別要用基準幣並警告()
    {
        var result = new StatementImporter(_db, _docsDir).Import(
            Statement("02010660350", ("囧幣", 100m, "不明交易")), "weird.pdf");

        Assert.Equal("TWD", _store.Accounts().Single().Currency);                     // 兜底用基準幣
        Assert.Contains(result.Warnings!, w => w.Contains("囧幣") && w.Contains("幣別"));
    }

    [Fact]
    public void 顯示小數位跟著幣別走()
    {
        _store.CreateAccount(Edit("台幣帳戶", "bank", "TWD", 123_45));                  // 123.45 台幣
        _store.CreateAccount(Edit("美金帳戶", "bank", "USD", 123_45));                  // 123.45 美金
        _store.SetBaseCurrency("TWD");

        var byName = _store.NetWorth().Accounts.ToDictionary(a => a.Name, a => a);
        Assert.Equal("123", byName["台幣帳戶"].BalanceText);                             // TWD scale = 0
        Assert.Equal("123.45", byName["美金帳戶"].BalanceText);                          // USD scale = 2

        var list = _store.AccountsAll().ToDictionary(a => a.Name, a => a.ListLabel);
        Assert.Contains("123.45", list["美金帳戶"]);
    }
}
