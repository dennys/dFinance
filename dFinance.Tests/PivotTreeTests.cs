using dFinance.Data;
using Xunit;

namespace dFinance.Tests;

/// <summary>
/// 分類報表的樹結構、小計、排序、展開、總計。這些規則一旦壞掉，報表上的數字會「看起來正常但是錯」，
/// 所以要靠測試鎖住，不能只有 UI 按一按。
/// </summary>
public static class PivotTreeTests
{
    static LedgerStore.PivotCategory Cat(long id, string path, long? parent)
        => new(id, parent, "expense", path.Split('／').Length - 1, path.Split('／')[^1], path);
    static LedgerStore.PivotCategory CatIn(long id, string path, long? parent)
        => new(id, parent, "income", path.Split('／').Length - 1, path.Split('／')[^1], path);

    static LedgerStore.PivotCell Cell(long id, string month, long minor) => new(id, month, minor);

    static readonly LedgerStore.PivotCategory[] Tree =
    [
        Cat(1, "餐飲", null), Cat(2, "餐飲／外食", 1), Cat(3, "餐飲／外送", 1),
        Cat(4, "交通", null), Cat(5, "交通／車票", 4),
        CatIn(6, "薪資收入", null), CatIn(7, "薪資收入／本薪", 6),
    ];

    static List<PivotNode> Build(params LedgerStore.PivotCell[] cells)
        => PivotTree.Build(Tree, cells, "未分類", "支出", "收入");

    [Fact]
    public static void 最上面是支收兩塊_分類挂在塊底下_小計含整個子樹()
    {
        var cells = new[] { Cell(1, "2026-01", 500), Cell(2, "2026-01", 700), Cell(3, "2026-01", 200), Cell(2, "2026-02", 100) };
        var blocks = Build(cells);

        Assert.Single(blocks);                                    // 只有支出側有資料 → 收入那块不要出現
        var 支出 = blocks[0];
        Assert.Equal(PivotTree.ExpenseSideId, 支出.Id);
        Assert.Equal(1_500, 支出.Total);                          // 塊的小計＝這側全部相加

        var 餐飲 = 支出.Children.Single();
        Assert.Equal(1_400, 餐飲.Cell("2026-01"));                // 直屬 500 + 外食 700 + 外送 200
        Assert.Equal(100, 餐飲.Cell("2026-02"));                  // 只有外食有數
        Assert.Equal(1_500, 餐飲.Total);
        Assert.Equal(2, 餐飲.Children.Count);
        Assert.Equal([0, 1, 2], new[] { 支出.Depth, 餐飲.Depth, 餐飲.Children[0].Depth });   // 縮排多一層是因為外面套了塊
    }

    [Fact]
    public static void 百分比的分母是同側塊不是收支相抵後的數()
    {
        // 支出塊 1,000（外食 700 + 外送 300）、收入塊 6,300 → 收−支 +5,300。
        // 拿收−支當分母，同一筆外食的占比會跟著當月有沒有薪資而變 —— 那就不是占比了。
        var blocks = Build(Cell(2, "2026-01", 700), Cell(3, "2026-01", 300), Cell(6, "2026-01", 6_300));
        var 外食 = blocks[0].Children.Single().Children.Single(c => c.Id == 2);
        Assert.Equal("70.0%", PivotTree.PercentOf(外食, blocks, "2026-01"));
        Assert.Equal("70.0%", PivotTree.PercentOf(外食, blocks, PivotTree.TotalKey));      // 合計欄：分母是塊的合計
        Assert.Equal("100.0%", PivotTree.PercentOf(blocks[0], blocks, "2026-01"));      // 塊自己恒 100%
        Assert.Equal("100.0%", PivotTree.PercentOf(blocks[1], blocks, "2026-01"));
    }

    [Fact]
    public static void 塊該月為零不給百分比_不是零百分比也不會炸()
    {
        // 2 月刷 1,000 又全額退款：塊那個月是 0，除不出來（顯示 «—» 而不是 0%，0 代筆是「沒有」）。
        var blocks = Build(Cell(2, "2026-01", 1_000), Cell(2, "2026-02", 1_000), Cell(3, "2026-02", -1_000));
        var 外食 = blocks[0].Children.Single().Children.Single(c => c.Id == 2);
        Assert.Equal("100.0%", PivotTree.PercentOf(外食, blocks, "2026-01"));
        Assert.Equal(PivotTree.PercentNa, PivotTree.PercentOf(外食, blocks, "2026-02"));
    }

    [Fact]
    public static void 退款是負的百分比也保留負號()
    {
        // 支出塊 = 餐飲(1,000 − 200) + 交通(200) = 1,000；分母取絕對值，符號由分子決定。
        var blocks = Build(Cell(2, "2026-01", 1_000), Cell(3, "2026-01", -200), Cell(5, "2026-01", 200));
        var 餐飲 = blocks[0].Children.Single(c => c.Id == 1);
        var 外送 = 餐飲.Children.Single(c => c.Id == 3);
        Assert.Equal("80.0%", PivotTree.PercentOf(餐飲, blocks, "2026-01"));
        Assert.Equal("-20.0%", PivotTree.PercentOf(外送, blocks, "2026-01"));
        Assert.Equal("100.0%", PivotTree.PercentOf(blocks[0], blocks, "2026-01"));      // 塊自己不會變 −100%
    }

