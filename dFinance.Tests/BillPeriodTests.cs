using dFinance.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace dFinance.Tests;

/// <summary>
/// 「依帳期彙總」的口徑。這張表是用來跟紙本帳單對帳的，算錯不會有紅字、只會看起來很正常：
/// 消費合計 ≠ 單上應繳 要嘛是 parser 漏列、要嘛是這裡算錯，兩者要分得開，所以逐條鎖住。
/// </summary>
[Collection("DataVersion")]
public sealed class BillPeriodTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"dfinance-billperiod-{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _db;
    private readonly LedgerStore _store;

    public BillPeriodTests()
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

    // ---------------------------------------------------------------- 蓋資料的小工具

    long Account(string name, string type = "credit_card", string currency = "TWD")
        => Db.InsertId(_db, """
            INSERT INTO account(name, type, currency, opening_balance_minor, created_at) VALUES($n, $t, $c, 0, $at);
            """, ("$n", name), ("$t", type), ("$c", currency), ("$at", Db.Now()));

    long Card(long accountId, string mask = "1234")
        => Db.InsertId(_db, "INSERT INTO card(account_id, mask) VALUES($a, $m);", ("$a", accountId), ("$m", mask));

    long Bill(long cardId, string periodEnd, string? periodStart = "2026-10-01", string? dueDate = "2026-11-15",
              long? totalDueMinor = null, string? paidAt = null)
        => Db.InsertId(_db, """
            INSERT INTO credit_card_bill(card_id, period_start, period_end, due_date, total_due_minor, paid_at)
            VALUES($c, $ps, $pe, $d, $td, $pa);
            """, ("$c", cardId), ("$ps", (object?)periodStart ?? DBNull.Value), ("$pe", periodEnd),
            ("$d", (object?)dueDate ?? DBNull.Value), ("$td", (object?)totalDueMinor ?? DBNull.Value),
            ("$pa", (object?)paidAt ?? DBNull.Value));

    long Entry(string kind, long accountId, long amountMinor, long? billId = null, string status = "cleared",
               string currency = "TWD", long? toAccountId = null, bool deleted = false)
        => Db.InsertId(_db, """
            INSERT INTO entry(kind, account_id, to_account_id, bill_id, currency, amount_minor, txn_date,
                              merchant, status, source, source_line_hash, deleted_at, created_at)
            VALUES($kind, $acct, $to, $bill, $cur, $amt, '2026-10-05', 'm', $status, 'import', $hash, $del, $at);
            """, ("$kind", kind), ("$acct", accountId),
            ("$to", (object?)toAccountId ?? DBNull.Value),
            ("$bill", (object?)billId ?? DBNull.Value), ("$cur", currency), ("$amt", amountMinor),
            ("$status", status), ("$hash", Guid.NewGuid().ToString("N")),
            ("$del", deleted ? Db.Now() : (object)DBNull.Value), ("$at", Db.Now()));

    // ---------------------------------------------------------------- 消費合計怎麼加

    [Fact]
    public void 一列一期_消費合計排除繳款但含負額退款()
    {
        var card = Account("富邦卡");
        var bank = Account("活存", "bank");
        var k = Card(card);
        var bill = Bill(k, "2026-10-31", totalDueMinor: 3_000_00);
        Entry("expense", card, 2_000_00, bill);
        Entry("expense", card, 1_500_00, bill);
        Entry("refund", card, -500_00, bill);                       // 退款在 DB 裡是負的 → 直接相加就是淨消費
        Entry("transfer", card, 3_000_00, bill, toAccountId: card); // 繳款：不算消費，另進「繳款」欄

        var r = Assert.Single(_store.BillPeriods().Rows);
        Assert.Equal(3_000_00, r.SpendMinor);                       // 2,000 + 1,500 − 500
        Assert.Equal(3, r.Entries);                                 // 筆數跟消費合計是同一批列（繳款不計）
        Assert.Equal(3_000_00, r.TransferMinor);
        Assert.Equal(3_000_00, r.TotalDueMinor);
        Assert.Equal(0, r.DiffMinor);
        Assert.False(r.Mismatch);
        Assert.Equal("—", r.DiffText);                              // 0 是「對上了」，不是少一筆
        Assert.Equal("2026-10-01 ~ 2026-10-31", r.PeriodText);
    }

    [Fact]
    public void pending與deleted的列不進消費合計()
    {
        var card = Account("富邦卡");
        var k = Card(card);
        var bill = Bill(k, "2026-10-31", totalDueMinor: 700_00);
        Entry("expense", card, 1_000_00, bill, status: "pending");   // 还没確認的暫存列：不進（Live 口徑）
        Entry("expense", card, 2_000_00, bill, deleted: true);       // 軟刪除的也不進
        Entry("expense", card, 700_00, bill);

        var r = Assert.Single(_store.BillPeriods().Rows);
        Assert.Equal(700_00, r.SpendMinor);
        Assert.Equal(1, r.Entries);
        Assert.False(r.Mismatch);
    }

    [Fact]
    public void 單上沒印應繳時差額留白也不算對不上()
    {
        var card = Account("富邦卡");
        var k = Card(card);
        var bill = Bill(k, "2026-10-31", totalDueMinor: null);       // 那份單子沒這欄
        Entry("expense", card, 1_000_00, bill);

        var r = Assert.Single(_store.BillPeriods().Rows);
        Assert.Null(r.TotalDueMinor);
        Assert.Null(r.DiffMinor);                                   // 不准把沒印當成 0（那就變成人身上的 1,000 元漏列）
        Assert.Equal("", r.DiffText);
        Assert.False(r.Mismatch);
    }

    [Fact]
    public void 對不上的期要標出來差額是消費減應繳()
    {
        var card = Account("富邦卡");
        var k = Card(card);
        var bill = Bill(k, "2026-10-31", totalDueMinor: 3_000_00);
        Entry("expense", card, 2_000_00, bill);                      // 少解析 1,000

        var r = Assert.Single(_store.BillPeriods().Rows);
        Assert.True(r.Mismatch);
        Assert.Equal(-1_000_00, r.DiffMinor);
        Assert.Equal("-1,000", r.DiffText);
    }

    // ---------------------------------------------------------------- 掛不上帳單的列

    [Fact]
    public void 掛不上帳單的卡帳列要看得见也不進合計()
    {
        var card = Account("富邦卡");
        var k = Card(card);
        var bill = Bill(k, "2026-10-31", totalDueMinor: 1_000_00);
        Entry("expense", card, 1_000_00, bill);
        Entry("expense", card, 500_00, billId: null);                // 卡號被遮 → 掛不上任何帳期

        var report = _store.BillPeriods();
        Assert.Equal(1, report.UnmappedEntries);
        Assert.Equal(1, report.Rows.Count(x => x.Unmapped));         // 列裡看得到（不靜靜吃掉）
        Assert.Equal(1_000_00, report.TotalSpendMinor);              // 那 500 不進合計：沒有單可對
    }

    // ---------------------------------------------------------------- 幣別

    [Fact]
    public void 缺匯率的期不進合計列但要標出來()
    {
        var card = Account("日圓卡", currency: "JPY");                // 故意沒有 JPY→TWD 匯率（0003 只预塞 USD）
        var k = Card(card);
        var bill = Bill(k, "2026-10-31", totalDueMinor: 10_000);
        Entry("expense", card, 9_000, bill, currency: "JPY");

        var report = _store.BillPeriods();
        var r = Assert.Single(report.Rows);
        Assert.True(r.NeedsRate);
        Assert.Equal(9_000, r.SpendMinor);                           // 列上照單上的幣別（要能跟紙本對）
        Assert.Equal("90", r.SpendText);                             // JPY 的 scale=0
        Assert.Equal(0, report.TotalSpendMinor);                     // 不知道就是不知道，不准假設 1:1
        Assert.Equal(1, report.UnconvertedBills);
        Assert.Equal("JPY", r.Currency);
    }

    [Fact]
    public void 有匯率的期折算後才進合計列()
    {
        var card = Account("美金卡", currency: "USD");
        var k = Card(card);
        var bill = Bill(k, "2026-10-31", totalDueMinor: 100_00);
        Entry("expense", card, 90_00, bill, currency: "USD");
        _store.SetRate("USD", 30m);

        var report = _store.BillPeriods();
        var r = Assert.Single(report.Rows);
        Assert.False(r.NeedsRate);
        Assert.Equal(90_00, r.SpendMinor);                           // 列上仍是原幣
        Assert.Equal(270_000, report.TotalSpendMinor);               // 90 USD × 30 = 2,700 元
        Assert.Equal(300_000, report.TotalDueMinor);
        Assert.Equal(-30_000, report.TotalDiffMinor);
        Assert.Equal("TWD", report.BaseCurrency);
    }

    // ---------------------------------------------------------------- 篩選

    [Fact]
    public void 近N期是每張卡各留最近幾期()
    {
        var card = Account("富邦卡");
        var k = Card(card);
        for (int m = 9; m <= 11; m++) Bill(k, $"2026-{m:00}-28", periodStart: null, dueDate: null, totalDueMinor: 100);

        var two = _store.BillPeriods(periodsPerCard: 2);
        Assert.Equal(2, two.Rows.Count);
        Assert.Equal(["2026-10-28", "2026-11-28"], two.Rows.Select(r => r.PeriodEnd).OrderBy(x => x).ToArray());
        Assert.Equal(3, _store.BillPeriods(periodsPerCard: 0).Rows.Count);
    }

    [Fact]
    public void 只看對不上時會留下缺匯率的期()
    {
        var twd = Account("富邦卡");
        var k = Card(twd);
        var ok = Bill(k, "2026-10-31", totalDueMinor: 1_000_00);
        Entry("expense", twd, 1_000_00, ok);                          // 對得上 → 會被濾掉
        var bad = Bill(k, "2026-09-30", totalDueMinor: 2_000_00);
        Entry("expense", twd, 1_500_00, bad);                         // 差 500 → 留

        var jpy = Account("日圓卡", currency: "JPY");                 // 沒匯率：說不上對不對 → 也要留
        var ku = Card(jpy);
        var foreign = Bill(ku, "2026-10-31", totalDueMinor: 10_000);
        Entry("expense", jpy, 10_000, foreign, currency: "JPY");
        Entry("expense", twd, 300_00, billId: null);                // 掛不到單：也是要先處理的 → 不能藏起來（footer 在講的就是它）

        var report = _store.BillPeriods(onlyMismatched: true);
        var rows = report.Rows;
        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r.Mismatch && r.DiffMinor == -500_00);
        Assert.Contains(rows, r => r.NeedsRate);
        Assert.Contains(rows, r => r.Unmapped);
        Assert.Equal(1, report.UnmappedEntries);
        Assert.DoesNotContain(rows, r => !r.Unmapped && r.Currency == "TWD" && r.DiffMinor == 0);   // 對得上的台幣那期被濾掉了
    }

    [Fact]
    public void 繳銷看paid_at而不只是看有繳款列()
    {
        var card = Account("富邦卡");
        var k = Card(card);
        var bill = Bill(k, "2026-10-31", totalDueMinor: 1_000_00);
        Entry("expense", card, 1_000_00, bill);
        Assert.False(Assert.Single(_store.BillPeriods().Rows).Paid);

        _store.MarkBillPaid(bill);
        var paid = Assert.Single(_store.BillPeriods().Rows);
        Assert.True(paid.Paid);
        Assert.NotEqual("", paid.PaidText);

        _store.MarkBillPaid(bill, paid: false);
        Assert.False(Assert.Single(_store.BillPeriods().Rows).Paid);
    }

    [Fact]
    public void 同期有別種幣別的列_應繳不准算兩次_差額也不准跨幣別相減()
    {
        var card = Account("富邦卡");
        var k = Card(card);
        var bill = Bill(k, "2026-10-31", totalDueMinor: 100_000);      // 單上應繳 1,000 元
        Entry("expense", card, 60_000, bill);                          // 台幣 600
        Entry("expense", card, 30_000, bill, currency: "USD");         // 外幣列挂在台幣卡上（舊庫那種）

        var r = _store.BillPeriods();
        Assert.Equal(100_000, r.TotalDueMinor);                        // 一份帳單的應繳只算一次
        Assert.Equal(60_000, r.TotalSpendMinor);                       // 幣別不符的不算进這期（不同幣別不准直接加）
        var row = Assert.Single(r.Rows.Where(x => x.BillId != 0));
        Assert.Equal(-40_000, row.DiffMinor);                          // 600 − 1000：同幣別才准相減
        var foreign = Assert.Single(r.Rows.Where(x => x.Foreign));
        Assert.Equal(30_000, foreign.SpendMinor);
        Assert.Equal("USD", foreign.Currency);
        Assert.Equal(1, r.UnmappedEntries);                            // 要看得见：這筆沒算进任何帳期
    }

    [Fact]
    public void 拆帳戶把外幣列搬走之後_也不會回頭把該期應繳算兩次()
    {
        var card = Account("富邦卡");
        var k = Card(card);
        var bill = Bill(k, "2026-10-31", totalDueMinor: 100_000);
        Entry("expense", card, 60_000, bill);
        Entry("expense", card, 30_000, bill, currency: "USD");
        Assert.Equal(1, _store.SplitForeignCurrencyEntries());         // 工具→幣別… 那顆按鈕（bill_id 留著）

        var r = _store.BillPeriods();
        Assert.Equal(100_000, r.TotalDueMinor);
        Assert.Equal(60_000, r.TotalSpendMinor);
        Assert.Contains(r.Rows, x => x.Foreign && x.Currency == "USD");
    }

    [Fact]
    public void 存款單與繳費單不進這張表()
    {
        var bank = Account("活存", "bank");
        Entry("expense", bank, 1_000_00);                            // 沒有帳期這個概念
        Entry("expense", bank, 500_00);
        var report = _store.BillPeriods();
        Assert.Empty(report.Rows);
        Assert.Equal(0, report.UnmappedEntries);
    }
}
