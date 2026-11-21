using dFinance.Data;
using Xunit;

namespace dFinance.Tests;

/// <summary>
/// 分類報表的樹結構、小計、排序、展開。這些規則一旦壞掉，報表上的數字會「看起來正常但是錯」，
/// 所以要靠測試鎖住，不能只有 UI 按一按。
/// </summary>
public static class PivotTreeTests
{
    static LedgerStore.PivotCategory Cat(long id, string path, long? parent, int depth)
        => new(id, parent, "expense", depth, path.Split('／')[^1], path);

    static LedgerStore.PivotCell Cell(long id, string month, long minor) => new(id, month, minor);

    static readonly LedgerStore.PivotCategory[] Tree =
    [
        Cat(1, "餐飲", null, 0), Cat(2, "餐飲／外食", 1, 1), Cat(3, "餐飲／外送", 1, 1),
        Cat(4, "交通", null, 0), Cat(5, "交通／車票", 4, 1),
    ];

    [Fact]
    public static void 父列小計是直屬加整個子樹()
    {
        var cells = new[] { Cell(1, "2026-01", 500), Cell(2, "2026-01", 700), Cell(3, "2026-01", 200), Cell(2, "2026-02", 100) };
        var roots = PivotTree.Build(Tree, cells, "未分類");

        Assert.Single(roots);                                 // 交通整棹 0 → 不進表
        var 餐飲 = roots.Single(n => n.Id == 1);
        Assert.Equal(1_400, 餐飲.Cell("2026-01"));             // 直屬 500 + 外食 700 + 外送 200
        Assert.Equal(100, 餐飲.Cell("2026-02"));               // 只有外食有數
        Assert.Equal(1_500, 餐飲.Total);
        Assert.Equal(2, 餐飲.Children.Count);
    }

    [Fact]
    public static void 整棵沒資料的頂層不進表_未分類另挂一列()
    {
        var roots = PivotTree.Build(Tree, [Cell(0, "2026-01", 700)], "未分類");
        Assert.Single(roots);                                 // 餐飲/交通這棵都是 0 → 不進表
        Assert.Equal("未分類", roots[0].Name);
        Assert.Equal(700, roots[0].Total);
    }

    [Fact]
    public static void 按某欄排序會讓父列帶著子列一起動()
    {
        var cells = new[] { Cell(2, "2026-01", 300), Cell(3, "2026-01", 900), Cell(5, "2026-01", 1_500) };
        var roots = PivotTree.Build(Tree, cells, "未分類");
        var expanded = new HashSet<long> { 1, 4 };

        PivotTree.Sort(roots, "2026-01", descending: true);
        var visible = PivotTree.Visible(roots, expanded);

        Assert.Equal(["交通", "車票", "餐飲", "外送", "外食"], visible.Select(n => n.Name));
    }

    [Fact]
    public static void 子項只在自己parent底下排序()
    {
        var cells = new[] { Cell(2, "2026-01", 100), Cell(3, "2026-01", 900), Cell(5, "2026-01", 500) };
        var roots = PivotTree.Build(Tree, cells, "未分類");

        PivotTree.Sort(roots, "2026-01", descending: false);   // 由小到大

        var 餐飲 = roots.Single(n => n.Id == 1);
        var 交通 = roots.Single(n => n.Id == 4);
        Assert.Equal(["外食", "外送"], 餐飲.Children.Select(n => n.Name));    // 100 < 900
        Assert.Equal(500, 交通.Total);                               // 這一層由小到大：500 < 1,000
        Assert.Equal("交通", roots[0].Name);           // 這一層的最小才是餐飲
    }

    [Fact]
    public static void 展開只把子列插在自己那條父列下面()
    {
        var cells = new[] { Cell(1, "2026-01", 100), Cell(4, "2026-01", 200) };
        var roots = PivotTree.Build(Tree, cells, "未分類");

        var collapsed = PivotTree.Visible(roots, new HashSet<long>());
        Assert.Equal(["餐飲", "交通"], collapsed.Select(n => n.Name));

        var expanded = PivotTree.Visible(roots, new HashSet<long> { 1 });
        Assert.Equal(["餐飲", "外食", "外送", "交通"], expanded.Select(n => n.Name));
        Assert.Equal("▶ 交通", collapsed.Single(n => n.Id == 4).Label);
        Assert.Equal("▼ 餐飲", expanded[0].Label);             // 有子類才有 ▼/▶，頂層縮排是 0
        Assert.Equal(new string('　', 3) + " 外食", expanded[1].Label);   // 子列：三個全形空白（兩個縮排 + 佔位的 ▶ 欄位）
    }

    [Fact]
    public static void 月欄由舊到新且只列出有資料的月()
    {
        var months = PivotTree.Months([Cell(1, "2026-03", 1), Cell(1, "2026-01", 1), Cell(4, "2026-02", 1)]);
        Assert.Equal(["2026-01", "2026-02", "2026-03"], months);
    }
}
