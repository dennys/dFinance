using dFinance.Classification;
using Xunit;

namespace dFinance.Tests;

/// <summary>
/// 商家→分類 建議的純測試：不啟 WPF、不打 DB，餵 <see cref="ClassifyInput"/> 就好。
/// 「語系不准改變比對結果」用 invariant 本身的行為直接鎖（土耳其 I 那類），
/// 不去改全域 <c>CurrentCulture</c>：那會讓同一時刻跑的另一批測試（會讀 Loc 的那邊）看到不一樣的語系。
/// </summary>
public class ClassificationTests
{
    static readonly CategoryNode[] Nodes =
    [
        new(11, "餐飲／超商飲料", "expense", true),
        new(12, "餐飲／外食", "expense", true),
        new(13, "餐飲／外送", "expense", true),
        new(21, "交通／油費", "expense", true),
        new(22, "交通／車票", "expense", true),
        new(23, "交通／出租接駁", "expense", true),
        new(41, "水電瓦斯／瓦斯", "expense", true),
        new(51, "通訊網路／行動門號", "expense", true),
        new(72, "醫療保健／藥局藥妝", "expense", true),
        new(80, "保險", "expense", true),
        new(310, "購物／網購", "expense", true),
        new(901, "餐飲", "expense", false),                                     // 父類：規則指到這裡要紅標
        new(902, "薪資收入／本薪", "income", true),
    ];

    static long _nextId = 1000;

    static ClassifyRule R(string pattern, long? target, int priority = 200, RuleMatch match = RuleMatch.Contains,
                          string kind = "expense", RuleSource source = RuleSource.User, bool enabled = true)
        => new(System.Threading.Interlocked.Increment(ref _nextId), pattern, match, target, priority, enabled, source, kind, null);

    static ClassifyRule Ignore(string pattern) => R(pattern, null, 900, RuleMatch.Ignore, "any");

    static SuggestionRow One(IReadOnlyList<ClassifyRule> rules, string? merchant, string kind = "expense")
    {
        var output = MerchantClassifier.Run(new ClassifyInput([new EntryFact(merchant, kind, 1)], rules, Nodes));
        return Assert.Single(output.Rows);
    }

    // ---------------------------------------------------------------- 正規化（B6）

    [Theory]
    [InlineData("代收－ＳＵＫＩＹＡ 新竹關新店", "代收sukiya新竹關新店")]  // 全形英文＋全形破折號（剝渠道詞是下一步,正規化不管）
    [InlineData("CAFÉ 台北", "cafe台北")]                                      // 去變音符：CAFÉ → cafe
    [InlineData("7-ELEVEN", "7eleven")]                                      // 標點被抹掉 ⇒ 7-11 與 7-ELEVEN 同鍵
    [InlineData("A- 中油－關東橋站自助加油（Ｄ４", "a中油關東橋站自助加油d4")]
    public void 正規化要把同一間店的多種寫法壓成同一個鍵(string raw, string expected)
        => Assert.Equal(expected, MerchantClassifier.Key(raw));

    [Fact]
    public void 正規化後的鍵不准跟語系走()
    {
        // 土耳其语的 ToLower('I') 是無點 ı、德語小數點是逗號：比對一律 invariant + Ordinal，這裡鎖住。
        Assert.Equal("i", MerchantClassifier.Key("I"));
        Assert.Equal("ist", MerchantClassifier.Key("İST"));                   // 带點的 I 正規化後也是 ist
        var row = One([R("IST", 12)], "İST 門市");
        Assert.Equal(12, row.CategoryId);
    }

    [Fact]
    public void 同鍵的原文變體要合併成一列()
    {
        var rules = new[] { R("全家便利商店", 11) };
        var output = MerchantClassifier.Run(new ClassifyInput(
            [new EntryFact("全家 便利商店", "expense", 3), new EntryFact("全家便利商店", "expense", 2)], rules, Nodes));   // 差一個空格
        var row = Assert.Single(output.Rows);                                  // 兩列變一列（B5）
        Assert.Equal(5, row.Count);
        Assert.Equal(2, row.RawVariants.Count);
        Assert.Equal(11, row.CategoryId);
    }

    // ---------------------------------------------------------------- 渠道詞

