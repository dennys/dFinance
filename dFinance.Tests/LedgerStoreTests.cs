using dFinance.Data;
using dFinance.Data.Localization;
using Microsoft.Data.Sqlite;
using Xunit;

namespace dFinance.Tests;

/// <summary>
/// 帳本清單與概況的測試。重點是「報表口徑」：哪些列算數、錢怎麼加總。
/// 這些規則一旦在 UI 上被繞過就是 quietly 記錯帳，所以用測試鎖住。
/// </summary>
[Collection("DataVersion")]
public sealed class LedgerStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"dfinance-ledger-{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _db;
    private readonly LedgerStore _store;

    public LedgerStoreTests()
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

    long Account(string name, string type, string currency = "TWD", long openingMinor = 0)
        => Db.InsertId(_db,
            "INSERT INTO account(name, type, currency, opening_balance_minor, created_at) VALUES($n, $t, $c, $o, $at);",
            ("$n", name), ("$t", type), ("$c", currency), ("$o", openingMinor), ("$at", Db.Now()));

    long Doc(string name)
        => Db.InsertId(_db,
            "INSERT INTO document(sha256, file_path, file_name, imported_at, parser, parser_ver) VALUES($s, $p, $f, $t, 'TestParser', 1);",
            ("$s", Guid.NewGuid().ToString("N")), ("$p", name), ("$f", name), ("$t", Db.Now()));

    long Entry(string kind, long accountId, long amountMinor, string? date, string status = "cleared",
               string? merchant = null, long? toAccountId = null, long? docId = null, string? currency = "TWD",
               long? categoryId = null, string? orphanedAt = null)
        => Db.InsertId(_db, """
            INSERT INTO entry(kind, account_id, to_account_id, category_id, currency, amount_minor,
                              txn_date, merchant, status, source, source_document_id, source_line_hash, orphaned_at, created_at)
            VALUES($kind, $acct, $to, $cat, $cur, $amt, $date, $m, $status, 'import', $doc, $hash, $orphan, $at);
            """,
            ("$kind", kind), ("$acct", accountId),
            ("$to", (object?)toAccountId ?? DBNull.Value),
            ("$cat", (object?)categoryId ?? DBNull.Value),
            ("$cur", currency ?? "TWD"), ("$amt", amountMinor),
            ("$date", (object?)date ?? DBNull.Value), ("$m", (object?)merchant ?? DBNull.Value),
            ("$status", status), ("$doc", (object?)docId ?? DBNull.Value),
            ("$hash", Guid.NewGuid().ToString("N")), ("$at", Db.Now()),
            ("$orphan", (object?)orphanedAt ?? DBNull.Value));

    // ---------------------------------------------------------------- 報表鐵律

    [Fact]
    public void 清單和概況只算_cleared_與_imported()
    {
        var acct = Account("測試現金", "cash");
        Entry("expense", acct, 1000, "2026-11-01", status: "cleared");
        Entry("expense", acct, 2000, "2026-11-02", status: "imported");
        Entry("expense", acct, 4000, "2026-11-03", status: "pending");     // 尚未發生
        Entry("expense", acct, 8000, "2026-11-04", status: "ignored");     // 使用者說不記
        Entry("expense", acct, 16000, "2026-11-05", status: "duplicate");

        var rows = _store.List(new LedgerStore.Filter { Status = "ledger" });
        Assert.Equal(2, rows.Count);
        Assert.Equal(3000L, _store.Totals("2026-11-01", "2026-11-30", null).ExpenseMinor);

        // 忘了過濾就會把未發生的款項算進支出 —— 這條就是為了抓住那個
        Assert.DoesNotContain(rows, r => r.Status is "pending" or "ignored" or "duplicate");
    }

    [Fact]
    public void 軟刪除的列看不到還原後才回來()
    {
        var acct = Account("測試現金", "cash");
        var id = Entry("expense", acct, 500, "2026-11-01");

        _store.SoftDelete(id);
        Assert.Empty(_store.List(new LedgerStore.Filter()));
        Assert.Single(_store.List(new LedgerStore.Filter { Status = "deleted" }));

        _store.Restore(id);
        Assert.Single(_store.List(new LedgerStore.Filter()));
    }

    [Fact]
    public void 轉帳不算收支但會移動兩個帳戶的餘額()
    {
        var bank = Account("聯邦活存", "bank", openingMinor: 100_000);
        var card = Account("聯邦信用卡", "credit_card", openingMinor: 0);   // 卡的期初是 0，欠錢靠交易累成負數
        Entry("transfer", bank, 30_000, "2026-11-05", toAccountId: card);

        var k = _store.Totals("2026-11-01", "2026-11-30", null);
        Assert.Equal(0, k.IncomeMinor);                       // 繳款若被當成支出就會雙倍，這裡必須是 0
        Assert.Equal(0, k.ExpenseMinor);

        var nw = _store.NetWorth();
        Assert.Equal(70_000, nw.Accounts.Single(a => a.Name == "聯邦活存").BalanceMinor);
        Assert.Equal(30_000, nw.Accounts.Single(a => a.Name == "聯邦信用卡").BalanceMinor);   // 繳進去 → 卡餘額往零走
        Assert.Equal(100_000, nw.NetMinor);                   // 左口袋進右口袋，淨值不變
    }

    // ---------------------------------------------------------------- 概況

    [Fact]
    public void 外幣帳戶有匯率才計入淨值()
    {
        Account("台幣活存", "bank", "TWD", 50_000);
        Account("美元定存", "term_deposit", "USD", 100_000);            // 1,000 美元，遷移 seed 了 32.9
        Account("日圓定存", "term_deposit", "JPY", 999_999);            // 沒匯率 → 寧可不計

        var nw = _store.NetWorth();
        Assert.Equal(1, nw.UnconvertedAccounts);                        // 只有日圓那個
        Assert.Equal(50_000 + 3_290_000, nw.AssetsMinor);               // 1,000 × 32.9
        Assert.Equal(50_000 + 3_290_000, nw.NetMinor);
        Assert.Equal(3_290_000, nw.Accounts.Single(a => a.Name == "美元定存").ConvertedMinor);
        Assert.False(nw.Accounts.Single(a => a.Name == "日圓定存").CountedInNet);
    }

    [Fact]
    public void 負債是負餘額的帳戶合計()
    {
        var bank = Account("活存", "bank", openingMinor: 80_000);
        var card = Account("卡", "credit_card");
        Entry("expense", card, 30_000, "2026-11-01");

        var nw = _store.NetWorth();
        Assert.Equal(80_000, nw.AssetsMinor);
        Assert.Equal(-30_000, nw.LiabilitiesMinor);
        Assert.Equal(50_000, nw.NetMinor);
    }

    [Fact]
    public void 卡未繳看帳單表而且繳過的不再計()
    {
        var card = Account("卡", "credit_card");
        var cardId = Db.InsertId(_db, "INSERT INTO card(account_id, mask) VALUES($a, '1234');", ("$a", card));
        Db.Exec(_db, "INSERT INTO credit_card_bill(card_id, period_end, total_due_minor, due_date) VALUES($c, '2026-10-31', 5000, '2026-11-15');", ("$c", cardId));
        Db.Exec(_db, "INSERT INTO credit_card_bill(card_id, period_end, total_due_minor, due_date, paid_at, paid_minor) VALUES($c, '2026-09-30', 7000, '2026-10-15', $t, 7000);", ("$c", cardId), ("$t", Db.Now()));

        Assert.Equal(5000L, _store.CardUnpaidMinor());
    }

    [Fact]
    public void 每月總資產用月底餘額累加()
    {
        var bank = Account("活存", "bank", openingMinor: 100_000);
        Entry("expense", bank, 30_000, "2026-10-05");
        Entry("income", bank, 50_000, "2026-11-05");

        var points = _store.MonthlyNetWorth(new DateOnly(2026, 12, 31));
        Assert.Equal(new DateOnly(2026, 10, 1), points[0].Month);
        Assert.Equal(70_000, points[0].NetMinor);       // 10 月：100,000 − 30,000
        Assert.Equal(120_000, points[1].NetMinor);      // 11 月：+50,000
        Assert.Equal(120_000, points[2].NetMinor);      // 12 月沒有新交易，餘額不變
    }

    [Fact]
    public void 未來帳單含未到期的列與未繳卡費()
    {
        var bank = Account("活存", "bank");
        Entry("expense", bank, 11_110, "2999-01-15", merchant: "電費");
        Entry("expense", bank, 22_220, "2999-01-16", status: "ignored");   // 使用者說不記 → 不列
        Entry("expense", bank, 3_333, "2000-01-01");                      // 過去的 → 不列

        var card = Account("卡", "credit_card");
        var cardId = Db.InsertId(_db, "INSERT INTO card(account_id, mask) VALUES($a, '1234');", ("$a", card));
        Db.Exec(_db, "INSERT INTO credit_card_bill(card_id, period_end, total_due_minor, due_date) VALUES($c, '2999-01-31', 5000, '2999-02-10');", ("$c", cardId));
        Db.Exec(_db, "INSERT INTO credit_card_bill(card_id, period_end, total_due_minor, due_date, paid_at, paid_minor) VALUES($c, '2999-02-28', 7000, '2999-03-10', $t, 7000);", ("$c", cardId), ("$t", Db.Now()));

        var list = _store.Upcoming();
        Assert.Equal(2, list.Count);
        Assert.Equal(111.10m, list[0].Amount);                          // 先到期的排前面
        Assert.StartsWith("電費", list[0].Label);
        Assert.StartsWith("信用卡帳單", list[1].Label);
        Assert.Equal(50m, list[1].Amount);
    }

    [Fact]
    public void 餘額快照對不上時會點名帳戶()
    {
        var bank = Account("活存", "bank", openingMinor: 10_000);
        Entry("expense", bank, 3_000, "2026-11-01");
        Assert.Empty(_store.BalanceMismatches());                     // 還沒有快照 → 不該亂嗆

        Db.Exec(_db, "INSERT INTO account_balance(account_id, as_of, currency, balance_minor, source) VALUES($a, '2026-11-30', 'TWD', 7_000, 'statement');", ("$a", bank));
        Assert.Empty(_store.BalanceMismatches());                     // 10,000 − 3,000 = 7,000，對得上

        Db.Exec(_db, "UPDATE account_balance SET balance_minor = 9_000;");
        var bad = Assert.Single(_store.BalanceMismatches());          // 漏解析的偵測器就在這裡
        Assert.Equal("活存", bad.Account);
        Assert.Equal(2_000, bad.DiffMinor);
    }

    [Fact]
    public void 疑似重複只跨檔案比信用卡不亂標存款單()
    {
        var card = Account("卡", "credit_card");
        var bank = Account("活存", "bank");
        var (d1, d2, d3) = (Doc("a.pdf"), Doc("b.pdf"), Doc("c.pdf"));

        Entry("expense", card, 1_000, "2026-11-01", docId: d1);
        Entry("expense", card, 1_000, "2026-11-01", docId: d2);       // 同一筆在兩份檔案出現 → 疑似重複
        Entry("expense", bank, 2_000, "2026-11-02", docId: d1);
        Entry("expense", bank, 2_000, "2026-11-02", docId: d3);       // 存款單不做跨檔案比對

        Assert.Equal(1, _store.Flags().DuplicateGroups);
        var rows = _store.List(new LedgerStore.Filter { Status = "dupgroup" });
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("卡", r.Account));
    }

    [Fact]
    public void 孤兒列用警示列篩得出來()
    {
        var card = Account("卡", "credit_card");
        Entry("expense", card, 1_000, "2026-11-01", orphanedAt: Db.Now());
        Entry("expense", card, 2_000, "2026-11-02");

        Assert.Equal(1, _store.Flags().Orphans);
        var orphans = _store.List(new LedgerStore.Filter { Status = "orphan" });
        Assert.Equal(1_000L, Assert.Single(orphans).AmountMinor);
    }

    // ---------------------------------------------------------------- 寫入

    [Fact]
    public void 改分類會寫_edited_at_讓重匯不敢覆蓋()
    {
        var acct = Account("現金", "cash");
        var id = Entry("expense", acct, 1_000, "2026-11-01");
        var cat = _store.Categories("expense", leavesOnly: true).First();   // 標分類的下拉只給最底層

        _store.SetCategory(id, cat.Id);

        Assert.NotNull(Db.Scalar(_db, "SELECT edited_at FROM entry WHERE id=$id;", ("$id", id)));
        Assert.Equal(cat.Id, Db.Scalar(_db, "SELECT category_id FROM entry WHERE id=$id;", ("$id", id)));
    }

    [Fact]
    public void 分類報表只算基準幣而且未分類另成一列()
    {
        var twd = Account("台幣戶", "bank");
        var usd = Account("美元戶", "bank", "USD", 100_000);
        var cat = _store.AddCategory("餐飲", "expense");
        Entry("expense", twd, 1_000, "2026-11-01", categoryId: cat);
        Entry("expense", twd, 2_000, "2026-11-05", categoryId: cat);
        Entry("expense", usd, 9_999, "2026-11-02", currency: "USD");              // 非基準幣：没折算就不能進總額
        Entry("expense", twd, 3_000, null);                      // 沒消費日的列沒有月可以放
        Entry("expense", twd, 700, "2026-10-31");                // 未分類

        var pivot = _store.CategoryPivot();
        Assert.Equal(3_000, pivot.Cells.Single(c => c.CategoryId == cat && c.Month == "2026-11").TotalMinor);
        Assert.Contains(pivot.Cells, c => c.CategoryId == 0 && c.TotalMinor == 700);   // 未分類那列
        Assert.Equal(3_700, pivot.Cells.Sum(c => c.TotalMinor));                       // 美元 9,999 和沒日期的 3,000 都不在表裡
        Assert.Equal(1, pivot.SkippedForeignEntries);                                  // 但要让人看到少了幾筆
        Assert.Equal(["2026-10", "2026-11"], PivotTree.Months(pivot.Cells));
        Assert.Equal(0, _store.BaseCurrencyScale);                                     // 種子 TWD scale=0
    }

    [Fact]
    public void 跨收支方向的子分類會被擋而且訊息不是亂碼()
    {
        var exp = _store.AddCategory("交通", "expense");
        var ex = Assert.Throws<SqliteException>(() =>
            Db.Exec(_db, "INSERT INTO category(name, kind, parent_id) VALUES('外幣利息','income',$p);", ("$p", exp)));
        Assert.Contains("同方向", ex.Message);      // 0004 是 Big5 存的，RAISE 的文字會變亂碼；0007 重建後才對，這裡鎖住
    }

    [Fact]
    public void 分類只能標到最底層_父類會被擋()
    {
        var acct = Account("現金", "cash");
        var id = Entry("expense", acct, 1_000, "2026-11-01");
        var parent = _store.AddCategory("交通", "expense");
        var leaf = _store.AddCategory("油費", "expense", parent);

        Assert.Throws<InvalidOperationException>(() => _store.SetCategory(id, parent));
        Assert.Null(Db.Scalar(_db, "SELECT category_id FROM entry WHERE id=$id;", ("$id", id)));   // 擋下来就不能偷偷寫進去

        _store.SetCategory(id, leaf);                                                             // 最底層照樣能標
        Assert.Equal(leaf, Db.Scalar(_db, "SELECT category_id FROM entry WHERE id=$id;", ("$id", id)));

        // 標分類的下拉不列父類；篩選下拉要列（筛「交通」要連子類一起筛到）
        Assert.DoesNotContain("交通", _store.Categories("expense", leavesOnly: true).Select(c => c.Name));
        Assert.Contains("交通／油費", _store.Categories("expense", leavesOnly: true).Select(c => c.Name));
        Assert.Contains("交通", _store.Categories("expense").Select(c => c.Name));
    }

    [Fact]
    public void 建議分類要一商家一列而且帳戶筛選要有效()
    {
        var a = Account("渣打", "credit_card");
        var b = Account("現金", "cash");
        Entry("expense", a, 100, "2026-11-01", merchant: "Uber");
        Entry("expense", a, 200, "2026-11-02", merchant: "Uber");
        Entry("expense", b, 300, "2026-11-03", merchant: "全聯");
        Entry("expense", b, 400, "2026-11-04", merchant: null);

        var all = _store.UncategorizedMerchants();
        Assert.Equal(3, all.Count);
        Assert.Equal(2, all.First(m => m.Merchant == "Uber").Count);
        Assert.Contains(Loc.T("Store_Unnamed"), all.Select(m => m.Merchant));   // 無店名那列的 label 要跟著語系

        Assert.Single(_store.UncategorizedMerchants(accountId: a));             // 帳戶筛選之前根本沒進 SQL
    }

    [Fact]
    public void 依商家批次補分類只動看到的帳戶也受最底層限制()
    {
        var a = Account("渣打", "credit_card");
        var b = Account("現金", "cash");
        Entry("expense", a, 100, "2026-11-01", merchant: "Uber");
        Entry("expense", b, 200, "2026-11-02", merchant: "Uber");
        var parent = _store.AddCategory("交通", "expense");
        var leaf = _store.AddCategory("車票", "expense", parent);

        Assert.Equal(1, _store.SetCategoryForMerchant("Uber", leaf, accountId: a));
        Assert.Equal(1L, Db.Scalar(_db, "SELECT COUNT(*) FROM entry WHERE category_id=$c;", ("$c", leaf)));
        Assert.Throws<InvalidOperationException>(() => _store.SetCategoryForMerchant("Uber", parent));
    }

    [Fact]
    public void 補消費日只准正確格式而且補完才篩得進去()
    {
        var acct = Account("現金", "cash");
        var id = Entry("expense", acct, 1_000, null);

        Assert.Single(_store.List(new LedgerStore.Filter { From = "2026-11-01", To = "2026-11-30" })
                        .Where(r => r.Id == id));                     // 沒日期的列不會憑空消失，但排最後

        Assert.False(LedgerStore.TryDate("11/03/2026", out _));       // 美式格式要擋，不是猜
        Assert.False(LedgerStore.TryDate("2026-13-03", out _));
        Assert.True(LedgerStore.TryDate("2026/11/3", out var normalized));
        Assert.Equal("2026-11-03", normalized);

        _store.SetDate(id, normalized);
        var row = Assert.Single(_store.List(new LedgerStore.Filter { From = "2026-11-01", To = "2026-11-30" }));
        Assert.Equal("2026-11-03", row.Date);
    }

    [Fact]
    public void 轉帳列不准有分類()
    {
        var bank = Account("活存", "bank");
        var card = Account("卡", "credit_card");
        var id = Entry("transfer", bank, 1_000, "2026-11-01", toAccountId: card);
        var cat = _store.Categories("expense", leavesOnly: true).First();   // 標分類的下拉只給最底層

        var row = Assert.Single(_store.List(new LedgerStore.Filter()));
        Assert.False(row.CanPickCategory);
        Assert.ThrowsAny<Exception>(() => Db.Exec(_db, "UPDATE entry SET category_id=$c WHERE id=$id;", ("$c", cat.Id), ("$id", id)));
    }

    [Fact]
    public void 手動記一筆直接算數而且擋掉不合理的輸入()
    {
        var acct = Account("錢包", "wallet");
        var cat = _store.Categories("expense", leavesOnly: true).First();   // 標分類的下拉只給最底層

        _store.InsertManual(new LedgerStore.ManualEntry(acct, "expense", 1_234, "2026-11-06", "路邊攤", cat.Id, Note: "附註"));

        var row = Assert.Single(_store.List(new LedgerStore.Filter()));
        Assert.Equal("路邊攤", row.Merchant);
        Assert.Equal(1_234L, row.AmountMinor);
        Assert.Equal("cleared", row.Status);                          // 手動輸入的就是數，不用再过一遍
        Assert.Equal(1_234L, _store.Totals("2026-11-01", "2026-11-30", null).ExpenseMinor);
        Assert.Equal("附註", row.Note);

        Assert.Throws<ArgumentException>(() => _store.InsertManual(new(acct, "expense", 0, "2026-11-06", null, null)));
        Assert.Throws<ArgumentException>(() => _store.InsertManual(new(acct, "transfer", 100, "2026-11-06", null, null)));
        Assert.Throws<ArgumentException>(() => _store.InsertManual(new(acct, "expense", -100, "2026-11-06", null, null)));
    }

    [Fact]
    public void 分類有種子而且只給方向對的那組()
    {
        Assert.Contains(_store.Categories("expense"), c => c.Name == "餐飲");
        Assert.DoesNotContain(_store.Categories("expense"), c => c.Name == "利息收入");
        Assert.Contains(_store.Categories("income"), c => c.Name == "利息收入");

        var again = _store.AddCategory("餐飲", "expense");              // 同名不准憑空多一個
        Assert.Equal(_store.Categories("expense").First(c => c.Name == "餐飲").Id, again);
        Assert.True(_store.AddCategory("寵物", "expense") > 0);
        Assert.Contains("寵物", _store.Categories("expense").Select(c => c.Name));
    }

    [Fact]
    public void 寫過資料就要讓開著的分頁知道()
    {
        var acct = Account("現金", "cash");
        var before = LedgerStore.DataVersion;
        var id = Entry("expense", acct, 1_000, "2026-11-01");           // 匯入器自己不會推版本
        Assert.Equal(before, LedgerStore.DataVersion);

        _store.SetStatus(id, "ignored");
        Assert.True(LedgerStore.DataVersion > before);                  // 分頁靠這個決定要不要重查
        Assert.Empty(_store.List(new LedgerStore.Filter()));
    }
}
