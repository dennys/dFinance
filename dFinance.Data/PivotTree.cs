namespace dFinance.Data;

/// <summary>
/// 分類報表的一列（樹上的一個節點）。UI 直接綁這個：<see cref="Label"/> 是帶縮排與 ▶/▼ 的顯示字，
/// <see cref="Cells"/> 給月欄綁。金額一律 long minor，小計已經在 <see cref="PivotTree.Build"/> 加好了。
/// </summary>
public sealed class PivotNode
{
    public required long Id { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Kind { get; init; }
    public int Depth { get; set; }
    public bool IsExpanded { get; set; }

    public List<PivotNode> Children { get; } = [];
    public Dictionary<string, long> Cells { get; } = [];          // 月 → minor（已含整個子樹）
    public long Total { get; set; }

    public bool HasChildren => Children.Count > 0;
    public long Cell(string month) => Cells.TryGetValue(month, out var v) ? v : 0;

    /// <summary>
    /// 分類欄的顯示字：全形空白縮排 + ▶/▼ + 名稱（跟 <c>CategoryRow.Indented</c> 同一套做法，不用 converter）。
    /// shortcut: 這個字只在重建可見列時會被重讀，所以展開/收合/排序都重建整份清單（幾百列而已，比做 INPC 划算）。
    /// </summary>
    public string Label => new string('　', Depth * 2) + (HasChildren ? (IsExpanded ? "▼ " : "▶ ") : "　 ") + Name;
}

/// <summary>
/// 把 <see cref="LedgerStore.CategoryPivot"/> 的原料組成樹、算小計、排序、展開成可見列。
/// 純函式（不碰 WPF），所以這些規則能用測試鎖住 —— 小計或「子項只在自己 parent 底下排」被改壞
/// 就是 quietly 記錯帳，看不出來。
/// </summary>
public static class PivotTree
{
    public const string TotalKey = "total";                       // 合計欄的排序身分（欄頭是給人看的，會翻譯）
    public const string NameKey = "_name";                        // 分類欄：照名稱/路徑排

    /// <summary>有資料的月份，由舊到新（欄位就是這些，需求 1）。</summary>
    public static List<string> Months(IReadOnlyList<LedgerStore.PivotCell> cells)
        => cells.Select(c => c.Month).Distinct(StringComparer.Ordinal).OrderBy(m => m, StringComparer.Ordinal).ToList();

    /// <summary>
    /// 建樹 + 算小計：一個節點某個月的數字 = 自己的交易 + 整個子樹同月的和（需求 5）；
    /// 整棵都沒資料的頂層不進表。<paramref name="uncategorizedName"/> 是「未分類」那列的顯示字（要跟著語系）。
    /// </summary>
    public static List<PivotNode> Build(IReadOnlyList<LedgerStore.PivotCategory> categories,
                                        IReadOnlyList<LedgerStore.PivotCell> cells,
                                        string uncategorizedName)
    {
        var byId = new Dictionary<long, PivotNode>();
        var roots = new List<PivotNode>();
        foreach (var c in categories)                             // SQL 已排過：父一定比子早出現
        {
            var node = new PivotNode { Id = c.Id, Name = c.Name, Path = c.Path, Kind = c.Kind, Depth = c.Depth };
            byId[c.Id] = node;
            if (c.ParentId is { } pid && byId.TryGetValue(pid, out var parent)) parent.Children.Add(node);
            else roots.Add(node);
        }
        if (cells.Any(c => c.CategoryId == 0))                    // SQL 把 category_id IS NULL 給成 id 0
        {
            var ghost = new PivotNode { Id = 0, Name = uncategorizedName, Path = uncategorizedName, Kind = "" };
            byId[0] = ghost;
            roots.Add(ghost);
        }

        foreach (var cell in cells)
            if (byId.TryGetValue(cell.CategoryId, out var node)) node.Cells[cell.Month] = cell.TotalMinor;

        foreach (var root in roots) Sum(root);
        return roots.Where(n => n.Total != 0).ToList();
    }

    static long Sum(PivotNode node)
    {
        var own = node.Cells.Values.Sum();                        // 先存自己那批，下面會被子樹蓋掉
        foreach (var child in node.Children)
        {
            Sum(child);
            foreach (var (month, value) in child.Cells)
                node.Cells[month] = node.Cells.TryGetValue(month, out var cur) ? cur + value : value;
        }
        node.Total = own + node.Children.Sum(c => c.Total);
        return node.Total;
    }

    /// <summary>
    /// 排這一層，再遞迴排每一層的子節點 —— 所以子項只在自己 parent 底下排序（需求 3），
    /// 父項帶著它的子項一起移動（需求 2）。同額就照名稱比，結果可預期。
    /// </summary>
    public static void Sort(List<PivotNode> nodes, string key, bool descending)
    {
        Comparison<PivotNode> byValue = key == NameKey
            ? (a, b) => string.CompareOrdinal(a.Path, b.Path)
            : (a, b) => a.Cell(key) == b.Cell(key)
                ? string.CompareOrdinal(a.Path, b.Path)
                : a.Cell(key).CompareTo(b.Cell(key));
        nodes.Sort(descending ? (a, b) => byValue(b, a) : byValue);
        foreach (var n in nodes) Sort(n.Children, key, descending);
    }

    /// <summary>
    /// 照現在的先後順序，把展開節點的子列插進可見列（需求 4）。
    /// 展開狀態由呼叫端持有（不是存在節點上），所以排序重排清單時不會把它弄丟。
    /// </summary>
    public static List<PivotNode> Visible(IReadOnlyList<PivotNode> roots, ISet<long> expanded)
    {
        var rows = new List<PivotNode>();
        void Walk(PivotNode n)
        {
            n.IsExpanded = expanded.Contains(n.Id);
            rows.Add(n);
            if (n.IsExpanded) foreach (var child in n.Children) Walk(child);
        }
        foreach (var root in roots) Walk(root);
        return rows;
    }
}
