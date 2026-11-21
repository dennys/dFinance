using System.Diagnostics;
using dFinance.Data.Localization;
using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;                      // ListSortDirection：欄頭排序箭頭
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using dFinance.Data;
using Microsoft.Data.Sqlite;

namespace dFinance;

/// <summary>
/// 首頁（accountId = null，dashboard）與帳戶分頁（傳入 accountId）共用這支控制項。
/// 開多個帳戶分頁對比時不會重讀：資料留在各自的 instance 裡，切回來只比 <see cref="LedgerStore.DataVersion"/>，
/// 髒了才重查。
/// </summary>
public partial class LedgerTab : UserControl
{
    readonly LedgerStore? _store;
    readonly long? _accountId;
    readonly bool _homeMode;
    long _loadedVersion = -1;
    bool _showingReport;
    long _reportVersion = -1;    bool _loadingFilter;                          // 灌篩選選項時別跟著重查

    readonly ObservableCollection<PivotNode> _pivotRows = [];
    List<PivotNode> _pivotRoots = [];
    List<string> _pivotMonths = [];
    readonly HashSet<long> _pivotExpanded = [];
    string _pivotSortKey = PivotTree.TotalKey;
    bool _pivotDesc = true;                       // 第一次點某欄都是由大到小：先看花費最多的那項
    int _pivotSpan = 12;                          // 報表月欄：近 N 個月，0 = 全部

    record AccountPick(long? Id, string Label);
    record CategoryPick(long? Id, string Label, bool Uncategorized);
    record StatusPick(string Mode, string Label);
    record RangePick(int Span, string Label);
    record DatePreset(string Mode, string Label);          // 顯示文字會跟著語系變，判斷一律看 Mode

    /// <summary>使用者要把某個帳戶再開一個分頁（由 MainWindow 接手）。</summary>
    public event Action<long, string>? OpenAccountRequested;

    /// <summary>報表格子 drill down：分類路徑與日期範圍（合計欄為 null，表示不限日期）。</summary>
    public event Action<string, string?, string?>? DrillDownRequested;

    public LedgerTab() => InitializeComponent();                       // 設計期

    public LedgerTab(SqliteConnection db, long? accountId, bool ledgerMode = false) : this()
    {
        _store = new LedgerStore(db);
        _accountId = accountId;
        _homeMode = accountId is null && !ledgerMode;
        BuildFilterOptions();
        BuildReportRange();

        if (_accountId is not null)
        {
            AccountBox.Visibility = AccountLabel.Visibility = OpenAccountButton.Visibility = Visibility.Collapsed;
            AccountHeaderText.Visibility = Visibility.Visible;
            AccountCol.Visibility = Visibility.Collapsed;      // 都同一個帳戶，欄位留著給主頁（全部帳戶）用
        }
        else if (_homeMode)
        {
            FilterBar.Visibility = ActionBar.Visibility = LedgerPanel.Visibility = Visibility.Collapsed;
            HomePanel.Visibility = Visibility.Visible;
        }
        else HomePanel.Visibility = Visibility.Collapsed;

        Loaded += (_, _) => Reload();
        IsVisibleChanged += (_, _) => RefreshIfStale();
    }

    /// <summary>分頁切回來時用：沒人寫過資料就不碰資料庫。</summary>
    public void RefreshIfStale()
    {
        if (_store is null) return;
        if (_loadedVersion != LedgerStore.DataVersion) Reload();
        if (_showingReport && _reportVersion != LedgerStore.DataVersion) ShowCategoryReport();
    }

    /// <summary>給 --uismoke 看的：真的載入到幾列（0 就是篩選或接庫有問題）。</summary>
    public int RowCount => LedgerGrid.Items.Count;

    /// <summary>給 --uismoke 看的：首頁淨值表載入幾個帳戶（首頁沒有帳本清單，RowCount 永遠是 0）。</summary>
    public int HomeAccountCount => HomeNetWorthGrid.Items.Count;

