using System.Windows;
using dFinance.Data.Localization;
using System.Windows.Controls;
using System.Windows.Data;
using dFinance.Data;

namespace dFinance;

/// <summary>
/// 幣別與匯率。基準幣、幣別字典（含匯入用的別名「美金|美元」）、匯率表、抓匯率與 proxy。
/// 程式碼建介面（跟 AccountsDialog / CategoryManagerDialog 一樣，不為一個視窗多開 XAML）。
/// </summary>
public sealed class CurrencyDialog : Window
{
    readonly LedgerStore _store;
    readonly ComboBox _base = new() { Width = 90, Margin = new Thickness(6, 0, 6, 0), ToolTip = Loc.T("Ccy_BaseHint") };
    readonly DataGrid _ccyGrid = new() { AutoGenerateColumns = false, CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column };
    readonly DataGrid _rateGrid = new() { AutoGenerateColumns = false, CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column };
    // 要 IsEditable：字典外的幣別也能现场填（下拉只是省得人打，不是限制）。兩側留白：貼著標籤很難看。
    readonly ComboBox _rateFrom = new() { Width = 80, IsEditable = true, Margin = new Thickness(6, 0, 6, 0), ToolTip = Loc.T("Ccy_CodeHint") };
    readonly TextBox _rateValue = new() { Width = 90, Margin = new Thickness(0, 0, 6, 0), ToolTip = Loc.T("Ccy_RateHint") };
    readonly TextBox _proxy = new() { Width = 230, ToolTip = Loc.T("Ccy_ProxyHint") };
    readonly TextBox _status = new() { IsReadOnly = true, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, TextWrapping = TextWrapping.Wrap };

