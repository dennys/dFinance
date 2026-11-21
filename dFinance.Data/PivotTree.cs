using System.ComponentModel;
using System.Globalization;

namespace dFinance.Data;

/// <summary>
/// 分類帳報表的一列（樹上的一個節點）。UI 直接綁這個：<see cref="Label"/> 是帶縮排與 ▶/▼ 的顯示字，
/// <see cref="Cells"/> 給月欄綁。金額一律 long minor，小計已經在 <see cref="PivotTree.Build"/> 加好了。
/// Id 是小於 0 的是合成列（支出／收入那兩塊、總計），不是真正的分類：<see cref="IsSummary"/> 給 UI 粗體、不給 drill down。
/// </summary>
public sealed class PivotNode : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public required long Id { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Kind { get; init; }
    public int Depth { get; set; }

    bool _isExpanded;
    /// <summary>展開狀態。收合/展開走差額更新（不重建整張表），所以格子裡的 +/− 要靠自己發通知才會換。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
        }
    }

    public List<PivotNode> Children { get; } = [];
    public Dictionary<string, long> Cells { get; } = [];          // 月 → minor（已含整個子樹）
    public long Total { get; set; }

    public bool HasChildren => Children.Count > 0;
    public bool IsSummary => Id < 0;
    public long Cell(string month) => Cells.GetValueOrDefault(month);

    /// <summary>
    /// 分類欄的顯示字：全形空白縮排 + 展開符 + 名稱（跟 <c>CategoryRow.Indented</c> 同一套做法，不用 converter）。
    /// 用「− / +」不用實心三角：實心三角在 13pt 的格子裡比分類名還抢眼（使用者反映過）。
    /// 展開/收合只動差額，這一格不會被重讀，所以 <see cref="IsExpanded"/> 改變時要發 <see cref="PropertyChanged"/>。
    /// </summary>
    public string Label => new string('　', Depth * 2) + (HasChildren ? (IsExpanded ? "− " : "+ ") : "　 ") + Name;
}

/// <summary>
/// 把 <see cref="LedgerStore.CategoryPivot"/> 的原料組成樹、算小計、排序、展開成可見列。
/// 純函式（不碰 WPF），所以這些規則能用測試鎖住 —— 小計、「子項只在自己 parent 底下排」、
/// 「總計是收−支」被改壞就是 quietly 記錯帳，看不出來。
/// </summary>
public static class PivotTree
{
    public const string TotalKey = "total";                       // 合計欄的排序身分（欄頭是給人看的，會翻譯）
    public const string NameKey = "_name";                        // 分類欄：照名稱/路徑排

    // 合成列的 id（0 是「未分類」那棵，真正存在於 category 表以外）：由小到大排就是支出 → 收入。
    public const long ExpenseSideId = -3;
    public const long IncomeSideId = -2;
    public const long TotalRowId = -1;                            // 最下面那列總計（收−支）

    public const string ExpenseSide = "expense";
    public const string IncomeSide = "income";

    /// <summary>有資料的月份，由舊到新（欄位就是這些，需求 1）。</summary>
    public static List<string> Months(IReadOnlyList<LedgerStore.PivotCell> cells)
        => cells.Select(c => c.Month).Distinct(StringComparer.Ordinal).OrderBy(m => m, StringComparer.Ordinal).ToList();