    [Fact]
    public void 渠道詞要反覆剝到不動點而且只剝前綴()
    {
        var ignores = new[] { "代收", "全盈支付" };
        Assert.Equal("全家便利商店", MerchantClassifier.Key(MerchantClassifier.StripIgnores("代收－全盈支付－全家便利商店", ignores)));
        // 中間的不剝：它只負責剝前綴，不是全域刪除（不然「代收」兩個字出現在店名中間會被吃掉）。
        Assert.Equal("x代收y", MerchantClassifier.Key(MerchantClassifier.StripIgnores("x代收y", ignores)));
        // 剝不光的判斷交給 Run（要退回原字串），這裡只看剝的結果。
        Assert.Equal("", MerchantClassifier.StripIgnores("代收", ignores));
    }

    [Fact]
    public void 拉丁渠道詞要有詞尾邊界_amazon_不准被剝成_mazon()
    {
        Assert.Equal(0, MerchantClassifier.IndexOfToken("a- 中油", "a"));       // 後面是分隔符 ⇒ 剝得掉
        Assert.Equal(-1, MerchantClassifier.IndexOfToken("amazon 台灣", "a"));  // 後面是字母 ⇒ 不准剝
        Assert.Equal(0, MerchantClassifier.IndexOfToken("代收－全家", "代收")); // 中文沒有詞邊界，只要求左邊
    }

    [Fact]
    public void 整列都是渠道名的商家不准整個消失()
    {
        var row = One([Ignore("代收"), Ignore("全盈支付"), R("全家", 11)], "代收－全盈支付");
        Assert.Null(row.CategoryId);                                            // 認不到，但這一列要還在清單上
        Assert.Equal(SuggestionSource.None, row.Source);
    }

    [Fact]
    public void 渠道詞撞關鍵字時關鍵字要贏()
    {
        // 「一卡通」同時是交通關鍵字與支付渠道名（本機真實情況）。
        var row = One([Ignore("一卡通"), R("一卡通", 22)], "一卡通－台北車站");
        Assert.Equal(22, row.CategoryId);
    }

    // ---------------------------------------------------------------- 比對

    [Fact]
    public void priority_小的先贏_代繳瓦斯費單上也會印中油()
    {
        var rules = new[] { R("瓦斯", 41, 100), R("中油", 21, 200) };
        Assert.Equal(41, One(rules, "代繳瓦斯費－中油 180XXX206").CategoryId);   // 那是代繳瓦斯費，不是去加油
    }

    [Fact]
    public void 外送要比_uber_那條先贏否則會被出租接駁搶走()
    {
        var rules = new[] { R("UberEats", 13, 150), R("Uber", 23, 200) };
        Assert.Equal(13, One(rules, "UBER EATS 台北").CategoryId);
        Assert.Equal(23, One(rules, "Uber 台灣").CategoryId);
    }

    [Fact]
    public void exact_要勝過_contains_不管_priority()
    {
        var rules = new[] { R("coffee", 12, 10), R("coffee shop", 11, 300, RuleMatch.Exact) };
        Assert.Equal(11, One(rules, "COFFEE SHOP").CategoryId);                 // 精確比對那條 priority 比較大也照樣贏
        Assert.Equal(12, One(rules, "我的 coffee 店").CategoryId);               // 打不中精確就退回 contains
    }

    [Fact]
    public void glob_讓_險_那種寫法真的有意義()
    {
        var rules = new[] { R("?險", 80, 200, RuleMatch.Glob) };
        Assert.Equal(80, One(rules, "郵險").CategoryId);
        Assert.Null(One(rules, "東亞郵險").CategoryId);                          // ? 只是一個字
    }

    [Fact]
    public void 停用的規則不准參與比對()
        => Assert.Null(One([R("中油", 21, enabled: false)], "中油 關新").CategoryId);

    [Fact]
    public void 正規化後是空的關鍵字不准變成萬能比對()
        => Assert.Null(One([R("－", 21), R("＊＊", 22)], "任何商家").CategoryId);

    // ---------------------------------------------------------------- 紅標（B2 的解藥）

