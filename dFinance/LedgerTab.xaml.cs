using System.Diagnostics;
using System.IO;
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
    long _reportVersion = -1;
    bool _loadingFilter;                          // 灌篩選選項時別跟著重查

    record AccountPick(long? Id, string Label);
    record CategoryPick(long? Id, string Label, bool Uncategorized);
    record StatusPick(string Mode, string Label);

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
            PresetBox.ItemsSource = new[] { "本月", "上月", "今年", "全部", "自訂" };
            PresetBox.SelectedIndex = 0;

            var accounts = new List<AccountPick> { new(null, "全部帳戶") };
            // 已歸檔的也列出來（不列就再也筛不到它），但标清楚，别让人以为是个还在用的帳戶
            accounts.AddRange(s.Accounts(includeArchived: true).Select(a => new AccountPick(
                a.Id, (a.Currency == "TWD" ? a.Name : $"{a.Name}（{a.Currency}）") + (a.IsArchived ? "（已歸檔）" : ""))));
            AccountBox.ItemsSource = accounts;
            AccountBox.SelectedItem = accounts.FirstOrDefault(a => a.Id == _accountId) ?? accounts[0];

            var cats = new List<CategoryPick> { new(null, "全部分類", false), new(null, "未分類", true) };
            cats.AddRange(s.Categories("expense").Select(c => new CategoryPick(c.Id, c.Name, false)));
            cats.AddRange(s.Categories("income").Select(c => new CategoryPick(c.Id, c.Name, false)));
            CategoryBox.ItemsSource = cats;
            CategoryBox.SelectedIndex = 0;

            StatusBox.ItemsSource = new[]
            {
                new StatusPick("ledger", "帳本"), new StatusPick("imported", "待補分類"),
                new StatusPick("orphan", "孤兒列"), new StatusPick("dupgroup", "疑似重複"),
                new StatusPick("duplicate", "已標重複"), new StatusPick("ignored", "已忽略"),
                new StatusPick("deleted", "已刪除"), new StatusPick("all", "全部（含未發生）"),
            };
            StatusBox.SelectedIndex = 0;
        }
        finally { _loadingFilter = false; }
    }

    void Preset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingFilter || PresetBox.SelectedItem is not string p) return;
        var today = DateOnly.FromDateTime(DateTime.Today);
        _loadingFilter = true;
        try
        {
            var (from, to) = p switch
            {
                "本月" => (new DateOnly(today.Year, today.Month, 1), LastDay(today)),
                "上月" => LastMonth(today),
                "今年" => (new DateOnly(today.Year, 1, 1), new DateOnly(today.Year, 12, 31)),
                "全部" => ((DateOnly?)null, (DateOnly?)null),
                _ => ((DateOnly?)null, (DateOnly?)null),
            };
            FromBox.SelectedDate = from?.ToDateTime(TimeOnly.MinValue);
            ToBox.SelectedDate = to?.ToDateTime(TimeOnly.MinValue);
        }
        finally { _loadingFilter = false; }
        Reload();
    }

    void Range_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingFilter) return;
        _loadingFilter = true;
        try { PresetBox.SelectedItem = "自訂"; } finally { _loadingFilter = false; }
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
        PresetBox.SelectedItem = "本月";
        CategoryBox.SelectedIndex = 0;
        StatusBox.SelectedIndex = 0;
        AccountBox.SelectedIndex = 0;
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
            PresetBox.SelectedItem = "自訂";
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
        if (_store is not { } s) { KpiText.Text = "資料庫開不起來，帳本暫時不能用。"; return; }

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

        CountText.Text = $"{rowCount} 列" + (rowCount >= f.Limit ? "（已截斷，請縮小範圍）" : "");
        if (_homeMode) ShowHome(nw, s);
        ShowKpi(k, nw, s.CardUnpaidMinor());
        ShowAlerts(orphans, dups, mismatch);
        if (_accountId is { } id && nw.Accounts.FirstOrDefault(a => a.Id == id) is { } mine)
            AccountHeaderText.Text = $"{mine.Name}（{mine.Type} {mine.Currency}）餘額 {mine.BalanceMinor / 100m:N0}" +
                                     (mine.CountedInNet ? "" : "　— 非台幣帳戶，未計入淨值");
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
        plot.Title("每月總資產（TWD）");
        plot.Font.Automatic();                 // 標題是中文，預設字型沒有 glyph 會變亂碼
        NetWorthPlot.Refresh();
    }

    /// <summary>分類帳報表：分類（列）× 月（欄）透視表，最後一欄為該分類合計；僅顯示報表本身。</summary>
    public void ShowCategoryReport()
    {
        if (_store is not { } s) return;
        HomePanel.Visibility = Visibility.Collapsed;
        _showingReport = true;
        _reportVersion = LedgerStore.DataVersion;

        var totals = s.CatMonthTotals();
        var months = totals.Select(t => t.Month).Distinct().OrderBy(m => m, StringComparer.Ordinal).ToList();
        var rows = totals.GroupBy(t => (t.CategoryPath, t.IsRollup)).Select(g =>
        {
            var row = new Dictionary<string, string>
            {
                ["CategoryPath"] = g.Key.CategoryPath,
                ["Label"] = g.Key.IsRollup ? $"{g.Key.CategoryPath}（含子類）" : g.Key.CategoryPath,
            };
            foreach (var m in months)
                row[m] = Money.FromMinor(g.Where(t => t.Month == m).Sum(t => t.TotalMinor)).ToString("N0");
            row["Total"] = Money.FromMinor(g.Sum(t => t.TotalMinor)).ToString("N0");
            return row;
        }).ToList();

        CategoryReportGrid.Columns.Clear();
        CategoryReportGrid.Columns.Add(ReportColumn("分類路徑", "Label", new DataGridLength(3, DataGridLengthUnitType.Star)));
        foreach (var m in months) CategoryReportGrid.Columns.Add(ReportColumn(m, m, DataGridLength.Auto));
        CategoryReportGrid.Columns.Add(ReportColumn("合計", "Total", DataGridLength.Auto));
        CategoryReportGrid.ItemsSource = rows;
    }

    static DataGridTextColumn ReportColumn(string header, string key, DataGridLength width) => new()
    {
        Header = header,
        Binding = new System.Windows.Data.Binding($"[{key}]"),
        Width = width,
        IsReadOnly = true,
        ElementStyle = key == "Label" ? null : NumberCellStyle,
    };

    static readonly Style NumberCellStyle = new(typeof(TextBlock))
    {
        Setters = { new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right) },
    };

    void CategoryReport_CellClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject src) return;
        var cell = FindAncestor<DataGridCell>(src);
        if (cell is null || cell.Column?.Header is not string header || header == "分類路徑") return;
        if (cell.DataContext is not Dictionary<string, string> row) return;

        var category = row["CategoryPath"];
        if (header == "合計") { DrillDownRequested?.Invoke(category, null, null); return; }

        var first = DateOnly.ParseExact(header, "yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        DrillDownRequested?.Invoke(category, first.ToString("yyyy-MM-dd"), LastDay(first).ToString("yyyy-MM-dd"));
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
        Add("本月收入", N(k.IncomeMinor), null);
        Add("本月支出", N(k.ExpenseMinor), null);
        Add("淨額", N(k.NetMinor), null);
        Add("待分類", $"{k.Uncategorized} 筆", N(k.UncategorizedMinor));
        Add("淨值", N(nw.NetMinor), $"資產 {N(nw.AssetsMinor)}　負債 {N(nw.LiabilitiesMinor)}");
        Add("卡未繳", N(unpaidMinor), null);
        if (nw.UnconvertedAccounts > 0) Add("未折算", $"{nw.UnconvertedAccounts} 個帳戶", "非台幣、沒有匯率來源，故意不計入淨值");

        static string N(long minor) => (minor / 100m).ToString("N0");

        void Add(string label, string value, string? note)
        {
            if (KpiText.Inlines.Count > 0) KpiText.Inlines.Add(new System.Windows.Documents.Run("　｜　") { Foreground = System.Windows.Media.Brushes.LightGray });
            KpiText.Inlines.Add(new System.Windows.Documents.Run(label + " ") { Foreground = System.Windows.Media.Brushes.Gray });
            KpiText.Inlines.Add(new System.Windows.Documents.Run(value) { FontWeight = FontWeights.Bold });
            if (note is not null) KpiText.Inlines.Add(new System.Windows.Documents.Run($" {note}") { Foreground = System.Windows.Media.Brushes.Gray });
        }
    }

    void ShowAlerts(int orphans, int dups, List<(string Account, long DiffMinor, string Source, string AsOf)> mismatch)
    {
        OrphanButton.Content = $"孤兒列 {orphans} 筆";
        OrphanButton.Visibility = orphans > 0 ? Visibility.Visible : Visibility.Collapsed;
        DuplicateButton.Content = $"疑似重複 {dups} 對";
        DuplicateButton.Visibility = dups > 0 ? Visibility.Visible : Visibility.Collapsed;
        BalanceButton.Content = $"餘額不符 {mismatch.Count} 個帳戶" +
                                (mismatch.Count > 0 ? $"：{string.Join("、", mismatch.Select(m => $"{m.Account} 差 {Money.FromMinor(m.DiffMinor):N0}"))}" : "");
        BalanceButton.ToolTip = "餘額快照（存款單解析出來的，或人工盤點的）對不上交易累加";
        BalanceButton.Visibility = mismatch.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Orphan_Click(object sender, RoutedEventArgs e) => StatusBox.SelectedItem = FindStatus("orphan");
    void Duplicate_Click(object sender, RoutedEventArgs e) => StatusBox.SelectedItem = FindStatus("dupgroup");

    void Balance_Click(object sender, RoutedEventArgs e)
    {
        if (_store is not { } s) return;
        var list = s.BalanceMismatches();
        MessageBox.Show(list.Count == 0 ? "都對得上。"
                        : string.Join("\n", list.Select(m => $"{m.Account}：{(m.Source == "manual" ? "人工盤點" : "存款單")} {m.AsOf} 比交易累加多 {Money.FromMinor(m.DiffMinor):N0}")
                                     .Append("\n對不上通常是漏了交易或期初填錯；可在「帳戶…」裡重新盤點或改期初餘額。")),
                        "餘額快照 vs 交易累加",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    object? FindStatus(string mode) => ((IEnumerable<StatusPick>)StatusBox.ItemsSource).FirstOrDefault(s => s.Mode == mode);

    // ---------------------------------------------------------------- 格子

    void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LedgerGrid.SelectedItem is not LedgerStore.Row r) { LineageBox.Text = ""; return; }
        LineageBox.Text =
            $"商家      {r.Merchant}\n" +
            $"金額      {r.Amount:N0} {r.Currency}" + (r.OrigCurrency is null ? "" : $"（原幣 {r.OrigCurrency}）") + "\n" +
            $"帳戶      {r.Account}（{r.AccountType}）\n" +
            $"種類      {r.Kind}　狀態 {r.StatusText}" + (r.Orphaned ? "　孤兒" : "") + (r.Edited ? "　人工改過" : "") + "\n" +
            $"來源      {r.SourceFile ?? "（手動輸入）"}" + (r.SourcePage is { } p ? $" 第 {p} 頁" : "") + "\n" +
            $"發行方    {r.Issuer}\n" +
            $"parser    {r.Parser}\n" +
            $"留底      {r.ArchivedPath ?? "（沒有）"}\n\n" +
            $"原文\n{r.SourceRaw}";
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
                MessageBox.Show("消費日要用 yyyy/MM/dd（例 2026-11-03），清空就是留空等以後補。", "日期格式不對");
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
            s.SetCategory(r.Id, r.CategoryId);
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
            MessageBox.Show("這筆沒有留底 PDF（手動輸入的，或留底路徑缺失）。", "開啟留底 PDF");
            return;
        }
        if (!File.Exists(path)) { MessageBox.Show("留底檔不見了：" + path, "開啟留底 PDF"); return; }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "開啟留底 PDF"); }
    }
}