    /// <summary>
    /// 給 <c>--uismoke</c>：開報表 → 展開第一個有子類的分類 → 按最後一個月欄排序 → 收合。
    /// 展開沒多列、或排序後空了，就是展開/排序壞了（這兩條路只有 UI 模得到）。
    /// </summary>
    public int SmokeReportInteract()
    {
        ShowCategoryReport();
        var parent = _pivotRoots.FirstOrDefault(n => n.HasChildren);
        if (parent is null) return 0;
        _pivotExpanded.Add(parent.Id);
        RebuildPivotRows();
        var expandedCount = _pivotRows.Count;
        _pivotSortKey = _pivotMonths.Count > 0 ? _pivotMonths[^1] : PivotTree.TotalKey;
        _pivotDesc = true;
        RebuildPivotRows();
        _pivotExpanded.Clear();
        RebuildPivotRows();
        if (expandedCount <= _pivotRows.Count)
            throw new InvalidOperationException($"展開「{parent.Name}」後列數沒增加（{expandedCount} → {_pivotRows.Count}）");
        if (_pivotRows.Count == 0) throw new InvalidOperationException("排序後報表空了");
        return expandedCount - _pivotRows.Count;
    }

    /// <summary>匯入完或外部改過資料時叫這個（當前分頁馬上更新，沒顯示的分頁等切過去再說）。</summary>
    public void RefreshNow()
    {
        if (_store is null) return;
        BuildFilterOptions();
        Reload();
    }

    // ---------------------------------------------------------------- 篩選列

    void BuildFilterOptions()
    {
        if (_store is not { } s) return;
        _loadingFilter = true;
        try
        {
            PresetBox.ItemsSource = new[]
            {
                new DatePreset("month", Loc.T("Common_ThisMonth")), new DatePreset("last", Loc.T("Common_LastMonth")),
                new DatePreset("year", Loc.T("Common_ThisYear")), new DatePreset("all", Loc.T("Common_All")),
                new DatePreset("custom", Loc.T("Common_Custom")),
            };
            PresetBox.SelectedIndex = 0;

            var accounts = new List<AccountPick> { new(null, Loc.T("Ledger_AllAccounts")) };
            // 已歸檔的也列出來（不列就再也筛不到它），但标清楚，别让人以为是个还在用的帳戶
            accounts.AddRange(s.Accounts(includeArchived: true).Select(a => new AccountPick(
                a.Id, (a.Currency == "TWD" ? a.Name : $"{a.Name}（{a.Currency}）") + (a.IsArchived ? Loc.T("Ledger_ArchivedSuffix") : ""))));
            AccountBox.ItemsSource = accounts;
            AccountBox.SelectedItem = accounts.FirstOrDefault(a => a.Id == _accountId) ?? accounts[0];

            var cats = new List<CategoryPick> { new(null, Loc.T("Ledger_AllCategories"), false), new(null, Loc.T("Ledger_Uncategorized"), true) };
            cats.AddRange(s.Categories("expense").Select(c => new CategoryPick(c.Id, c.Name, false)));
            cats.AddRange(s.Categories("income").Select(c => new CategoryPick(c.Id, c.Name, false)));
            CategoryBox.ItemsSource = cats;
            CategoryBox.SelectedIndex = 0;

            StatusBox.ItemsSource = new[]
            {
                new StatusPick("ledger", Loc.T("Ledger_Status_Ledger")), new StatusPick("imported", Loc.T("Ledger_Status_Imported")),
                new StatusPick("orphan", Loc.T("Ledger_Status_Orphan")), new StatusPick("dupgroup", Loc.T("Ledger_Status_DupGroup")),
                new StatusPick("duplicate", Loc.T("Ledger_Status_Duplicate")), new StatusPick("ignored", Loc.T("Ledger_Status_Ignored")),
                new StatusPick("deleted", Loc.T("Ledger_Status_Deleted")), new StatusPick("all", Loc.T("Ledger_Status_All")),
            };
            StatusBox.SelectedIndex = 0;
        }
        finally { _loadingFilter = false; }
    }