    [Fact]
    public void 規則目標不是最底層要紅標跳過而不是靜默()
    {
        var output = MerchantClassifier.Run(new ClassifyInput([new EntryFact("中餐", "expense", 1)],
                                                              [R("中餐", 901)], Nodes));
        var b = Assert.Single(output.Broken);
        Assert.Equal(BrokenReason.TargetNotLeaf, b.Reason);
        Assert.Null(Assert.Single(output.Rows).CategoryId);
    }

    [Fact]
    public void 規則目標被刪掉要報得出來()
    {
        var output = MerchantClassifier.Run(new ClassifyInput([new EntryFact("中餐", "expense", 1)],
                                                              [R("中餐", 99999)], Nodes));
        Assert.Equal(BrokenReason.CategoryGone, Assert.Single(output.Broken).Reason);
    }

    [Fact]
    public void 規則的方向跟目標分類方向相反要紅標()
    {
        // 「薪資 → 薪資收入／本薪」寫成 expense 就是這種形狀：EnsureLeaf 只擋父類不擋方向，所以靠這裡擋（B7）。
        var output = MerchantClassifier.Run(new ClassifyInput([new EntryFact("薪資 倍利", "expense", 1)],
                                                              [R("薪資", 902, kind: "expense")], Nodes));
        Assert.Equal(BrokenReason.KindMismatch, Assert.Single(output.Broken).Reason);
    }

    [Fact]
    public void 反向的規則不准去命中另一側的商家()
    {
        var row = One([R("薪資", 902, kind: "income")], "薪資 倍利");           // 這列是 expense
        Assert.Null(row.CategoryId);
    }

    // ---------------------------------------------------------------- 群組與預勾

    [Fact]
    public void 同一個商家的收支兩側要分成兩列()
    {
        var output = MerchantClassifier.Run(new ClassifyInput(
            [new EntryFact("全聯", "expense", 4), new EntryFact("全聯", "income", 1)], [], Nodes));
        Assert.Collection(output.Rows.OrderBy(r => r.Kind),
                          r => Assert.Equal("expense", r.Kind),
                          r => Assert.Equal("income", r.Kind));
    }

    [Fact]
    public void 無商家那一群要照_null_成群()
    {
        var output = MerchantClassifier.Run(new ClassifyInput(
            [new EntryFact(null, "expense", 2), new EntryFact(null, "expense", 1)], [], Nodes));
        var row = Assert.Single(output.Rows);
        Assert.True(row.NoMerchant);
        Assert.Equal(3, row.Count);
        Assert.Empty(row.RawVariants);                                          // 套用時要打 merchant IS NULL
    }

    [Fact]
    public void 只有_seed_user_規則命中才預勾_ai_來的永不預勾()
    {
        Assert.True(One([R("中油", 21, source: RuleSource.User)], "中油").PreApply);
        Assert.True(One([R("中油", 21, source: RuleSource.Seed)], "中油").PreApply);
        var ai = One([R("中油", 21, source: RuleSource.Advisor)], "中油");
        Assert.False(ai.PreApply);
        Assert.Equal(SuggestionSource.Advisor, ai.Source);
    }

    [Fact]
    public void 本機真實商家要能預勾()
    {
        // CLASSIFICATION.md §0 那幾筆「認到了卻至今未分類」的，逐條鎖住。
        var rules = new[]
        {
            Ignore("代收"), Ignore("全盈支付"), Ignore("連加"), Ignore("街口電支"), Ignore("ＴＷＱＲ"),
            R("全家", 11), R("統一超商", 11), R("中華電信", 51), R("SUKIYA", 12), R("全聯", 310),
        };
        Assert.Equal(11, One(rules, "代收－全盈支付－全家便利商店").CategoryId);
        Assert.Equal(11, One(rules, "代收－連加＊統一超商").CategoryId);
        Assert.Equal(51, One(rules, "街口電支－中華電信TAIPEI").CategoryId);
        Assert.Equal(12, One(rules, "代收－ＳＵＫＩＹＡ 新竹關新店").CategoryId);
        Assert.Equal(310, One(rules, "代收－全聯").CategoryId);
        Assert.Null(One(rules, "代收－全支付ＴＷＱＲ跨機構購物交易").CategoryId);   // 這 2 筆真的認不出，要讓人自己選
    }
}