    [Fact]
    public static void 總計列收支不給百分比()
    {
        var cells = new[] { Cell(2, "2026-01", 700), Cell(6, "2026-01", 6_300) };
        var blocks = Build(cells);
        var sum = PivotTree.TotalRow(cells, "總計")!;
        Assert.Equal("", PivotTree.PercentOf(sum, blocks, "2026-01"));      // 收−支不是任何塊的一部分
        Assert.Equal("", PivotTree.PercentOf(sum, blocks, PivotTree.TotalKey));
    }

    [Fact]
    public static void 整年加起來是零但每月有數的還是要進表()
    {
        // 1 月刷 1,000、2 月全額退款（退款是負的支出）：整棵相加是 0，但兩個月都有數。
        // 拿 Total 判斷會讓整棵從報表消失，而最下面的總計列（另算的）還是會顯示那兩個月。
        var blocks = Build(Cell(2, "2026-01", 1_000), Cell(2, "2026-02", -1_000));
        var 外食 = blocks[0].Children.Single().Children.Single(c => c.Id == 2);
        Assert.Equal(1_000, 外食.Cell("2026-01"));
        Assert.Equal(-1_000, 外食.Cell("2026-02"));
        Assert.Equal(0, 外食.Total);
        Assert.Equal(0, blocks[0].Total);
    }

    [Fact]
    public static void 整棵沒資料的頂層不進表_未分類挂在支出塊下()
    {
        var blocks = Build(Cell(0, "2026-01", 700));
        Assert.Single(blocks);                                    // 餐飲/交通都是 0 → 不進表
        Assert.Equal("未分類", Assert.Single(blocks[0].Children).Name);
        Assert.Equal(700, blocks[0].Total);
    }

    [Fact]
    public static void 支出塊永遠在收入塊上面_按金額排也不會對調()
    {
        var blocks = Build(Cell(1, "2026-01", 100), Cell(6, "2026-01", 50_000));
        Assert.Equal(["支出", "收入"], blocks.Select(n => n.Name));

        PivotTree.Sort(blocks, PivotTree.TotalKey, descending: true);   // 收入那塊大 500 倍也不能排到前面
        Assert.Equal(["支出", "收入"], blocks.Select(n => n.Name));
        Assert.Equal(50_000, blocks[1].Total);
    }

    [Fact]
    public static void 按某欄排序會讓父列帶著子列一起動()
    {
        var cells = new[] { Cell(2, "2026-01", 300), Cell(3, "2026-01", 900), Cell(5, "2026-01", 1_500) };
        var blocks = Build(cells);
        var expanded = new HashSet<long> { PivotTree.ExpenseSideId, 1, 4 };

        PivotTree.Sort(blocks, "2026-01", descending: true);
        var visible = PivotTree.Visible(blocks, expanded);

        Assert.Equal(["支出", "交通", "車票", "餐飲", "外送", "外食"], visible.Select(n => n.Name));
    }

    [Fact]
    public static void 子項只在自己parent底下排序()
    {
        var blocks = Build(Cell(2, "2026-01", 100), Cell(3, "2026-01", 900), Cell(5, "2026-01", 500));
        PivotTree.Sort(blocks, "2026-01", descending: false);     // 由小到大

        var 支出 = blocks.Single();
        var 餐飲 = 支出.Children.Single(n => n.Id == 1);
        var 交通 = 支出.Children.Single(n => n.Id == 4);
        Assert.Equal(["外食", "外送"], 餐飲.Children.Select(n => n.Name));    // 100 < 900：只在本 parent 底下排
        Assert.Equal(["交通", "餐飲"], 支出.Children.Select(n => n.Name));    // 這一層 500 < 1,000
        Assert.Equal(500, 交通.Total);
    }

    [Fact]
    public static void 展開只把子列插在自己那條父列下面()
    {
        var blocks = Build(Cell(1, "2026-01", 100), Cell(4, "2026-01", 200));

        var collapsed = PivotTree.Visible(blocks, new HashSet<long>());
        Assert.Equal(["支出"], collapsed.Select(n => n.Name));

        var expanded = PivotTree.Visible(blocks, new HashSet<long> { PivotTree.ExpenseSideId, 1 });
        Assert.Equal(["支出", "餐飲", "外食", "外送", "交通"], expanded.Select(n => n.Name));
        Assert.Equal("▼ 支出", expanded[0].Label);                        // 塊本身也能展開
        Assert.Equal(new string('　', 2) + "▼ 餐飲", expanded[1].Label);   // 分類縮排一層（外面套了塊）
        Assert.Equal(new string('　', 4) + "　 外食", expanded[2].Label);  // 子類縮排兩層
    }

    [Fact]
    public static void 總計列是收減支而不是兩側金額相加()
    {
        // 由 LedgerStore 算好符號（收入正、支出負、退款負負得正），這裡只負責貼在最下面一列
        var net = new[] { Cell(0, "2026-01", 4_000), Cell(0, "2026-02", -1_500) };
        var sum = PivotTree.TotalRow(net, "總計")!;

        Assert.Equal(4_000, sum.Cell("2026-01"));                 // 當月賺
        Assert.Equal(-1_500, sum.Cell("2026-02"));                // 當月虧
        Assert.Equal(2_500, sum.Total);
        Assert.Equal(PivotTree.TotalRowId, sum.Id);
        Assert.True(sum.IsSummary);
        Assert.False(sum.HasChildren);
        Assert.Null(PivotTree.TotalRow([], "總計"));               // 沒資料就不要有一列 0
    }

    [Fact]
    public static void 月欄由舊到新且只列出有資料的月()
    {
        var months = PivotTree.Months([Cell(1, "2026-03", 1), Cell(1, "2026-01", 1), Cell(4, "2026-02", 1)]);
        Assert.Equal(["2026-01", "2026-02", "2026-03"], months);
    }
}