    /// <summary>
    /// 建樹 + 算小計：一個節點某個月的數字 = 自己的交易 + 整個子樹同月的和（需求 5）；
    /// 整棵都沒資料的頂層不進表。最上面固定分兩塊 —— 支出（含未分類）在前、收入在後：
    /// 兩側同額混排會看不出這筆錢是花掉的還是收進來的，總計也就沒辦法當成「當月賺還是虧」看。
    /// </summary>
    public static List<PivotNode> Build(IReadOnlyList<LedgerStore.PivotCategory> categories,
                                        IReadOnlyList<LedgerStore.PivotCell> cells,
                                        string uncategorizedName, string expenseName, string incomeName)
    {
        var byId = new Dictionary<long, PivotNode>();
        var tops = new List<PivotNode>();
        foreach (var c in categories)                             // SQL 已排過：父一定比子早出現
        {
            var node = new PivotNode { Id = c.Id, Name = c.Name, Path = c.Path, Kind = c.Kind, Depth = c.Depth };
            byId[c.Id] = node;
            if (c.ParentId is { } pid && byId.TryGetValue(pid, out var parent)) parent.Children.Add(node);
            else tops.Add(node);
        }
        if (cells.Any(c => c.CategoryId == 0))                    // SQL 把 category_id IS NULL 給成 id 0
        {
            var ghost = new PivotNode { Id = 0, Name = uncategorizedName, Path = uncategorizedName, Kind = "" };
            byId[0] = ghost;
            tops.Add(ghost);
        }

        foreach (var cell in cells)
            if (byId.TryGetValue(cell.CategoryId, out var node)) node.Cells[cell.Month] = cell.TotalMinor;

        foreach (var top in tops) Sum(top);
        // 進不進表看「有沒有任何一個月有數」，不是看整棵加起來等不等 0：
        // 1 月刷 1,000、2 月全額退款那種加起來是 0，但兩個月都有數 —— 拿 Total 判斷會讓整棵從報表消失，
        // 而最下面的總計列（另算的）還是會顯示那兩個月，結果小計跟總計對不上。
        var alive = tops.Where(n => n.Cells.Values.Any(v => v != 0)).ToList();

        var sides = new List<PivotNode>();
        foreach (var (side, id, name) in new[] { (ExpenseSide, ExpenseSideId, expenseName), (IncomeSide, IncomeSideId, incomeName) })
        {
            var kids = alive.Where(n => SideOf(n.Kind) == side).ToList();
            if (kids.Count == 0) continue;                        // 這一側沒資料就不要有標題列
            var block = new PivotNode { Id = id, Name = name, Path = name, Kind = side };
            foreach (var k in kids) { BumpDepth(k); block.Children.Add(k); }
            sides.Add(block);
            MergeInto(block);                                     // 塊的小計＝這一側全部相加
        }
        return sides;
    }

    /// <summary>那一棵算哪一側：未分類（沒有 kind）跟一切非收入的都算支出側。</summary>
    static string SideOf(string kind) => kind == IncomeSide ? IncomeSide : ExpenseSide;

    static void BumpDepth(PivotNode n)
    {
        n.Depth++;
        foreach (var c in n.Children) BumpDepth(c);
    }

    /// <summary>
    /// 把子節點（已經含自己的子樹）的月份數字合進父節點。<see cref="Sum"/> 不能對同一棵跑兩次：
    /// 它會把子樹的數字合進父節點的 Cells，再跑一次就變成自己加自己（小計偷偷翻倍，看不出來）。
    /// </summary>
    static void MergeInto(PivotNode node)
    {
        foreach (var child in node.Children)
            foreach (var (month, value) in child.Cells)
                node.Cells[month] = node.Cells.TryGetValue(month, out var cur) ? cur + value : value;
        node.Total = node.Children.Sum(c => c.Total);
    }

    /// <summary>這一棵的小計：自己的直屬金額 + 整個子樹。</summary>
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
    /// 最下面那列總計：每個月是「收入 − 支出」（負的就是當月虧）。
    /// 符號由 <see cref="LedgerStore.CategoryPivot"/> 照交易 kind 算好貼過來 —— 分類標錯不會影響這個數。
    /// 它不受展開與排序影響，永遠在最後一列。
    /// </summary>
    public static PivotNode? TotalRow(IReadOnlyList<LedgerStore.PivotCell> netCells, string name)
    {
        if (netCells.Count == 0) return null;
        var row = new PivotNode { Id = TotalRowId, Name = name, Path = name, Kind = "" };
        foreach (var c in netCells) row.Cells[c.Month] = c.TotalMinor;
        row.Total = netCells.Sum(c => c.TotalMinor);
        return row;
    }