    void Preset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingFilter || PresetBox.SelectedItem is not DatePreset p) return;
        var today = DateOnly.FromDateTime(DateTime.Today);
        _loadingFilter = true;
        try
        {
            var (from, to) = p.Mode switch
            {
                "month" => (new DateOnly(today.Year, today.Month, 1), LastDay(today)),
                "last" => LastMonth(today),
                "year" => (new DateOnly(today.Year, 1, 1), new DateOnly(today.Year, 12, 31)),
                _ => ((DateOnly?)null, (DateOnly?)null),
            };
            FromBox.SelectedDate = from?.ToDateTime(TimeOnly.MinValue);
            ToBox.SelectedDate = to?.ToDateTime(TimeOnly.MinValue);
        }
        finally { _loadingFilter = false; }
        Reload();
    }

    /// <summary>用 Mode 挑預設區間（顯示文字是語系字串，不能拿來比對）。</summary>
    void SelectPreset(string mode)
    {
        var pick = ((IEnumerable<DatePreset>)PresetBox.ItemsSource).FirstOrDefault(p => p.Mode == mode);
        if (pick is not null) PresetBox.SelectedItem = pick;
    }

    void Range_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingFilter) return;
        _loadingFilter = true;
        try { SelectPreset("custom"); } finally { _loadingFilter = false; }
        Reload();
    }

    void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingFilter) Reload();
    }

    void Search_KeyUp(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Reload(); }
    void Search_Click(object sender, RoutedEventArgs e) => Reload();

    void Reset_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SelectPreset("month");
        CategoryBox.SelectedIndex = 0;
        StatusBox.SelectedIndex = 0;
        AccountBox.SelectedIndex = 0;
        Reload();                                   // 本來就在預設值的那些控件不會發事件，不自己重載就會卡在上次搜尋的結果
    }

    void Suggest_Click(object sender, RoutedEventArgs e)
    {
        if (_store is not { } s) return;
        var f = CurrentFilter();
        var dlg = new SuggestDialog(s, f.From, f.To, f.AccountId) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true && dlg.Applied > 0) RefreshNow();
    }

    /// <summary>分類帳 drill down：套用分類與日期範圍（全部帳戶）後重查。</summary>
    public void ApplyDrillDown(string categoryPath, string? from, string? to)
    {
        _loadingFilter = true;
        try
        {
            SelectPreset("custom");
            FromBox.SelectedDate = from is null ? null : DateTime.ParseExact(from, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            ToBox.SelectedDate = to is null ? null : DateTime.ParseExact(to, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            CategoryBox.SelectedItem = (CategoryBox.ItemsSource as IEnumerable<CategoryPick>)?
                .FirstOrDefault(c => c.Label == categoryPath);
        }
        finally { _loadingFilter = false; }
        Reload();
    }

    static DateOnly LastDay(DateOnly d) => new(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month));
    static (DateOnly, DateOnly) LastMonth(DateOnly today)
    {
        var first = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
        return (first, LastDay(first));
    }

    static string? DateOf(DatePicker p) => p.SelectedDate is { } d ? d.ToString("yyyy-MM-dd") : null;

    LedgerStore.Filter CurrentFilter() => new()
    {
        AccountId = _accountId ?? AccountBox.SelectedValue as long?,
        CategoryId = CategoryBox.SelectedValue as long?,
        Uncategorized = (CategoryBox.SelectedItem as CategoryPick)?.Uncategorized == true,
        Status = (StatusBox.SelectedItem as StatusPick)?.Mode ?? "ledger",
        From = DateOf(FromBox),
        To = DateOf(ToBox),
        Search = string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text.Trim(),
    };

    /// <summary>首頁沒有日期篩選列，概況固定看本月。</summary>
    static LedgerStore.Filter CurrentMonthFilter()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        return new() { From = new DateOnly(today.Year, today.Month, 1).ToString("yyyy-MM-dd"), To = LastDay(today).ToString("yyyy-MM-dd") };
    }

    // ---------------------------------------------------------------- 裝載

    void Reload()
    {
        if (_store is not { } s) { KpiText.Text = Loc.T("Home_DbUnavailable"); return; }

        var f = _homeMode ? CurrentMonthFilter() : CurrentFilter();
        List<LedgerStore.Row> rows = _homeMode ? [] : s.List(f);   // 首頁不顯示帳本清單，不白查 2000 列
        LedgerGrid.ItemsSource = rows;
        ShowSummary(f, rows.Count, s);
        _loadedVersion = LedgerStore.DataVersion;
    }

    /// <summary>概況區塊：格子裡改完只重算這個（列本身靠 INPC 就地更新，不重查整張表）。</summary>
    void ShowSummary(LedgerStore.Filter f, int rowCount, LedgerStore s)
    {
        var k = s.Totals(f.From, f.To, f.AccountId);
        var nw = s.NetWorth();
        var (orphans, dups) = s.Flags();
        var mismatch = s.BalanceMismatches();

        CountText.Text = Loc.F("Ledger_Rows", rowCount) + (rowCount >= f.Limit ? Loc.T("Ledger_Truncated") : "");
        if (_homeMode) ShowHome(nw, s);
        ShowKpi(k, nw, s.CardUnpaidMinor());
        ShowAlerts(orphans, dups, mismatch);
        if (_accountId is { } id && nw.Accounts.FirstOrDefault(a => a.Id == id) is { } mine)
            AccountHeaderText.Text = Loc.F("Ledger_AccountHeader", mine.Name, mine.Type, mine.Currency, mine.BalanceText) +
                                     (mine.CountedInNet ? "" : Loc.F("Ledger_AccountUnconverted", mine.Currency, nw.BaseCurrency));
    }

    /// <summary>帳單繳清了就地標已繳（不標就會永遠算在「卡未繳」與「未來帳單」裡）。</summary>
    void MarkPaid_Click(object sender, RoutedEventArgs e)
    {
        if (_store is not { } s || (sender as FrameworkElement)?.DataContext is not LedgerStore.UpcomingItem { BillId: { } billId }) return;
        s.MarkBillPaid(billId);
        Reload();                                   // 重查：卡未繳 KPI 與未來帳單都要跟著變
    }

    /// <summary>首頁：淨值明細分組（銀行→信用卡→其它）＋每月總資產折線＋未來帳單。</summary>
    void ShowHome(LedgerStore.NetWorthReport nw, LedgerStore s)
    {
        var accounts = nw.Accounts.OrderBy(a => a.GroupOrder).ThenBy(a => a.Name).ToList();
        var view = new System.Windows.Data.ListCollectionView(accounts);
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(LedgerStore.AccountBalance.Group)));
        HomeNetWorthGrid.ItemsSource = view;
        UpcomingGrid.ItemsSource = s.Upcoming();

        var plot = NetWorthPlot.Plot;
        plot.Clear();
        var points = s.MonthlyNetWorth();
        var line = plot.Add.Scatter(
            points.Select(p => p.Month.ToDateTime(TimeOnly.MinValue).ToOADate()).ToArray(),
            points.Select(p => (double)p.NetMinor / Money.LedgerScale).ToArray());
        line.LineWidth = 2;
        line.MarkerSize = 3;
        plot.Axes.DateTimeTicksBottom();
        plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic
        {
            LabelFormatter = v => v.ToString("N0"),
        };
        plot.Axes.AutoScale();
        plot.Title(Loc.F("Home_PlotTitle", nw.BaseCurrency));
        plot.Font.Automatic();                 // 標題是中文，預設字型沒有 glyph 會變亂碼
        NetWorthPlot.Refresh();
    }

    /// <summary>
    /// 分類帳報表：分類樹（列）× 月（欄）的透視表。預設只顯示頂層，點「分類」欄那一列就展開它的子類；
    /// 點月欄或合計欄還是 drill down（舊行為）。格子裡是 long minor，顯示才格式化，所以欄排序比的是數值。
    /// </summary>
    public void ShowCategoryReport()
    {
        if (_store is not { } s) return;
        HomePanel.Visibility = Visibility.Collapsed;
        _showingReport = true;
        _reportVersion = LedgerStore.DataVersion;

        // 「近 N 個月」= 從這個月往前推 N-1 個月的 1 號起，不封頂：未來的帳單月也要看得到。
        var from = _pivotSpan <= 0 ? null
            : new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(1 - _pivotSpan).ToString("yyyy-MM-dd");
        var pivot = s.CategoryPivot(from, null, _accountId);
        _pivotMonths = PivotTree.Months(pivot.Cells);
        _pivotRoots = PivotTree.Build(pivot.Categories, pivot.Cells, Loc.T("Ledger_Uncategorized"));
        ReportNote.Text = pivot.SkippedForeignEntries > 0
            ? Loc.F("Report_ForeignSkipped", pivot.SkippedForeignEntries, pivot.BaseCurrency) : "";

        BuildPivotColumns(s);
        RebuildPivotRows();
    }

    /// <summary>月欄跟著資料走（需求 1）：有資料的月才一欄，最後一欄是合計。</summary>
    void BuildPivotColumns(LedgerStore s)
    {
        var format = new CellFormat(s.BaseCurrencyScale);
        CategoryReportGrid.Columns.Clear();
        CategoryReportGrid.Columns.Add(new DataGridTextColumn
        {
            Header = Loc.T("Ledger_Col_CategoryPath"),
            Binding = new Binding(nameof(PivotNode.Label)),
            SortMemberPath = PivotTree.NameKey,               // 這一欄的同時是「展開/收合」的身分標記
            Width = new DataGridLength(3, DataGridLengthUnitType.Star),
        });
        foreach (var m in _pivotMonths) CategoryReportGrid.Columns.Add(ReportColumn(m, m, m, DataGridLength.Auto, format));
        CategoryReportGrid.Columns.Add(ReportColumn(Loc.T("Common_Total"), nameof(PivotNode.Total), PivotTree.TotalKey, DataGridLength.Auto, format));
        CategoryReportGrid.ItemsSource = _pivotRows;
    }

    void BuildReportRange()
    {
        ReportRangeBox.ItemsSource = new[]
        {
            new RangePick(6, Loc.F("Report_LastMonths", 6)),
            new RangePick(12, Loc.F("Report_LastMonths", 12)),
            new RangePick(24, Loc.F("Report_LastMonths", 24)),
            new RangePick(0, Loc.T("Report_AllMonths")),
        };
        ReportRangeBox.SelectedIndex = 1;                     // 預設近 12 個月
    }

    void ReportRange_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReportRangeBox.SelectedItem is not RangePick pick || pick.Span == _pivotSpan) return;
        _pivotSpan = pick.Span;
        if (_showingReport) ShowCategoryReport();             // 沒開報表就不查庫
    }

    /// <summary>排一次，再把展開節點的子列插回可見列。列數就幾百，重建整份清單比做增量通知划算。</summary>
    void RebuildPivotRows()
    {
        PivotTree.Sort(_pivotRoots, _pivotSortKey, _pivotDesc);
        _pivotRows.Clear();
        foreach (var row in PivotTree.Visible(_pivotRoots, _pivotExpanded)) _pivotRows.Add(row);
    }

    /// <summary>
    /// 欄頭點擊自己排（e.Handled = true）：內建那份排序不會讓父列帶著子列走，也做不到「只在一層內排」。
    /// 第一次點某欄都是由大到小（先看花費最多的），按同一欄第二次才反向。
    /// </summary>
    void Report_Sorting(object? sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (e.Column?.SortMemberPath is not { } key) return;
        _pivotDesc = _pivotSortKey == key ? !_pivotDesc : true;
        _pivotSortKey = key;
        foreach (var col in CategoryReportGrid.Columns) col.SortDirection = null;
        e.Column.SortDirection = _pivotDesc ? ListSortDirection.Descending : ListSortDirection.Ascending;
        RebuildPivotRows();
    }

    static DataGridTextColumn ReportColumn(string header, string path, string sortKey, DataGridLength width, IValueConverter format) => new()
    {
        Header = header,
        Binding = new Binding(path) { Converter = format },
        SortMemberPath = sortKey,                               // 欄位身分靠它認，欄頭是給人看的（會翻譯）
        Width = width,
        IsReadOnly = true,
        ElementStyle = NumberCellStyle,
    };

    /// <summary>格子裡的 long minor → 顯示字；小數位跟著基準幣的 <c>currency.scale</c>（美金不會被顯示成整數）。</summary>
    sealed class CellFormat(int scale) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => Money.Format(System.Convert.ToInt64(value ?? 0L), scale);
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    static readonly Style NumberCellStyle = new(typeof(TextBlock))
    {
        Setters = { new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right) },
    };

    void CategoryReport_CellClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject src) return;
        var cell = FindAncestor<DataGridCell>(src);
        if (cell is null || cell.Column?.SortMemberPath is not string key) return;
        if (cell.DataContext is not PivotNode node) return;

        if (key == PivotTree.NameKey)                         // 點「分類」欄 = 展開/收合（需求 4）
        {
            if (!node.HasChildren) return;
            if (!_pivotExpanded.Remove(node.Id)) _pivotExpanded.Add(node.Id);
            RebuildPivotRows();
            return;
        }
        if (key == PivotTree.TotalKey) { DrillDownRequested?.Invoke(node.Path, null, null); return; }

        var first = DateOnly.ParseExact(key, "yyyy-MM", CultureInfo.InvariantCulture);
        DrillDownRequested?.Invoke(node.Path, first.ToString("yyyy-MM-dd"), LastDay(first).ToString("yyyy-MM-dd"));
    }

    static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (var cur = d; cur is not null; cur = System.Windows.Media.VisualTreeHelper.GetParent(cur))
            if (cur is T t) return t;
        return null;
    }

    void HomeNetWorth_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src
            && ItemsControl.ContainerFromElement(HomeNetWorthGrid, src) is DataGridRow { Item: LedgerStore.AccountBalance a })
            OpenAccountRequested?.Invoke(a.Id, a.Name);
    }

    void ShowKpi(LedgerStore.Kpi k, LedgerStore.NetWorthReport nw, long unpaidMinor)
    {
        KpiText.Inlines.Clear();
        Add(Loc.T("Ledger_Kpi_Income"), N(k.IncomeMinor), null);
        Add(Loc.T("Ledger_Kpi_Expense"), N(k.ExpenseMinor), null);
        Add(Loc.T("Ledger_Kpi_Net"), N(k.NetMinor), null);
        Add(Loc.T("Ledger_Kpi_Uncategorized"), Loc.F("Ledger_Count", k.Uncategorized), N(k.UncategorizedMinor));
        Add(Loc.T("Ledger_Kpi_NetWorth"), N(nw.NetMinor), Loc.F("Ledger_AssetsLiabilities", N(nw.AssetsMinor), N(nw.LiabilitiesMinor)));
        Add(Loc.T("Ledger_Kpi_CardUnpaid"), N(unpaidMinor), null);
        if (nw.UnconvertedAccounts > 0) Add(Loc.T("Ledger_Kpi_Unconverted"), Loc.F("Ledger_AccountCount", nw.UnconvertedAccounts), Loc.T("Ledger_UnconvertedNote"));

        static string N(long minor) => Money.Format(minor, 0);

        void Add(string label, string value, string? note)
        {
            if (KpiText.Inlines.Count > 0) KpiText.Inlines.Add(new System.Windows.Documents.Run(Loc.T("Ledger_Sep")) { Foreground = System.Windows.Media.Brushes.LightGray });
            KpiText.Inlines.Add(new System.Windows.Documents.Run(label + " ") { Foreground = System.Windows.Media.Brushes.Gray });
            KpiText.Inlines.Add(new System.Windows.Documents.Run(value) { FontWeight = FontWeights.Bold });
            if (note is not null) KpiText.Inlines.Add(new System.Windows.Documents.Run($" {note}") { Foreground = System.Windows.Media.Brushes.Gray });
        }
    }

    void ShowAlerts(int orphans, int dups, List<(string Account, long DiffMinor, string Source, string AsOf)> mismatch)
    {
        OrphanButton.Content = Loc.F("Ledger_OrphanButton", orphans);
        OrphanButton.Visibility = orphans > 0 ? Visibility.Visible : Visibility.Collapsed;
        DuplicateButton.Content = Loc.F("Ledger_DupButton", dups);
        DuplicateButton.Visibility = dups > 0 ? Visibility.Visible : Visibility.Collapsed;
        BalanceButton.Content = Loc.F("Ledger_MismatchButton", mismatch.Count) +
                                (mismatch.Count > 0 ? Loc.F("Ledger_MismatchButtonSuffix", string.Join("、", mismatch.Select(m => $"{m.Account} {Loc.F("Ledger_DiffShort", Money.Format(m.DiffMinor, 0))}"))) : "");
        BalanceButton.ToolTip = Loc.T("Ledger_MismatchTip");
        BalanceButton.Visibility = mismatch.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Orphan_Click(object sender, RoutedEventArgs e) => StatusBox.SelectedItem = FindStatus("orphan");
    void Duplicate_Click(object sender, RoutedEventArgs e) => StatusBox.SelectedItem = FindStatus("dupgroup");

    void Balance_Click(object sender, RoutedEventArgs e)
    {
        if (_store is not { } s) return;
        var list = s.BalanceMismatches();
        MessageBox.Show(list.Count == 0 ? Loc.T("Ledger_MismatchAllOk")
                        : string.Join("\n", list.Select(m => Loc.F("Ledger_MismatchLine", m.Account,
                                        Loc.T(m.Source == "manual" ? "Common_Manual" : "Common_BankSlip"),
                                        m.AsOf, Money.Format(m.DiffMinor, 0)))
                                     .Append(Loc.T("Ledger_MismatchFooter"))),
                        Loc.T("Ledger_MismatchTitle"),
                        MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    object? FindStatus(string mode) => ((IEnumerable<StatusPick>)StatusBox.ItemsSource).FirstOrDefault(s => s.Mode == mode);

    // ---------------------------------------------------------------- 格子

    void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LedgerGrid.SelectedItem is not LedgerStore.Row r) { LineageBox.Text = ""; return; }
        LineageBox.Text =
            Loc.F("Ledger_Lineage_Merchant", r.Merchant) + "\n" +
            Loc.F("Ledger_Lineage_Amount", r.Amount.ToString("N0"), r.Currency) + (r.OrigCurrency is null ? "" : Loc.F("Ledger_Lineage_Orig", r.OrigCurrency)) + "\n" +
            Loc.F("Ledger_Lineage_Account", r.Account, r.AccountType) + "\n" +
            Loc.F("Ledger_Lineage_Kind", r.Kind, r.StatusText) + (r.Orphaned ? Loc.T("Ledger_Lineage_Orphan") : "") + (r.Edited ? Loc.T("Ledger_Lineage_Edited") : "") + "\n" +
            Loc.F("Ledger_Lineage_Source", r.SourceFile ?? Loc.T("Ledger_Lineage_Manual")) + (r.SourcePage is { } p ? Loc.F("Ledger_Lineage_Page", p) : "") + "\n" +
            Loc.F("Ledger_Lineage_Issuer", r.Issuer) + "\n" +
            $"parser    {r.Parser}\n" +
            Loc.F("Ledger_Lineage_Archived", r.ArchivedPath ?? Loc.T("Common_None")) + "\n\n" +
            Loc.F("Ledger_Lineage_Raw", r.SourceRaw);
    }

    void Grid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || _store is not { } s || e.Row.Item is not LedgerStore.Row r) return;

        if (e.Column == DateCol)
        {
            var text = (e.EditingElement as TextBox)?.Text;
            if (!LedgerStore.TryDate(text, out var dbDate))
            {
                e.Cancel = true;
                MessageBox.Show(Loc.T("Ledger_BadDateMsg"), Loc.T("Ledger_BadDateTitle"));
                return;
            }
            r.Date = dbDate;
            s.SetDate(r.Id, dbDate);
        }
        else if (e.Column == MerchantCol)
        {
            r.Merchant = (e.EditingElement as TextBox)?.Text?.Trim();
            s.SetMerchant(r.Id, r.Merchant);
        }
        else if (e.Column == NoteCol)
        {
            r.Note = (e.EditingElement as TextBox)?.Text?.Trim();
            s.SetNote(r.Id, r.Note);
        }
        else if (e.Column == CategoryCol)
        {
            try { s.SetCategory(r.Id, r.CategoryId); }
            catch (Exception ex)
            {
                // 下拉已經只列最底層，會走到這裡通常是別人剛把這個分類拆了子類（快取舊了）。
                // shortcut: 只提示不還原下拉內容， Reload 就恢復；要一鍵改選子類等有人按到再說。
                MessageBox.Show(ex.Message, Loc.T("Ledger_BadCategoryTitle"));
                return;
            }
            // 綁定只寫了 CategoryId；格子裡的文字綁的是 Category，不一起換就會看起來像沒儲存。
            r.Category = r.Categories.FirstOrDefault(o => o.Id == r.CategoryId)?.Name;
        }
        else return;
        r.Edited = true;                                        // 列首出現 ●：以後重匯只准更新原始文字
        ShowSummary(CurrentFilter(), LedgerGrid.Items.Count, s);       // 列就地更新，只重算概況
    }

    void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LedgerGrid.CurrentColumn == AccountCol) OpenSelectedAccount();
    }

    void OpenAccount_Click(object sender, RoutedEventArgs e) => OpenSelectedAccount();

    void OpenSelectedAccount()
    {
        if (LedgerGrid.SelectedItem is LedgerStore.Row r) OpenAccountRequested?.Invoke(r.AccountId, r.Account);
    }

    IEnumerable<LedgerStore.Row> Selected() => LedgerGrid.SelectedItems.OfType<LedgerStore.Row>();

    void Status_Click(object sender, RoutedEventArgs e)
    {
        if (_store is not { } s || sender is not Button { Tag: string status }) return;
        foreach (var r in Selected()) s.SetStatus(r.Id, status);
        Reload();
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_store is not { } s) return;
        foreach (var r in Selected()) s.SoftDelete(r.Id);
        Reload();
    }

    void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_store is not { } s) return;
        foreach (var r in Selected()) s.Restore(r.Id);
        Reload();
    }

    void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_store is not { } s) return;
        var dialog = new AddEntryDialog(s, _accountId) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) Reload();
    }

    void AddCategory_Click(object sender, RoutedEventArgs e)
    {
        if (_store is not { } s) return;
        var dialog = new CategoryDialog { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        s.AddCategory(dialog.Name, dialog.Kind);
        RefreshNow();
    }

    /// <summary>帳戶管理：建帳戶、期初餘額、卡額度/自動扣繳、餘額盤點都在那里。</summary>
    void Accounts_Click(object sender, RoutedEventArgs e)
    {
        if (_store is not { } s) return;
        new AccountsDialog(s) { Owner = Window.GetWindow(this) }.ShowDialog();
        RefreshNow();                                       // 帳戶增改/盤點会改淨值与下拉，回来就重查
    }

    void OpenPdf_Click(object sender, RoutedEventArgs e)
    {
        if (LedgerGrid.SelectedItem is not LedgerStore.Row { ArchivedPath: { } path })
        {
            MessageBox.Show(Loc.T("Ledger_NoPdf"), Loc.T("Ledger_OpenPdf"));
            return;
        }
        if (!File.Exists(path)) { MessageBox.Show(Loc.T("Ledger_PdfMissing") + path, Loc.T("Ledger_OpenPdf")); return; }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, Loc.T("Ledger_OpenPdf")); }
    }
}