    public CurrencyDialog(LedgerStore store)
    {
        _store = store;
        Title = Loc.T("Ccy_Title"); Width = 660; Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        BuildBaseRow();
        BuildCurrencyGrid();
        BuildRateGrid();

        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(Section(Loc.T("Ccy_Base")));
        root.Children.Add(_baseRow);
        root.Children.Add(new TextBlock { Text = Loc.T("Ccy_PairHint"), Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 2, 0, 8) });
        root.Children.Add(Section(Loc.T("Ccy_Dictionary")));
        root.Children.Add(_ccyGrid);
        root.Children.Add(CurrencyAddRow());
        root.Children.Add(Section(Loc.T("Ccy_Rates")));
        root.Children.Add(_rateGrid);
        root.Children.Add(RateRow());
        root.Children.Add(NetworkRow());
        root.Children.Add(_status);
        Content = root;
        Reload();
    }

    static TextBlock Section(string title) => new() { Text = title, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 4) };

    /// <summary>給 <c>--uismoke</c>：人工填匯率那个下拉有沒有幣別可選（空下拉就是開得來但用不了）。</summary>
    public int RateCurrencyOptionCount => _rateFrom.Items.Count;

    /// <summary>給 <c>--uismoke</c> 用的钩子：視窗建得出來、字典讀得到嗎。</summary>
    public int CurrencyRowCount => _ccyGrid.Items.Count;

    // ---------------------------------------------------------------- 基準幣

    readonly StackPanel _baseRow = new() { Orientation = Orientation.Horizontal };

    void BuildBaseRow()
    {
        // 選一選就套用：基準幣只是報表口徑，不會弄髒匯率，不需要額外的確認按鈕。
        _base.SelectionChanged += (_, _) =>
        {
            if (_base.SelectedItem as string is not string ccy || ccy == _store.BaseCurrency) return;
            _store.SetBaseCurrency(ccy);
            Reload();                                       // 匯率表讀的是「對基準幣」那一側，要整張重讀
            Say(Loc.F("Ccy_BaseChanged", ccy));
        };
        _baseRow.Children.Add(new TextBlock { Text = Loc.T("Ccy_ReportCcy"), VerticalAlignment = VerticalAlignment.Center });
        _baseRow.Children.Add(_base);
        _baseRow.Children.Add(new TextBlock { Text = Loc.T("Ccy_BaseNote"), VerticalAlignment = VerticalAlignment.Center, Foreground = System.Windows.Media.Brushes.Gray });
    }

    // ---------------------------------------------------------------- 幣別字典

    void BuildCurrencyGrid()
    {
        _ccyGrid.Columns.Add(Column(Loc.T("Common_Code"), nameof(LedgerStore.CurrencyRow.Code), 70, readOnly: true));
        _ccyGrid.Columns.Add(Column(Loc.T("Common_Name"), nameof(LedgerStore.CurrencyRow.Name), 110));
        _ccyGrid.Columns.Add(Column(Loc.T("Common_Scale"), nameof(LedgerStore.CurrencyRow.Scale), 60));
        _ccyGrid.Columns.Add(Column(Loc.T("Ccy_Aliases"), nameof(LedgerStore.CurrencyRow.Aliases), 320));
        _ccyGrid.SelectionChanged += (_, _) =>
        {
            if (_ccyGrid.SelectedItem is not LedgerStore.CurrencyRow row) return;
            _newCode.Text = row.Code; _newName.Text = row.Name; _newScale.Text = row.Scale.ToString(); _newAliases.Text = row.Aliases;
        };
    }

    static DataGridTextColumn Column(string header, string path, int width, bool readOnly = false) => new()
    {
        Header = header,
        Binding = new Binding(path) { Mode = readOnly ? BindingMode.OneWay : BindingMode.TwoWay },
        Width = new DataGridLength(width),
        IsReadOnly = readOnly,
    };

    readonly TextBox _newCode = new() { Width = 60 };
    readonly TextBox _newName = new() { Width = 110 };
    readonly TextBox _newScale = new() { Width = 40, Text = "2" };
    readonly TextBox _newAliases = new() { Width = 190, ToolTip = Loc.T("Ccy_AliasHint") };

    UIElement CurrencyAddRow()
    {
        var save = new Button { Content = Loc.T("Ccy_SaveCurrency"), Width = 70, Margin = new Thickness(6, 0, 0, 0) };
        save.Click += (_, _) =>
        {
            try
            {
                _store.SaveCurrency(_newCode.Text, _newName.Text, int.TryParse(_newScale.Text, out var s) ? s : 2, _newAliases.Text);
                Reload();
                Say(Loc.F("Ccy_Saved", _newCode.Text.Trim().ToUpperInvariant()));
            }
            catch (Exception ex) { Say(ex.Message); }              // 驗證不過就留下來讓人補
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(new TextBlock { Text = Loc.T("Common_Code"), VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(_newCode);
        panel.Children.Add(new TextBlock { Text = Loc.T("Ccy_Label_Name"), VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(_newName);
        panel.Children.Add(new TextBlock { Text = Loc.T("Ccy_Label_Scale"), VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(_newScale);
        panel.Children.Add(new TextBlock { Text = Loc.T("Ccy_Label_Alias"), VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(_newAliases);
        panel.Children.Add(save);
        return panel;
    }

    // ---------------------------------------------------------------- 匯率

    void BuildRateGrid()
    {
        _rateGrid.Columns.Add(Column(Loc.T("Ccy_Col_From"), nameof(LedgerStore.RateRow.From), 70, readOnly: true));
        _rateGrid.Columns.Add(Column(Loc.T("Ccy_Col_To"), nameof(LedgerStore.RateRow.To), 80, readOnly: true));
        _rateGrid.Columns.Add(Column(Loc.T("Ccy_Rates"), nameof(LedgerStore.RateRow.Rate), 90, readOnly: true));
        _rateGrid.Columns.Add(Column(Loc.T("Ccy_Col_RateDate"), nameof(LedgerStore.RateRow.RateDate), 90, readOnly: true));
        _rateGrid.Columns.Add(Column(Loc.T("Common_Source"), nameof(LedgerStore.RateRow.Source), 70, readOnly: true));
        _rateGrid.Columns.Add(Column("⚠", nameof(LedgerStore.RateRow.StaleText), 80, readOnly: true));
        _rateGrid.SelectionChanged += (_, _) =>
        {
            if (_rateGrid.SelectedItem is not LedgerStore.RateRow row) return;
            _rateFrom.Text = row.From; _rateValue.Text = row.Rate.ToString("0.######");
        };
    }

    UIElement RateRow()
    {
        var save = new Button { Content = Loc.T("Ccy_Manual"), Width = 70, Margin = new Thickness(6, 0, 0, 0),
                                ToolTip = Loc.T("Ccy_ClearRateHint") };
        save.Click += (_, _) =>
        {
            var from = _rateFrom.Text;
            if (decimal.TryParse(_rateValue.Text.Replace(",", "").Trim(), out var rate) && rate > 0) _store.SetRate(from, rate);
            else if (string.IsNullOrWhiteSpace(_rateValue.Text)) _store.SetRate(from, null);
            else { Say(Loc.T("Ccy_BadRate")); return; }
            Reload();
            Say(Loc.F("Ccy_RateSaved", from, _store.BaseCurrency));
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "1", VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(_rateFrom);
        panel.Children.Add(new TextBlock { Text = $" = ?  {_store.BaseCurrency}", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        panel.Children.Add(_rateValue);
        panel.Children.Add(save);
        return panel;
    }

    // ---------------------------------------------------------------- 網路

    UIElement NetworkRow()
    {
        _proxy.Text = _store.RateProxy;

        var fetch = new Button { Content = Loc.T("Ccy_Fetch"), Margin = new Thickness(6, 0, 0, 0) };
        fetch.Click += (_, _) =>
        {
            var source = NewSource();
            Say(Loc.T("Ccy_Fetching"));
            var (updated, errors) = source.Refresh(_store);
            Reload();
            Say(errors.Count == 0 ? Loc.F("Ccy_FetchOk", updated)
                                  : Loc.F("Ccy_FetchPartial", updated) + string.Join("；", errors));
        };

        var test = new Button { Content = Loc.T("Ccy_Test"), Margin = new Thickness(6, 0, 0, 0),
                                ToolTip = Loc.T("Ccy_TestHint") };
        test.Click += (_, _) => { Say(Loc.T("Ccy_Testing")); Say(NewSource().Test(_store)); };

        var split = new Button { Content = Loc.T("Ccy_Split"), Margin = new Thickness(18, 0, 0, 0),
                                 ToolTip = Loc.T("Ccy_SplitHint") };
        split.Click += (_, _) =>
        {
            int moved;
            try { moved = _store.SplitForeignCurrencyEntries(); }
            catch (Exception ex) { Say(ex.Message); return; }        // 搬一半失败也要留得下来看，不能让視窗挂掉
            Reload();
            Say(moved == 0 ? Loc.T("Ccy_SplitNone") : Loc.F("Ccy_SplitDone", moved));
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "proxy", VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(_proxy);
        panel.Children.Add(fetch);
        panel.Children.Add(test);
        panel.Children.Add(split);
        return panel;
    }

    RateSource NewSource()
    {
        _store.RateProxy = _proxy.Text;                             // 抓之前先把欄位上的 proxy 存下來
        return new RateSource { ProxyUrl = _store.RateProxy };
    }

    // ----------------------------------------------------------------

    void Reload()
    {
        var codes = _store.CurrencyList().Select(c => c.Code).ToList();
        foreach (var c in _store.CurrenciesWithoutDictEntry().Where(c => !codes.Contains(c)))
        {
            codes.Add(c);
            _store.SaveCurrency(c, c, 2, null);                     // 匯入帶進來的陌生幣別要能在這裡改，不能只活在 entry 裡
        }
        var keep = _base.SelectedItem as string;
        _base.ItemsSource = codes;
        _base.SelectedItem = codes.Contains(_store.BaseCurrency) ? _store.BaseCurrency
                           : codes.Contains(keep ?? "") ? keep : _store.BaseCurrency;
        _ccyGrid.ItemsSource = _store.CurrencyList();
        _rateGrid.ItemsSource = _store.RateRows();
        // 「1 ? = ? 基準幣」那一列的下拉要有東西可選（之前沒接 ItemsSource：開得來但選不了）。
        // 人已經打進去的那個代碼不准被重灌弄掉。
        var typed = _rateFrom.Text;
        _rateFrom.ItemsSource = codes.Where(c => c != _store.BaseCurrency).ToList();
        _rateFrom.Text = typed;
    }

    void Say(string message) => _status.Text = message;
}