    /// <summary>
    /// 百分比模式的格子文字：這一列估「同側塊」（支出塊／收入塊）該月多少。
    /// <para>分母是<b>塊</b>而不是最下面那列「收−支」：收−支会被收入抵掉，同一筆消費的占比就會跟著
    /// 當月有沒有收入而變 —— 那是「相對盈虧」不是占比（块小計因此恒為 100%）。</para>
    /// <para>分母取絕對值、符號由分子決定：退款（負的支出）就是負的百分比，不會因為整塊被退款抵成
    /// 負數而翻正。塊自己固定 100.0%（那個月的塊被抵成負數也不會顯示 −100.0%）。</para>
    /// <para>塊該月是 0 → <see cref="PercentNa"/>，不是 0%：0 是「沒有」，這裡是「除不出來」。
    /// 最下面那列總計（收−支）不適用占比 → 空白。</para>
    /// 小數位固定 1、格式 InvariantCulture：介面可以换語系，數字的寫法不跟著换（跟 Money.Format 同一條規則）。
    /// </summary>
    public const string PercentNa = "—";

    /// <param name="column">欄的身分（月 <c>yyyy-MM</c> 或 <see cref="TotalKey"/>）；用欄而不是欄頭文字，
    /// 因為欄頭會翻译。</param>
    public static string PercentOf(PivotNode node, IReadOnlyList<PivotNode> roots, string column)
    {
        var block = BlockOf(node, roots);
        if (block is null) return "";                                  // 總計列：收−支没有「占比」這回事
        bool total = column == TotalKey;
        long denom = System.Math.Abs(total ? block.Total : block.Cell(column));
        if (denom == 0) return PercentNa;
        if (ReferenceEquals(node, block)) return "100.0%";
        var value = total ? node.Total : node.Cell(column);
        var pct = System.Math.Round(value * 100m / denom, 1, MidpointRounding.AwayFromZero);
        return pct.ToString("0.0", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>這一列所屬的塊（樹最上面那层）。不在這棵樹上（總計列）就回 null。</summary>
    static PivotNode? BlockOf(PivotNode node, IReadOnlyList<PivotNode> roots)
    {
        foreach (var root in roots) if (Contains(root, node)) return root;
        return null;
    }

    static bool Contains(PivotNode node, PivotNode target)
    {
        if (ReferenceEquals(node, target)) return true;
        foreach (var k in node.Children) if (Contains(k, target)) return true;
        return false;
    }

    /// <summary>一節點的所有子孫（收合時要把它們從可見列拿掉）。</summary>
    public static IEnumerable<PivotNode> Descendants(PivotNode n)
    {
        foreach (var c in n.Children) { yield return c; foreach (var d in Descendants(c)) yield return d; }
    }

    /// <summary>
    /// 排這一層，再遞迴排每一層的子節點 —— 所以子項只在自己 parent 底下排序（需求 3），
    /// 父項帶著它的子項一起移動（需求 2）。同額就照名稱比，結果可預期。
    /// 支出／收入那兩塊的先後是固定的（先看花費、再看收入）：按金額排不會把收入搬到支出上面。
    /// </summary>
    public static void Sort(List<PivotNode> nodes, string key, bool descending)
    {
        if (nodes.Any(n => n.IsSummary)) nodes.Sort((a, b) => a.Id.CompareTo(b.Id));
        else
        {
            Comparison<PivotNode> byValue = key == NameKey
                ? (a, b) => string.CompareOrdinal(a.Path, b.Path)
                : (a, b) => a.Cell(key) == b.Cell(key)
                    ? string.CompareOrdinal(a.Path, b.Path)
                    : a.Cell(key).CompareTo(b.Cell(key));
            nodes.Sort(descending ? (a, b) => byValue(b, a) : byValue);
        }
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
