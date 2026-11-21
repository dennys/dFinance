using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using dFinance.Data;
using dFinance.Data.Localization;
using Microsoft.Data.Sqlite;

namespace dFinance;

/// <summary>
/// 報表 → 依帳期彙總：一列 = 一份信用卡帳單，拿「明細合計」跟「單上應繳」對帳
/// （對不上的那幾期就是 parser 漏列的候選）。口徑只有 <see cref="LedgerStore.BillPeriods"/> 那一支 SQL，
/// 這裡只綁東西、不自己算錢（AGENTS）。
/// <para>點一列 = 開那個卡的帳戶分頁並套用該期範圍；點「繳銷」那一格 = 標已繳/未繳
/// （跟首頁「未來帳單」那顆按鈕同一支 <see cref="LedgerStore.MarkBillPaid"/>）。</para>
/// shortcut: 不做匯出（TODO c）、不建 rollup 表 —— 帳單是每期一筆的量級，每次重查比較不會錯到哪裡去。
/// </summary>
public partial class BillPeriodTab : UserControl
{
    readonly LedgerStore _store;
    int _span = 12;                                     // 每張卡只列最近 N 期（0 = 全部）

    /// <summary>參數：帳戶 id、帳戶名、起日、迄日（null = 不加那個條件）。</summary>
    public event Action<long, string, string?, string?>? DrillRequested;

    record SpanPick(int Span, string Label);

    public BillPeriodTab(SqliteConnection db)
    {
        InitializeComponent();
        _store = new LedgerStore(db);
        BuildColumns();
        BuildRange();                                   // 預設設成 12：與 _span 同值，不會多查一次
        Reload();
    }

    void BuildRange()
    {
        RangeBox.ItemsSource = new[]
        {
            new SpanPick(6, Loc.F("Bill_LastPeriods", 6)),
            new SpanPick(12, Loc.F("Bill_LastPeriods", 12)),
            new SpanPick(24, Loc.F("Bill_LastPeriods", 24)),
            new SpanPick(0, Loc.T("Bill_AllPeriods")),
        };
        RangeBox.SelectedIndex = 1;
    }

    /// <summary>欄位在 code 裡建：欄頭是翻譯過的文字，XAML 的 DataGridTextColumn.Header 綁不到 Loc 索引子。</summary>
    void BuildColumns()
    {
        var number = (Style)FindResource("NumberCell");
        var diff = (Style)FindResource("DiffCell");
        BillGrid.Columns.Clear();
        BillGrid.Columns.Add(Col(Loc.T("Common_Account"), nameof(LedgerStore.BillPeriod.AccountName), null, 200, null));
        BillGrid.Columns.Add(Col(Loc.T("Bill_Col_Period"), nameof(LedgerStore.BillPeriod.PeriodText), null, 190, null));
        BillGrid.Columns.Add(Col(Loc.T("Bill_Col_DueDate"), nameof(LedgerStore.BillPeriod.DueDate), null, 96, null));
        BillGrid.Columns.Add(Col(Loc.T("Common_Currency"), nameof(LedgerStore.BillPeriod.Currency), null, 54, number));
        BillGrid.Columns.Add(Col(Loc.T("Bill_Col_Entries"), nameof(LedgerStore.BillPeriod.Entries),
                                 nameof(LedgerStore.BillPeriod.Entries), 62, number));
        BillGrid.Columns.Add(Col(Loc.T("Bill_Col_Spend"), nameof(LedgerStore.BillPeriod.SpendText),
                                 nameof(LedgerStore.BillPeriod.SpendMinor), 110, number));
        BillGrid.Columns.Add(Col(Loc.T("Bill_Col_Due"), nameof(LedgerStore.BillPeriod.DueText),
                                 nameof(LedgerStore.BillPeriod.TotalDueMinor), 110, number));
        BillGrid.Columns.Add(Col(Loc.T("Bill_Col_Diff"), nameof(LedgerStore.BillPeriod.DiffText),
                                 nameof(LedgerStore.BillPeriod.DiffMinor), 110, diff));
        BillGrid.Columns.Add(Col(Loc.T("Bill_Col_Transfer"), nameof(LedgerStore.BillPeriod.TransferText),
                                 nameof(LedgerStore.BillPeriod.TransferMinor), 110, number));
        BillGrid.Columns.Add(Col(Loc.T("Bill_Col_Paid"), nameof(LedgerStore.BillPeriod.PaidText), null, 100, null));
        BillGrid.Columns.Add(Col("⚠", nameof(LedgerStore.BillPeriod.MarkText), null, 96, null));
        BillGrid.FrozenColumnCount = 2;                 // 欄很多時捲過去要知道這列是哪張卡、哪一期
    }

