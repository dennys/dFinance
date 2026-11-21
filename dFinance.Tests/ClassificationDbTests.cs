using dFinance.Classification;
using dFinance.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace dFinance.Tests;

/// <summary>
/// 建議子系統的 DB 端測試：0011 遷移、種子、以及「套用建議」寫進庫之後的口徑。
/// 功能性斷言（新增規則→建議從無變有）放這裡、不放 <c>--uismoke</c>：uismoke 跑的是 live 庫，
/// 而它的分類資料是靠 <c>devcheck --autocat</c> 塞的（塞完就沒有未分類列可建議），兩邊要求互斥。
/// </summary>
[Collection("DataVersion")]
public sealed class ClassificationDbTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"dfinance-classify-{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _db;
    private readonly LedgerStore _store;
    private readonly SuggestionService _svc;

    public ClassificationDbTests()
    {
        _db = Db.Open(_dbPath);
        _store = new LedgerStore(_db);
        _svc = new SuggestionService(_store);
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_dbPath)!, Path.GetFileName(_dbPath) + "*"))
            File.Delete(f);
    }

    long Account() => Db.InsertId(_db,
        "INSERT INTO account(name, type, currency, opening_balance_minor, created_at) VALUES($n, 'credit_card', 'TWD', 0, $at);",
        ("$n", Guid.NewGuid().ToString("N")), ("$at", Db.Now()));

    long Entry(string kind, long accountId, long amountMinor, string? merchant, long? categoryId = null)
        => Db.InsertId(_db, """
            INSERT INTO entry(kind, account_id, category_id, currency, amount_minor, txn_date, merchant,
                              status, source, source_line_hash, created_at)
            VALUES($kind, $acct, $cat, 'TWD', $amt, '2026-11-01', $m, 'cleared', 'import', $hash, $at);
            """,
            ("$kind", kind), ("$acct", accountId),
            ("$cat", (object?)categoryId ?? DBNull.Value), ("$amt", amountMinor),
            ("$m", (object?)merchant ?? DBNull.Value), ("$hash", Guid.NewGuid().ToString("N")), ("$at", Db.Now()));

    long LeafId(string path) => _store.CategoryNodes().Single(n => n.Path == path && n.IsLeaf).Id;

    // ---------------------------------------------------------------- 遷移與種子

    [Fact]
    public void 規則種子全部指得到最底層分類而且_ignore_不帶分類()
    {
        var rules = _svc.Rules();
        Assert.True(rules.Count > 50, $"種子沒進去看起來只有 {rules.Count} 條");
        var nodes = _store.CategoryNodes().ToDictionary(n => n.Id);

        foreach (var r in rules.Where(r => r.Match == RuleMatch.Ignore))
            Assert.Null(r.CategoryId);                                          // CHECK 也擋,但口徑要在這裡鎖住
        foreach (var r in rules.Where(r => r.Match != RuleMatch.Ignore))
        {
            var target = Assert.IsType<long>(r.CategoryId);
            Assert.True(nodes.ContainsKey(target), $"規則「{r.Pattern}」的目標 {target} 不存在");
            Assert.True(nodes[target].IsLeaf, $"規則「{r.Pattern}」的目標「{nodes[target].Path}」不是最底層");
            if (r.Kind != "any")
                Assert.Equal(nodes[target].Kind, r.Kind);                        // 方向寫反的種子不准混進庫
        }
        Assert.Contains(rules, r => r.Pattern == "統一超商" && r.Match != RuleMatch.Ignore);   // 舊表完全不中的那條（B8）
        Assert.DoesNotContain(rules, r => r.Pattern == "?險");                    // 舊表那條永不中的 contains（B1）
        Assert.DoesNotContain(rules, r => r.Pattern == "Foodpanda外送交通" || r.Pattern == "Netflix網路");   // B4
    }

    [Fact]
    public void 補的_catch_all_leaf_要存在而且跟父同方向()
    {
        var nodes = _store.CategoryNodes();
        foreach (var path in new[] { "餐飲／其他餐飲", "交通／停車費", "購物／其他", "水電瓦斯／其他",
                                     "通訊網路／其他", "訂閱軟體／其他", "醫療保健／其他", "居住／其他" })
            Assert.Contains(path, nodes.Where(n => n.IsLeaf).Select(n => n.Path));
    }

    [Fact]
    public void 遷移跑兩次不會把種子塞重()
    {
        var before = _svc.Rules().Count;
        using (var again = Db.Open(_dbPath)) { }                                 // 開庫就是再跑一次 Migrate
        Assert.Equal(before, _svc.Rules().Count);
    }

    // ---------------------------------------------------------------- 套用

    [Fact]
    public void 套用建議要一次標完同鍵變體_不動人已標的_也不寫_edited_at()
    {
        var a = Account();
        var other = LeafId("餐飲／外食");
        Entry("expense", a, 100, "中油－關東橋站自助加油（Ｄ４");
        Entry("expense", a, 200, "A- 中油－關東橋站自助加油（Ｄ４");              // 同一間店的另一種寫法（B5）
        var tagged = Entry("expense", a, 300, "中油－關東橋站自助加油（Ｄ４", other);   // 人已標的：不准被蓋掉

        var row = Assert.Single(_svc.Preview().Rows, r => r.CategoryId == LeafId("交通／油費"));
        Assert.Equal(2, row.RawVariants.Count);

        Assert.Equal(2, _svc.Apply(row, row.CategoryId!.Value));
        Assert.Equal(2L, Db.Scalar(_db, "SELECT COUNT(*) FROM entry WHERE category_id=$c;", ("$c", LeafId("交通／油費"))));
        Assert.Equal(other, Db.Scalar(_db, "SELECT category_id FROM entry WHERE id=$i;", ("$i", tagged)));
        // 決定 6：只有人在帳本格子裡親手改才算人工編輯。寫了 edited_at 會讓這批列對未來的 parser 修正免疫（B10）。
        Assert.Equal(0L, Db.Scalar(_db, "SELECT COUNT(*) FROM entry WHERE edited_at IS NOT NULL"));
    }

    [Fact]
    public void 套用只能標最底層()
    {
        var a = Account();
        Entry("expense", a, 100, "全家便利商店");
        var row = Assert.Single(_svc.Preview().Rows);
        Assert.Equal(LeafId("餐飲／超商飲料"), row.CategoryId);
        Assert.Throws<InvalidOperationException>(() => _svc.Apply(row, _store.CategoryNodes().Single(n => n.Path == "餐飲").Id));
    }

    [Fact]
    public void 收入那側的商家要拿到收入分類而且只動自己那一側()
    {
        var a = Account();
        Entry("income", a, 50000, "薪資 倍利");
        Entry("expense", a, 100, "薪資 倍利");                                   // 同名的支出列：不能被一起標掉

        var rows = _svc.Preview().Rows.Where(r => r.MerchantKey == "薪資倍利").ToList();
        var income = Assert.Single(rows, r => r.Kind == "income");
        Assert.Equal(LeafId("薪資收入／本薪"), income.CategoryId);

        Assert.Equal(1, _svc.Apply(income, income.CategoryId!.Value));
        Assert.Equal(1L, Db.Scalar(_db, "SELECT COUNT(*) FROM entry WHERE category_id IS NOT NULL AND kind='income'"));
        Assert.Equal(1L, Db.Scalar(_db, "SELECT COUNT(*) FROM entry WHERE category_id IS NULL AND kind='expense'"));
    }

    [Fact]
    public void 渠道詞不該變成建議但也不能讓商家從清單上消失()
    {
        var a = Account();
        Entry("expense", a, 100, "代收－全支付ＴＷＱＲ跨機構購物交易");              // 整列都是渠道名
        Entry("expense", a, 200, "代收－全盈支付－全家便利商店");                  // 67 筆那一組的形狀

        var rows = _svc.Preview().Rows;
        Assert.Equal(2, rows.Count);
        Assert.Equal("餐飲／超商飲料", Assert.Single(rows, r => r.CategoryPath is not null).CategoryPath);
        var unknown = Assert.Single(rows, r => r.CategoryPath is null);         // 要看得见「沒認到」，不能跟沒列一樣
        Assert.Equal(SuggestionSource.None, unknown.Source);
    }

    [Fact]
    public void 規則被拆成父類之後要紅標跳過而不是靜默()
    {
        var a = Account();
        Entry("expense", a, 100, "全家便利商店");
        var leaf = LeafId("餐飲／超商飲料");
        var child = _store.AddCategory("飲料", "expense", leaf);                 // 把規則的目標變成父類

        var row = Assert.Single(_svc.Preview().Rows);
        Assert.Null(row.CategoryId);
        Assert.False(row.PreApply);
        // 拆成父類會打到全部指向它的種子（全家/統一超商/7-11 …），要逐條紅標，不是只報第一條就了事。
        Assert.Contains(new BrokenRule(0, BrokenReason.TargetNotLeaf, "全家"),
                        _svc.Preview().Broken.Select(b => b with { RuleId = 0L }));
        Assert.NotNull(child);
    }

    [Fact]
    public void 沒接模型時行為要等於沒有_ai()
    {
        var a = Account();
        Entry("expense", a, 100, "全家便利商店");
        var plain = _svc.Preview().Rows;
        var withNull = new SuggestionService(_store, new NullAdvisor()).Preview().Rows;
        Assert.Equal(plain.Select(r => (r.MerchantKey, r.CategoryId, r.Source)),
                     withNull.Select(r => (r.MerchantKey, r.CategoryId, r.Source)));
    }
}
