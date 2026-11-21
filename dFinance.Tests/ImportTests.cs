using dFinance.Parsing;
using dFinance.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace dFinance.Tests;

/// <summary>
/// 匯入落庫的測試。不需要樣本 PDF：直接遞合成的 ParsedStatement，
/// 每個測試一個臨時 .db（跑完連連線池一起清掉，否則檔鎖著刪不掉）。
/// </summary>
public sealed class ImportTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"dfinance-test-{Guid.NewGuid():N}.db");
    private readonly string _docsDir = Path.Combine(Path.GetTempPath(), $"dfinance-test-docs-{Guid.NewGuid():N}");
    private readonly SqliteConnection _db;

    public ImportTests() => _db = Db.Open(_dbPath);

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();                      // SQLite 連線有池，不清池刪不掉檔
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_dbPath)!, Path.GetFileName(_dbPath) + "*"))
            File.Delete(f);
        if (Directory.Exists(_docsDir)) Directory.Delete(_docsDir, true);
    }

    static ParsedStatement Card(DateOnly end, params (DateOnly Date, decimal Amount, string Merchant)[] rows) => new()
    {
        Issuer = "台新國際商業銀行",
        Parser = "TaishinCardParser",
        AccountMask = "1234",
        StatementStartDate = end.AddMonths(-1),
        StatementEndDate = end,
        DueDate = end.AddDays(15),
        TotalDue = rows.Sum(r => r.Amount),
        Transactions = rows.Select(r => new ParsedTransaction
        {
            Date = r.Date, Merchant = r.Merchant, Amount = r.Amount, Currency = "TWD", Page = 1,
            RawLine = $"{r.Date:yyyy/MM/dd} {r.Merchant} {r.Amount}",
        }).ToList(),
    };

    int Count(string where = "", params (string, object?)[] ps)
        => Convert.ToInt32(Db.Scalar(_db, $"SELECT count(*) FROM entry WHERE deleted_at IS NULL {where};", ps));

    /// <summary>
    /// parser 交的是單上的原文（永豐卡「臺幣」、永豐存款單「新臺幣」、台新卡「TW」），
    /// 對成代碼是匯入端的活：這一對錯就是「外幣金額被當成台幣」那百倍級的錯，所以從匯入側也钉一次。
    /// </summary>
    [Theory]
    [InlineData("臺幣")]
    [InlineData("新臺幣")]
    [InlineData("台幣")]
    [InlineData("TW")]
    public void parser交的原文幣別靠aliases對成代碼(string raw)
    {
        var st = new ParsedStatement
        {
            Issuer = "永豐商業銀行", Parser = "SinopacCardParser", AccountMask = "9209",
            StatementEndDate = new DateOnly(2026, 11, 15),
            Transactions =
            [
                new ParsedTransaction { Date = new DateOnly(2026, 11, 1), Merchant = "全聯", Amount = 100m, Currency = raw, Page = 1 },
            ],
        };

        var r = new StatementImporter(_db, _docsDir).Import(st, "sinopac.pdf");

        Assert.Equal("TWD", Db.Scalar(_db, "SELECT currency FROM account WHERE mask='9209';"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("幣別「"));      // 對不到才會退回基準幣並警告
    }

    [Fact]
    public void 存款單的多個帳號分別歸到自己的帳戶()
    {
        var st = new ParsedStatement
        {
            Issuer = "兆豐國際商業銀行", Parser = "MegaDepositParser", AccountMask = "02010660350",
            Transactions =
            [
                new ParsedTransaction { Date = new DateOnly(2026, 10, 21), Merchant = "利息", Amount = 99m, Currency = "TWD", Account = "02064003171" },
                new ParsedTransaction { Date = new DateOnly(2026, 10, 22), Merchant = "網際轉", Amount = -99m, Currency = "TWD", Account = "02064003171" },
                new ParsedTransaction { Date = new DateOnly(2026, 10, 22), Merchant = "網際轉", Amount = 99m, Currency = "TWD", Account = "02010660350" },
            ],
        };

        var result = new StatementImporter(_db, _docsDir).Import(st, "mega.pdf");

        Assert.Equal(3, result.Inserted);
        Assert.Equal(2L, Db.Scalar(_db, "SELECT count(*) FROM account WHERE issuer='兆豐國際商業銀行';"));
        // 同一筆轉帳在兩個帳戶各留一列，不会疊成同一帳戶的兩筆支出。
        // 存款單的正數是「存入」（income）、負數是扣款（expense）；金額存正數（entry 的 CHECK），加減由 kind 決定。
        Assert.Equal(1, Count("AND account_id=(SELECT id FROM account WHERE mask='02064003171') AND amount_minor=9900 AND kind='income'"));
        Assert.Equal(1, Count("AND account_id=(SELECT id FROM account WHERE mask='02064003171') AND amount_minor=9900 AND kind='expense'"));
    }

    [Fact]
    public void 存款單的正數是存入不是支出負數才是支出()
    {
        // 銀行的借貸記法跟卡帳相反：存款對帳單上正的錢是「存进来」。
        // 以前只看正負號（kind = signed < 0 ? refund : expense），薪資會被記成 expense：
        // 淨值少 40,000、收支報表多一筆不存在的消費，而提款那一側負負得正还把餘額加上去。
        var st = new ParsedStatement
        {
            Issuer = "兆豐國際商業銀行", Parser = "MegaDepositParser", AccountMask = "02010660350",
            Transactions =
            [
                new ParsedTransaction { Date = new DateOnly(2026, 10, 21), Merchant = "薪資", Amount = 40_000m, Currency = "TWD" },
                new ParsedTransaction { Date = new DateOnly(2026, 10, 22), Merchant = "提款", Amount = -5_000m, Currency = "TWD" },
            ],
        };

        var result = new StatementImporter(_db, _docsDir).Import(st, "mega.pdf");
        Assert.Equal(2, result.Inserted);
        Assert.Equal(40_000_00L, Db.Scalar(_db, "SELECT amount_minor FROM entry WHERE kind='income';"));
        Assert.Equal(5_000_00L, Db.Scalar(_db, "SELECT amount_minor FROM entry WHERE kind='expense';"));
        Assert.Equal(35_000_00L, new LedgerStore(_db).NetWorth().NetMinor);        // 存 4 萬、提 5 千 → 35,000
    }

    [Fact]
    public void 單上印的餘額會進帳戶餘額當漏解析的校驗輸入()
    {
        var st = new ParsedStatement
        {
            Issuer = "兆豐國際商業銀行", Parser = "MegaDepositParser", AccountMask = "02010660350",
            Transactions =
            [
                new ParsedTransaction { Date = new DateOnly(2026, 10, 21), Merchant = "利息", Amount = 99m, Currency = "TWD", Account = "02010660350" },
            ],
            Balances =
            [
                new ParsedBalance { AsOf = new DateOnly(2026, 10, 31), Amount = 10_924m, Currency = "TWD", Account = "02010660350" },
            ],
        };

        new StatementImporter(_db, _docsDir).Import(st, "mega.pdf");

        // 存成 statement 來源，才能跟人工盤點分得開；幣別要對成代碼後再存（帳戶一筆只一種幣別）。
        Assert.Equal("1092400@TWD@statement", Db.Scalar(_db,
            "SELECT balance_minor || '@' || currency || '@' || source FROM account_balance;"));
        Assert.Equal("2026-10-31", Db.Scalar(_db, "SELECT as_of FROM account_balance;"));
    }

    [Fact]
    public void 單上的餘額不准蓋掉人当场盤的數()
    {
        var st = new ParsedStatement
        {
            Issuer = "兆豐國際商業銀行", Parser = "MegaDepositParser", AccountMask = "02010660350",
            Transactions =
            [
                new ParsedTransaction { Date = new DateOnly(2026, 10, 21), Merchant = "利息", Amount = 99m, Currency = "TWD", Account = "02010660350" },
            ],
            Balances = [ new ParsedBalance { AsOf = new DateOnly(2026, 10, 31), Amount = 10_924m, Currency = "TWD", Account = "02010660350" } ],
        };

        var imp = new StatementImporter(_db, _docsDir);
        imp.Import(st, "mega.pdf");
        var acct = (long)Db.Scalar(_db, "SELECT id FROM account WHERE mask='02010660350';")!;
        // 同一 (帳戶, 日, 幣別) 只能有一列：先把單子的換成人当场盤的那個數，再看重匯會不會蓋掉它
        Db.Exec(_db, "DELETE FROM account_balance;");
        Db.Exec(_db, "INSERT INTO account_balance(account_id, as_of, currency, balance_minor, source) VALUES($a,'2026-10-31','TWD',777,'manual');",
                ("$a", acct));

        imp.Import(st, "mega.pdf");                                       // 重匯同一份單子

        Assert.Equal(777L, Db.Scalar(_db, "SELECT balance_minor FROM account_balance WHERE source='manual';"));
    }

    [Fact]
    public void 建立資料庫會跑遷移並留下版本紀錄()
    {
        // 對照嵌入的遷移檔清單（加腳本不用回來改這條斷言）
        Assert.Equal(Db.MigrationNames(),
                     Db.Query(_db, "SELECT name FROM migration ORDER BY version").Select(o => (string)o!).ToList());
        Assert.Equal("0001_init", Db.Scalar(_db, "SELECT name FROM migration WHERE version=1;"));
        Assert.Equal(6L, Db.Scalar(_db, "SELECT count(*) FROM currency;"));        // 內建 TWD/USD/JPY/...
        // 匯率自動更新之前的寫死值；0005 之後存的是幣對（1 美金 = 32.9 台幣）
        Assert.Equal(32.9m, Convert.ToDecimal(Db.Scalar(_db,
            "SELECT rate FROM exchange_rate WHERE from_ccy='USD' AND to_ccy='TWD';")));
    }

    [Fact]
    public void 同一份內容重匯第二次只更新不新增()
    {
        var st = Card(new DateOnly(2026, 10, 31),
                      (new DateOnly(2026, 10, 5), 1500m, "全聯"),
                      (new DateOnly(2026, 10, 6), 250m, "超商"));

        var first = new StatementImporter(_db, _docsDir).Import(st, "a.pdf");
        Assert.Equal(2, first.Inserted);
        Assert.Equal(0, first.Updated);

        var second = new StatementImporter(_db, _docsDir).Import(st, "a.pdf");     // 換一份檔案重匯（sha 不同）
        Assert.Equal(0, second.Inserted);
        Assert.Equal(2, second.Updated);
        Assert.Equal(2, Count());
    }

    [Fact]
    public void 人工改過分類的列重匯後分類不動()
    {
        var st = Card(new DateOnly(2026, 10, 31), (new DateOnly(2026, 10, 5), 1500m, "全聯"));
        var importer = new StatementImporter(_db, _docsDir);
        importer.Import(st, "a.pdf");

        var categoryId = Db.InsertId(_db, "INSERT INTO category(name, kind) VALUES('餐飲飲食', 'expense');");
        Db.Exec(_db, "UPDATE entry SET category_id=$c, edited_at=$at;", ("$c", categoryId), ("$at", Db.Now()));

        st.Transactions[0].Merchant = "全聯福利中心 X 新莊";                        // 假裝 parser 改版洗乾淨了
        st.Transactions[0].RawLine = "2026-10-05 全聯福利中心 X 新莊 1500";
        var again = new StatementImporter(_db, _docsDir);
        again.Import(st, "a.pdf");

        // 人工改過的列：使用者會看到的欄位一個都不覆蓋，只刷新比對用的原始文字。
        Assert.Equal(categoryId, Db.Scalar(_db, "SELECT category_id FROM entry;"));
        Assert.Equal("全聯", Db.Scalar(_db, "SELECT merchant FROM entry;"));
        Assert.Contains("全聯福利中心", (string)Db.Scalar(_db, "SELECT source_raw FROM entry;")!);
    }

    [Fact]
    public void 零元列跳過且拿不到日期時留空等人補()
    {
        var st = Card(new DateOnly(2026, 10, 31),
                      (new DateOnly(2026, 10, 5), 0m, "退款抵掉"),
                      (default(DateOnly), 800m, "沒日期的那筆"));
        var r = new StatementImporter(_db, _docsDir).Import(st, "a.pdf");

        Assert.Equal(1, r.SkippedZero);
        Assert.Equal(1, r.Inserted);
        Assert.Null(Db.Scalar(_db, "SELECT txn_date FROM entry;"));               // 不准塞 0001-01-01
        Assert.Equal("imported", Db.Scalar(_db, "SELECT status FROM entry;"));
        Assert.Contains(r.Warnings!, w => w.Contains("消費日"));
    }

    [Fact]
    public void 沒被認出的負額先當退款並提醒()
    {
        var st = Card(new DateOnly(2026, 10, 31), (new DateOnly(2026, 10, 9), -8420m, "刷卡現金回饋"));
        var r = new StatementImporter(_db, _docsDir).Import(st, "a.pdf");

        Assert.Equal("refund", Db.Scalar(_db, "SELECT kind FROM entry;"));
        Assert.Equal(-842000L, Db.Scalar(_db, "SELECT amount_minor FROM entry;"));  // 1/100 元，整數儲存
        Assert.Contains(r.Warnings!, w => w.Contains("負額"));
    }

    [Fact]
    public void 繳款列記成轉帳從扣款帳戶扣到卡帳()
    {
        var bank = Db.InsertId(_db, "INSERT INTO account(name, type, currency, created_at) VALUES('活存', 'bank', 'TWD', '2026-11-01');");
        Db.Exec(_db, "INSERT OR REPLACE INTO settings(key, value) VALUES('default_payment_account_id', $v);", ("$v", bank.ToString()));

        var st = Card(new DateOnly(2026, 10, 31),
                      (new DateOnly(2026, 10, 5), 1500m, "全聯"),
                      (new DateOnly(2026, 10, 20), -1500m, "已繳款金額-本行帳戶扣款"));
        st.Transactions[^1].IsPayment = true;                                  // 正常由 StatementParser 標，這裡直接遞 ParsedStatement 就自己標
        var r = new StatementImporter(_db, _docsDir).Import(st, "a.pdf");

        Assert.Equal("transfer", Db.Scalar(_db, "SELECT kind FROM entry WHERE merchant LIKE '已繳款%';"));
        Assert.Equal(150000L, Db.Scalar(_db, "SELECT amount_minor FROM entry WHERE merchant LIKE '已繳款%';"));   // CHECK 要正數
        Assert.Equal(bank, Convert.ToInt64(Db.Scalar(_db, "SELECT account_id FROM entry WHERE merchant LIKE '已繳款%';")));   // 扣的那一側
        var card = Convert.ToInt64(Db.Scalar(_db, "SELECT id FROM account WHERE type='credit_card';"));
        Assert.Equal(card, Convert.ToInt64(Db.Scalar(_db, "SELECT to_account_id FROM entry WHERE merchant LIKE '已繳款%';"))); // 收錢的是卡帳
        Assert.Contains(r.Warnings!, w => w.Contains("轉帳") && w.Contains("活存"));
    }

    [Fact]
    public void 繳款列沒有扣款帳戶時宁願留退款也不憑空扣別的帳戶()
    {
        var st = Card(new DateOnly(2026, 10, 31), (new DateOnly(2026, 10, 20), -1500m, "已繳款金額-本行帳戶扣款"));
        st.Transactions[0].IsPayment = true;
        var r = new StatementImporter(_db, _docsDir).Import(st, "a.pdf");

        Assert.Equal("refund", Db.Scalar(_db, "SELECT kind FROM entry;"));
        Assert.Null(Db.Scalar(_db, "SELECT to_account_id FROM entry;"));          // 不准猜一個帳戶去扣
        Assert.Contains(r.Warnings!, w => w.Contains("扣款"));
    }

    /// <summary>同一筆卡帳繳款會在兩份單子上各出現一次：卡帳那側（「已繳款金額-本行帳戶扣款」）
    /// 與扣款帳戶自己的存款單那側（「兆豐卡 兆豐銀行」那种）。兩份都匯就是同一個錢移動扣兩次 ——
    /// 實資料的兆豐 4,534 元就是這樣被餘額校驗抓到的，所以四个方向都钉一次。</summary>
    static ParsedStatement MegaCard(DateOnly end, params (DateOnly Date, decimal Amount, string Merchant)[] rows) => new()
    {
        Issuer = "兆豐國際商業銀行", Parser = "MegaCardParser", AccountMask = "9306",
        StatementStartDate = end.AddMonths(-1), StatementEndDate = end, DueDate = end.AddDays(15),
        TotalDue = rows.Where(r => r.Amount > 0).Sum(r => r.Amount),
        Transactions = rows.Select(r => new ParsedTransaction
        {
            Date = r.Date, Merchant = r.Merchant, Amount = r.Amount, Currency = "TWD", Page = 1,
            RawLine = $"{r.Date:yyyy/MM/dd} {r.Merchant} {r.Amount}",
        }).ToList(),
    };

    static ParsedStatement MegaDeposit(params (DateOnly Date, decimal Amount, string Merchant)[] rows) => new()
    {
        Issuer = "兆豐國際商業銀行", Parser = "MegaDepositParser", AccountMask = "02010660350",
        Transactions = rows.Select(r => new ParsedTransaction
        {
            Date = r.Date, Merchant = r.Merchant, Amount = r.Amount, Currency = "TWD", Page = 1,
            RawLine = $"{r.Date:yyyy/MM/dd} {r.Merchant} {r.Amount}",
        }).ToList(),
    };

    /// <summary>扣款帳戶：預設付款帳戶設好，繳款才會落成轉帳（卡帳上沒印卡號那种）。</summary>
    long Bank()
    {
        var bank = Db.InsertId(_db, """
            INSERT INTO account(name, type, currency, issuer, mask, created_at)
            VALUES('兆豐活存', 'bank', 'TWD', '兆豐國際商業銀行', '02010660350', '2026-11-01');
            """);
        Db.Exec(_db, "INSERT OR REPLACE INTO settings(key, value) VALUES('default_payment_account_id', $v);", ("$v", bank.ToString()));
        return bank;
    }

    ParsedStatement PaidCardStatement()
    {
        var st = MegaCard(new DateOnly(2026, 10, 31),
                          (new DateOnly(2026, 10, 5), 1500m, "全聯"),
                          (new DateOnly(2026, 10, 28), -4534m, "已繳款金額-本行帳戶扣款"));
        st.Transactions[^1].IsPayment = true;                                  // 正常由 parser 標，這裡直接遵 ParsedStatement 就自己標
        return st;
    }

    object? Payment(string column = "kind")
        => Db.Scalar(_db, $"SELECT {column} FROM entry WHERE amount_minor = 453400 AND deleted_at IS NULL;", Array.Empty<(string, object?)>());

    [Fact]
    public void 卡帳先進來之後存款單的同一筆繳款不會扣兩次()
    {
        Bank();
        new StatementImporter(_db, _docsDir).Import(PaidCardStatement(), "mega_card.pdf");     // 實際檔案順序：卡帳在前

        var r = new StatementImporter(_db, _docsDir)
            .Import(MegaDeposit((new DateOnly(2026, 10, 28), -4534m, "兆豐卡 兆豐銀行")), "mega_dep.pdf");

        Assert.Equal(1, Count("AND amount_minor=453400"));                     // 不是轉帳＋支出兩筆
        Assert.Equal("transfer", Payment());
        Assert.Contains(r.Warnings!, w => w.Contains("同一個錢移動"));              // 跳掉一筆要看得见，不能静靜的
    }

    [Fact]
    public void 存款單先進來之後卡帳的繳款列把它升成轉帳而不是多插一筆()
    {
        var bank = Bank();
        new StatementImporter(_db, _docsDir)
            .Import(MegaDeposit((new DateOnly(2026, 10, 28), -4534m, "兆豐卡 兆豐銀行")), "mega_dep.pdf");

        new StatementImporter(_db, _docsDir).Import(PaidCardStatement(), "mega_card.pdf");

        Assert.Equal(1, Count("AND amount_minor=453400"));
        Assert.Equal("transfer", Payment());
        Assert.Equal(bank, Convert.ToInt64(Payment("account_id")!));                    // 存款帳戶扣的
        Assert.Equal("credit_card", Db.Scalar(_db, "SELECT type FROM account WHERE id=$id;", ("$id", Convert.ToInt64(Payment("to_account_id")!))));
        Assert.NotNull(Payment("bill_id"));                                            // 卡帳那側的帳期也掛上了，卡才不會报未繳
    }

    [Fact]
    public void 同額同日是別的支出_不准被繳款併掉()
    {
        // 實資料同一個帳戶同日有幾筆同額的現金提取；抓不準就留兩筆讓餘額校驗去報，不能悄悄少一筆。
        Bank();
        new StatementImporter(_db, _docsDir).Import(PaidCardStatement(), "mega_card.pdf");
        new StatementImporter(_db, _docsDir).Import(MegaDeposit((new DateOnly(2026, 10, 28), -4534m, "現金 FEE0 提款")), "mega_dep.pdf");

        Assert.Equal(2, Count("AND amount_minor=453400"));
    }

    [Fact]
    public void 兩份單子重匯_同一筆繳款不會变成兩筆()
    {
        Bank();
        for (var i = 0; i < 2; i++)                                            // 换檔名重匯（sha 不同，走的是跳檔案去重那條路）
        {
            new StatementImporter(_db, _docsDir).Import(PaidCardStatement(), $"card{i}.pdf");
            new StatementImporter(_db, _docsDir).Import(MegaDeposit((new DateOnly(2026, 10, 28), -4534m, "兆豐卡 兆豐銀行")), $"dep{i}.pdf");
        }
        Assert.Equal(1, Count("AND amount_minor=453400 AND kind='transfer'"));
    }

    [Fact]
    public void 同帳期重匯舊列會標孤兒而不是被刪掉()
    {
        var end = new DateOnly(2026, 10, 31);
        var importer = new StatementImporter(_db, _docsDir);
        importer.Import(Card(end, (new DateOnly(2026, 10, 5), 1500m, "全聯"),
                                 (new DateOnly(2026, 10, 6), 250m, "超商")), "a.pdf");

        // 新版 parser 漏了超商那筆（只認得全聯）
        importer.Import(Card(end, (new DateOnly(2026, 10, 5), 1500m, "全聯")), "b.pdf");

        Assert.Equal(2, Count());                                                   // 不准吃掉使用者的帳
        Assert.Equal(1, Count("AND orphaned_at IS NOT NULL"));
        Assert.Equal(1L, Db.Scalar(_db, "SELECT count(*) FROM document WHERE superseded_at IS NOT NULL;"));
    }

    [Fact]
    public void 留底的PDF用內容sha存檔而不是原檔名()
    {
        var bytes = new byte[] { 0x25, 0x50, 0x44, 0x46, 1, 2, 3 };                 // 假的 PDF 位元組
        var r = new StatementImporter(_db, _docsDir)
            .Import(Card(new DateOnly(2026, 10, 31), (new DateOnly(2026, 10, 5), 100m, "測")), "原始檔名 信用卡帳單.pdf",
                    pdfBytes: bytes);

        var archived = (string)Db.Scalar(_db, "SELECT archived_path FROM document WHERE id=$d;", ("$d", r.DocumentId))!;
        Assert.True(File.Exists(archived));
        Assert.Equal("原始檔名 信用卡帳單.pdf", Db.Scalar(_db, "SELECT file_name FROM document WHERE id=$d;", ("$d", r.DocumentId)));
        Assert.Matches("^[0-9a-f]{64}\\.pdf$", Path.GetFileName(archived));         // 檔名是內容 sha，不是原名
        Assert.Equal(bytes, File.ReadAllBytes(archived));                           // 原樣存，不解密不改寫
    }

    [Fact]
    public void 繳費單沒有銀行帳戶時落到現金帳戶並警告()
    {
        var bill = new ParsedStatement
        {
            Issuer = "台灣電力公司", Parser = "UtilityBillParser", DueDate = new DateOnly(2026, 11, 20), TotalDue = 1234m,
            Transactions = { new ParsedTransaction { Date = new DateOnly(2026, 11, 20), Merchant = "電費", Amount = 1234m, Currency = "TWD" } },
        };
        var r = new StatementImporter(_db, _docsDir).Import(bill, "電費.pdf");

        Assert.Equal(StatementKind.Bill, r.Kind);
        Assert.Equal("cash", Db.Scalar(_db, "SELECT a.type FROM entry e JOIN account a ON a.id=e.account_id;"));
        Assert.Equal(1, r.AutoCreatedAccounts);
        Assert.Contains(r.Warnings!, w => w.Contains("現金"));
    }

    // -------------------------------------------------- 繳費單的重匯範圍（戶號就是可靠身份）

    static ParsedStatement PowerBill(string accountMask) => new()
    {
        Issuer = "台灣電力公司", Parser = "UtilityBillParser", AccountMask = accountMask,
        DueDate = new DateOnly(2026, 12, 5), StatementEndDate = new DateOnly(2026, 11, 30), TotalDue = 3229m,
        Transactions = { new ParsedTransaction { Date = new DateOnly(2026, 12, 5), Merchant = "電費", Amount = 3229m, Currency = "TWD", Page = 1 } },
    };

    [Fact]
    public void 同一張繳費單從網銀重下載不會記兩次()
    {
        var importer = new StatementImporter(_db, _docsDir);
        // 電子繳費單是伺服器即席產生的（自來水那份的 Producer 是 HTML→PDF 轉換器、檔名還帶 UUID），
        // 所以「重下載/補發」给的是不同 bytes → 檔案級的 sha256 擋不住，只能靠列級哈希裡的戶號。
        importer.Import(PowerBill("06-22-1330-57-1"), "台電11月.pdf", sha256: "sha-a");
        var again = importer.Import(PowerBill("06-22-1330-57-1"), "台電11月-重下載.pdf", sha256: "sha-b");

        Assert.Equal(1, Count());
        Assert.Equal(0, again.Inserted);
        Assert.Equal(1, again.Updated);
    }

    [Fact]
    public void 同月同額的兩張不同戶號繳費單要各存一筆()
    {
        var importer = new StatementImporter(_db, _docsDir);
        importer.Import(PowerBill("06-22-1330-57-1"), "自己家.pdf", sha256: "sha-a");
        importer.Import(PowerBill("06-22-9999-99-9"), "幫房客繳的.pdf", sha256: "sha-b");      // 同月同額、同一個付款帳戶

        Assert.Equal(2, Count());
    }

    [Fact]
    public void 抽不到戶號的繳費單宁願存兩份也不吃一筆()
    {
        var importer = new StatementImporter(_db, _docsDir);
        importer.Import(PowerBill(""), "沒印戶號的單子.pdf", sha256: "sha-a");
        importer.Import(PowerBill(""), "同一份的第二份.pdf", sha256: "sha-b");

        Assert.Equal(2, Count());                    // 沒身份 → 退回只在同一份檔案內比（跳檔案判重是「不插入」，比較嚴重）
    }

    [Fact]
    public void 同天同額的兩筆不會被當成重複()
    {
        var st = Card(new DateOnly(2026, 10, 31),
                      (new DateOnly(2026, 10, 5), 300m, "超商"),
                      (new DateOnly(2026, 10, 5), 300m, "超商"));                   // 真的刷兩次
        new StatementImporter(_db, _docsDir).Import(st, "a.pdf");
        Assert.Equal(2, Count());
    }

    [Fact]
    public void 約束擋得住錯的資料()
    {
        var accountId = Db.InsertId(_db, "INSERT INTO account(name, type, currency, created_at) VALUES('測試現金', 'cash', 'TWD', $at);", ("$at", Db.Now()));

        // 支出必須是正數（負數要用 refund）
        Assert.Throws<SqliteException>(() => Db.Exec(_db,
            "INSERT INTO entry(kind, account_id, currency, amount_minor, txn_date, created_at) VALUES('expense', $a, 'TWD', -500, '2026-11-01', $at);",
            ("$a", accountId), ("$at", Db.Now())));

        // transfer 一定要有到帳帳戶
        Assert.Throws<SqliteException>(() => Db.Exec(_db,
            "INSERT INTO entry(kind, account_id, currency, amount_minor, created_at) VALUES('transfer', $a, 'TWD', 500, $at);",
            ("$a", accountId), ("$at", Db.Now())));

        // 外鍵真的在擋（SQLite 預設不啟用，靠連線字串 Foreign Keys=True）
        Assert.Throws<SqliteException>(() => Db.Exec(_db,
            "INSERT INTO entry(kind, account_id, currency, amount_minor, created_at) VALUES('expense', 999999, 'TWD', 500, $at);",
            ("$at", Db.Now())));
    }

    [Fact]
    public void 真實台新信用卡帳單匯入兩次不會重複並留有底檔()
    {
        var path = Samples.PathOf("TSB_Creditcard_Estatement_202610.pdf");
        var passwords = Samples.Passwords().ToArray();
        if (path is null || passwords.Length == 0)
            Assert.Skip($"樣本或 .env 不在這台機器（path={path ?? "找不到"}、密碼 {passwords.Length} 組）");

        var first = new StatementImporter(_db, _docsDir).ImportPdf(path, passwords);
        Assert.False(first.Duplicate);
        Assert.Equal(StatementKind.Card, first.Kind);
        Assert.True(first.Inserted > 0, "應該要解析出交易明細");
        Assert.Equal(first.Inserted, Count());
        Assert.Equal("TaishinCardParser", Db.Scalar(_db, "SELECT parser FROM document WHERE id=$d;", ("$d", first.DocumentId)));
        Assert.NotNull(Db.Scalar(_db, "SELECT due_date FROM credit_card_bill;"));            // 繳款截止日有落地

        var again = new StatementImporter(_db, _docsDir).ImportPdf(path, passwords);
        Assert.True(again.Duplicate);
        Assert.Equal(first.Inserted, Count());                                                 // 同一份檔重匯不准多記

        var archived = (string)Db.Scalar(_db, "SELECT archived_path FROM document WHERE id=$d;", ("$d", first.DocumentId))!;
        Assert.True(File.Exists(archived));
        Assert.Equal(new FileInfo(path).Length, new FileInfo(archived).Length);                // 留底是原檔大小（沒被解改）
    }

    [Fact]
    public void 明細會挂到帳單記錄上而且能跟應繳金額對帳()
    {
        var st = Card(new DateOnly(2026, 10, 31),
                      (new DateOnly(2026, 10, 5), 1500m, "全聯"),
                      (new DateOnly(2026, 10, 6), 250m, "超商"));
        new StatementImporter(_db, _docsDir).Import(st, "a.pdf");

        Assert.Equal(1L, Db.Scalar(_db, "SELECT count(*) FROM credit_card_bill;"));
        Assert.Equal(2L, Db.Scalar(_db, "SELECT count(*) FROM entry WHERE bill_id IS NOT NULL;"));
        Assert.Equal(175000L, Db.Scalar(_db, "SELECT total_due_minor FROM credit_card_bill;"));
        // 「明細合計 == 本期應繳」這條校驗要能在 SQL 做（README 的核心賣點）
        Assert.Equal(175000L, Db.Scalar(_db, "SELECT sum(amount_minor) FROM entry WHERE kind='expense';"));
    }

    [Fact]
    public void 同月份的兩張缴費單不會互相取代()
    {
        ParsedStatement Bill(string issuer, string merchant) => new()
        {
            Issuer = issuer, Parser = "UtilityBillParser", DueDate = new DateOnly(2026, 11, 20),
            StatementEndDate = new DateOnly(2026, 10, 31), TotalDue = 1000m,
            Transactions = { new ParsedTransaction { Date = new DateOnly(2026, 11, 20), Merchant = merchant, Amount = 1000m, Currency = "TWD" } },
        };
        var importer = new StatementImporter(_db, _docsDir);
        importer.Import(Bill("中華電信", "電信費 2026/10"), "35-0920236528.pdf");
        importer.Import(Bill("中華電信", "電信費 2026/10"), "35-6662032.pdf");      // 同一個戶主另一份單

        Assert.Equal(2, Count());
        Assert.Equal(0L, Db.Scalar(_db, "SELECT count(*) FROM document WHERE superseded_at IS NOT NULL;"));
        Assert.Equal(0L, Db.Scalar(_db, "SELECT count(*) FROM entry WHERE orphaned_at IS NOT NULL;"));
    }

    [Fact]
    public void 金額換算是四捨五入到分而且可還原()
    {
        Assert.Equal(1999L, Money.ToMinor(19.99m));
        Assert.Equal(123457L, Money.ToMinor(1234.567m));
        Assert.Equal(-1999L, Money.ToMinor(-19.99m));
        Assert.Equal(0L, Money.ToMinor(0m));
        Assert.Equal(1234.57m, Money.FromMinor(123457L));
    }
}