    static DataGridTextColumn Col(string header, string path, string? sortPath, double width, Style? style) => new()
    {
        Header = header,
        Binding = new Binding(path),
        // 格子裡是顯示用的文字（"3,000"），按欄頭排序要照真正的 long minor，才會是數值序。
        SortMemberPath = sortPath ?? path,
        Width = width,
        IsReadOnly = true,
        ElementStyle = style,
    };

    public void Reload()
    {
        var r = _store.BillPeriods(_span, OnlyMismatchBox.IsChecked == true);
        BillGrid.ItemsSource = r.Rows;
        TotalsText.Text = Loc.F("Bill_Totals", r.BaseCurrency, r.SpendText, r.DueText, r.DiffText)
                        + (r.UnconvertedBills > 0 ? "　" + Loc.F("Bill_Unconverted", r.UnconvertedBills) : "")
                        + (r.UnmappedEntries > 0 ? "　" + Loc.F("Bill_Unmapped", r.UnmappedEntries) : "");
    }

    void Range_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RangeBox.SelectedItem is not SpanPick pick || pick.Span == _span) return;
        _span = pick.Span;
        Reload();
    }

    void OnlyMismatch_Changed(object sender, RoutedEventArgs e) => Reload();

    void Grid_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject src) return;
        var cell = FindAncestor<DataGridCell>(src);
        if (cell is null || cell.Column?.SortMemberPath is not { } key || cell.DataContext is not LedgerStore.BillPeriod r) return;

        if (key == nameof(LedgerStore.BillPeriod.PaidText)) { TogglePaid(r); return; }

        // drill 用的是該期的日期範圍（要「照帳期」查是匯總那張表的事；帳本清單本來就只有日期/帳戶/分類可筛）
        DrillRequested?.Invoke(r.AccountId, r.AccountName,
                               string.IsNullOrEmpty(r.PeriodStart) ? null : r.PeriodStart,
                               r.Unmapped ? null : r.PeriodEnd);
    }

    void TogglePaid(LedgerStore.BillPeriod r)
    {
        if (r.Unmapped) return;
        try { _store.MarkBillPaid(r.BillId, paid: !r.Paid); }
        catch (Exception ex)                       // 兩條連線撞鎖之類：不接就是整個程式挂掉
        {
            MessageBox.Show(ex.Message, Loc.T("Ledger_WriteFailed"));
        }
        Reload();
    }

    static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (var cur = d; cur is not null; cur = System.Windows.Media.VisualTreeHelper.GetParent(cur))
            if (cur is T t) return t;
        return null;
    }

    /// <summary>給 <c>--uismoke</c>：分頁建得出來、真的查一次、換一次篩選也動得了。</summary>
    public string SmokeSummary()
    {
        Reload();
        if (RangeBox.DisplayMemberPath != "Label")                        // 沒設就會顯示成「SpanPick { Span = 12, Label = ... }」那種程式字
            throw new InvalidOperationException($"期間下拉沒設 DisplayMemberPath：會顯示成「{RangeBox.SelectedItem}」那種程式字");
        var rows = BillGrid.Items.Count;
        if (rows == 0) throw new InvalidOperationException("依帳期彙總沒有任何列（這個庫有信用卡帳單，不該是空）");
        if (BillGrid.Columns.Count < 10) throw new InvalidOperationException($"欄位只剩 {BillGrid.Columns.Count} 欄：BuildColumns 沒跑完");

        OnlyMismatchBox.IsChecked = true;                 // 「只看對不上的」也要走一次（WHERE 換了參數）
        var mismatch = BillGrid.Items.Count;
        OnlyMismatchBox.IsChecked = false;

        var all = _store.BillPeriods(periodsPerCard: 0).Rows.Count;
        if (all < rows) throw new InvalidOperationException($"全部帳期 {all} 比近 {_span} 期 {rows} 還少");
        if (string.IsNullOrWhiteSpace(TotalsText.Text)) throw new InvalidOperationException("合計列是空的");
        return $"{rows} 期／對不上 {mismatch}／全部 {all}／{TotalsText.Text}";
    }
}
