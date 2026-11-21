using dFinance.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace dFinance.Tests;

/// <summary>
/// 帳戶管理的測試。這裡鎖的是「錢的來源」：期初餘額、卡的額度/自動扣繳、人工盤點。
/// 這些一旦寫錯，淨值與餘額校驗會一起歪，而且歪得很安靜 —— 所以每條都要有斷言。
/// </summary>
[Collection("DataVersion")]
public sealed class AccountTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"dfinance-acct-{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _db;
    private readonly LedgerStore _store;

    public AccountTests()
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
    }

    static LedgerStore.AccountEdit Edit(long id = 0, string name = "測試現金", string type = "cash",
                                        string currency = "TWD", long opening = 0, string? date = null,
                                        long? limit = null, long? autopay = null)
        => new(id, name, type, currency, null, null, opening, date, false, limit, autopay, 0, null, null);

    long Manual(long accountId, string kind, long minor, string date = "2026-11-01", string currency = "TWD")
        => _store.InsertManual(new LedgerStore.ManualEntry(accountId, kind, minor, date, null, null, null, null, currency));

    /// <summary>舊庫的真實形狀：台幣帳戶裡混了美金列（那時候還沒有一帳戶一幣別，parser 猜錯就塞進去）。
    /// InsertManual 現在會擋這種列（那就是 1,250,000 日圓被當台幣的來源），所以舊資料只能照 SQL 塞。</summary>
    void LegacyForeignEntry(long accountId, string currency, long minor, string merchant = "Ｆ匯入")
        => Db.Exec(_db, """
            INSERT INTO entry(kind, account_id, currency, amount_minor, txn_date, merchant, status, source, created_at)
            VALUES('income', $a, $cur, $amt, '2026-11-01', $m, 'imported', 'import', datetime('now'));
            """, ("$a", accountId), ("$cur", currency), ("$amt", minor), ("$m", merchant));

    // ---------------------------------------------------------------- 建與改

    [Fact]
    public void 同名帳戶擋下來講人話含歸檔中的()
    {
        var id = _store.CreateAccount(Edit(name: "牛皮夾"));
        _store.SetArchived(id, true);

        var ex = Assert.Throws<ArgumentException>(() => _store.CreateAccount(Edit(name: "牛皮夾")));
        Assert.Contains("牛皮夾", ex.Message);                                  // 不是 SqliteException 那種 UNIQUE failed
    }

    [Fact]
    public void 不認識的型別與壞日期擋在資料層()
    {
        Assert.Throws<ArgumentException>(() => _store.CreateAccount(Edit(type: "savings")));
        Assert.Throws<ArgumentException>(() => _store.CreateAccount(Edit(date: "112/11/01")));   // 民國年
        Assert.Throws<ArgumentException>(() => _store.CreateAccount(Edit(name: "   ")));
    }

    [Fact]
    public void 建現金帳戶的期初餘額會進淨值歸檔後又不進()
    {
        var id = _store.CreateAccount(Edit(name: "牛皮夾", opening: 500_000));
        Assert.Equal(500_000, _store.DerivedBalance(id));
        Assert.Equal(500_000, _store.NetWorth().Accounts.Single(a => a.Name == "牛皮夾").BalanceMinor);

        _store.SetArchived(id, true);
        Assert.DoesNotContain(_store.NetWorth().Accounts, a => a.Name == "牛皮夾");   // 歸檔 = 不計入淨值
        Assert.Contains(_store.AccountsAll(), a => a.Name == "牛皮夾" && a.IsArchived); // 但清單要看得到，才改得回來
    }

    [Fact]
    public void 改期初餘額會移動推導出来的餘額()
    {
        var id = _store.CreateAccount(Edit(name: "活存", type: "bank", opening: 100_000));
        Manual(id, "expense", 30_000);
        Assert.Equal(70_000, _store.DerivedBalance(id));

        var a = _store.GetAccount(id)!;
        _store.UpdateAccount(a with { OpeningBalanceMinor = 200_000 });
        Assert.Equal(170_000, _store.DerivedBalance(id));
    }

    // ---------------------------------------------------------------- 卡的兩個欄位

    [Fact]
    public void 信用額度與自動扣繳存得進去而且不會插出第二張卡()
    {
        var bank = _store.CreateAccount(Edit(name: "活存", type: "bank"));
        var card = _store.CreateAccount(Edit(name: "聯邦卡", type: "credit_card", limit: 100_000, autopay: bank));

        Assert.Equal(100_000, _store.GetAccount(card)!.CreditLimitMinor);
        Assert.Equal(bank, _store.GetAccount(card)!.AutopayAccountId);

        _store.UpdateAccount(_store.GetAccount(card)! with { CreditLimitMinor = 200_000 });   // 重存是改不是再插一筆
        Assert.Equal(200_000, _store.GetAccount(card)!.CreditLimitMinor);
        Assert.Equal(1L, Db.Scalar(_db, "SELECT count(*) FROM card WHERE account_id=$a;", ("$a", card)));
    }

    [Fact]
    public void 自動扣繳不能指到自己或不存在的帳戶()
    {
        var card = _store.CreateAccount(Edit(name: "卡", type: "credit_card"));
        Assert.Throws<ArgumentException>(() => _store.UpdateAccount(_store.GetAccount(card)! with { AutopayAccountId = card }));
        Assert.Throws<ArgumentException>(() => _store.UpdateAccount(_store.GetAccount(card)! with { AutopayAccountId = 9999 }));
    }

    // ---------------------------------------------------------------- 盤點與餘額校驗

    [Fact]
    public void 人工盤點是餘額校驗的輸入對不上會被揪出來()
    {
        var id = _store.CreateAccount(Edit(name: "活存", type: "bank"));
        Manual(id, "expense", 1_000);                                  // 交易累加 = -10.00

        _store.AddBalanceSnapshot(id, "2026-11-10", "TWD", 500_000);   // 人說有 5,000

        var m = Assert.Single(_store.BalanceMismatches());
        Assert.Equal("活存", m.Account);
        Assert.Equal(501_000, m.DiffMinor);                             // 快照比交易累加多 5,010
        Assert.Equal("manual", m.Source);                               // 沒有 parser 也能校驗 —— 這才是這條的意義

        // 順手補差額（UI 的「記一筆調整」就是這個），補完就不該再報
        _store.InsertManual(new LedgerStore.ManualEntry(id, "adjustment", m.DiffMinor, "2026-11-10", null, null, null, "盤點調整"));
        Assert.Empty(_store.BalanceMismatches());
    }

    [Fact]
    public void 餘額校驗只比快照那天以前的交易()
    {
        // 兆豐那份的形狀：10 月的單子同時印了 09/10 月底結算餘額（期初）與 10/31 的帳戶總覽（期末），
        // 兩個快照比的是「區間差」，所以結算日之後的交易不准算進這段區間（算了就會報多一筆）。
        var id = _store.CreateAccount(Edit(name: "活存", type: "bank"));
        Manual(id, "expense", 1_000, "2026-10-05");                    // 區間內：要算
        Manual(id, "expense", 5_000, "2026-11-20");                    // 期末快照之後：不准算

        Db.Exec(_db, """
            INSERT INTO account_balance(account_id, as_of, currency, balance_minor, source) VALUES
                ($a, '2026-09-30', 'TWD', 0, 'statement'),
                ($a, '2026-10-31', 'TWD', -1_000, 'statement');
            """, ("$a", id));
        Assert.Empty(_store.BalanceMismatches());                       // 區間差 -1,000，對得上（11/20 那筆不准算進來）

        Db.Exec(_db, "UPDATE account_balance SET balance_minor = balance_minor + 3_000 WHERE as_of = '2026-10-31';");
        var bad = Assert.Single(_store.BalanceMismatches());
        Assert.Equal(3_000, bad.DiffMinor);                             // 單上的區間差 +2,000 vs 帳上 -1,000
        Assert.Equal("2026-09-30 → 2026-10-31", bad.AsOf);              // 要講得出去翻哪一期單子
    }

    [Fact]
    public void 沒填過期初的帳戶_單上只有一個期末餘額就不拿去比()
    {
        // 匯入自動建的帳戶不知道開戶以來的歷史。拿單上印的期末餘額去跟「期初 0 + 交易累加」比會天天誤報
        // （舊口徑就是這樣：王道那種差 1 元的假性紅是這麼來的）。
        var id = _store.CreateAccount(Edit(name: "活存", type: "bank"));
        Manual(id, "expense", 1_000, "2026-10-05");
        Db.Exec(_db, "INSERT INTO account_balance(account_id, as_of, currency, balance_minor, source) VALUES($a, '2026-10-31', 'TWD', 500_000, 'statement');",
                ("$a", id));
        Assert.Empty(_store.BalanceMismatches());                       // 不比 ≠ 對得上：沒有起點就老實跳過

        // 同一帳戶只要多一個更早的快照，區間內少讀一筆就會被抓出來（這才是抓漏解析的主力）。
        Db.Exec(_db, "INSERT INTO account_balance(account_id, as_of, currency, balance_minor, source) VALUES($a, '2026-09-30', 'TWD', 0, 'statement');",
                ("$a", id));
        var bad = Assert.Single(_store.BalanceMismatches());
        Assert.Equal(501_000, bad.DiffMinor);                            // 單上說這段進帳 501,000，帳上只有 -1,000
        Assert.Equal("2026-09-30 → 2026-10-31", bad.AsOf);
    }

    [Fact]
    public void 人填過期初之後_第一個單上餘額就開始比()
    {
        // 起點是人在「帳戶…」填的期初（或人工盤點），填了就有得比 —— 第一個快照也要比。
        var id = _store.CreateAccount(Edit(name: "活存", type: "bank", opening: 10_000, date: "2026-01-01"));
        Manual(id, "expense", 3_000, "2026-11-01");
        Db.Exec(_db, "INSERT INTO account_balance(account_id, as_of, currency, balance_minor, source) VALUES($a, '2026-11-30', 'TWD', 6_000, 'statement');",
                ("$a", id));
        var bad = Assert.Single(_store.BalanceMismatches());             // 10,000 - 3,000 = 7,000，單上說 6,000
        Assert.Equal(-1_000, bad.DiffMinor);
        Assert.Equal("2026-11-30", bad.AsOf);
    }

    [Fact]
    public void 同一天重覆盤點是蓋掉不是多一筆()
    {
        var id = _store.CreateAccount(Edit(name: "活存", type: "bank", opening: 100_000));
        _store.AddBalanceSnapshot(id, "2026-11-10", "TWD", 100_000);
        _store.AddBalanceSnapshot(id, "2026-11-10", "TWD", 120_000);

        Assert.Equal(1L, Db.Scalar(_db, "SELECT count(*) FROM account_balance;"));
        Assert.Equal(120_000, _store.GetAccount(id)!.ManualBalanceMinor);
    }

    // ---------------------------------------------------------------- 匯率

    [Fact]
    public void 匯率改得掉留空就不計入淨值()
    {
        _store.SetRate("JPY", 0.2m);
        var id = _store.CreateAccount(Edit(name: "日圓定存", type: "term_deposit", currency: "JPY", opening: 100_000));
        Assert.Equal(20_000, _store.NetWorth().Accounts.Single(a => a.Name == "日圓定存").ConvertedMinor);

        _store.SetRate("JPY", null);                                    // 留空 = 故意不折算
        Assert.False(_store.NetWorth().Accounts.Single(a => a.Name == "日圓定存").CountedInNet);
        Assert.Equal(1, _store.NetWorth().UnconvertedAccounts);
    }

    [Fact]
    public void 幣別與帳戶不同的列要在總表上標出來()
    {
        // 王道那份的真實情況：台幣帳戶裡混了美金列，金額是照台幣算進餘額的，不能讓人當准數
        var id = _store.CreateAccount(Edit(name: "王道活存", type: "bank"));
        LegacyForeignEntry(id, "USD", 311_550_00);

        Assert.Equal(1, _store.NetWorth().Accounts.Single(a => a.Name == "王道活存").ForeignEntries);
        Assert.Contains("幣別不符", _store.NetWorth().Accounts.Single(a => a.Name == "王道活存").CountedText);
    }
}

/// <summary>
/// LedgerStore.DataVersion 是靜態的，而 xUnit 預設不同 class 平行跑 ——
/// 兩個都會 Touch 它的 class 排同一條隊，否則「沒人寫過就版本不會變」那条断言会随机红。
/// </summary>
[CollectionDefinition("DataVersion")]
public sealed class DataVersionCollection;
