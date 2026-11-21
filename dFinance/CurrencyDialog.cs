using System.Windows;
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
    readonly ComboBox _base = new() { Width = 90, ToolTip = "淨值、每月總資產都用這種幣表達" };
    readonly DataGrid _ccyGrid = new() { AutoGenerateColumns = false, CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column };
    readonly DataGrid _rateGrid = new() { AutoGenerateColumns = false, CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column };
    readonly ComboBox _rateFrom = new() { Width = 80, IsEditable = true, ToolTip = "例 USD" };
    readonly TextBox _rateValue = new() { Width = 90, ToolTip = "1 個上面那個幣 = ? 個基準幣" };
    readonly TextBox _proxy = new() { Width = 230, ToolTip = "例 http://10.191.190.5:3128；空白 = 直連（公司網直連會連不出去）" };
    readonly TextBox _status = new() { IsReadOnly = true, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, TextWrapping = TextWrapping.Wrap };

    public CurrencyDialog(LedgerStore store)
    {
        _store = store;
        Title = "幣別與匯率"; Width = 660; Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        BuildBaseRow();
        BuildCurrencyGrid();
        BuildRateGrid();

        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(Section("基準幣"));
        root.Children.Add(_baseRow);
        root.Children.Add(new TextBlock { Text = "匯率是「1 個外幣 = ? 基準幣」，存的是幣對，所以換基準幣不會弄髒既有匯率。", Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 2, 0, 8) });
        root.Children.Add(Section("幣別字典"));
        root.Children.Add(_ccyGrid);
        root.Children.Add(CurrencyAddRow());
        root.Children.Add(Section("匯率"));
        root.Children.Add(_rateGrid);
        root.Children.Add(RateRow());
        root.Children.Add(NetworkRow());
        root.Children.Add(_status);
        Content = root;
        Reload();
    }

    static TextBlock Section(string title) => new() { Text = title, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 4) };

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
            Say($"基準幣改成 {ccy}；沒匯率的帳戶會暫時不計入淨值（不瞎算 1:1）。");
        };
        _baseRow.Children.Add(new TextBlock { Text = "報表用幣別", VerticalAlignment = VerticalAlignment.Center });
        _baseRow.Children.Add(_base);
        _baseRow.Children.Add(new TextBlock { Text = "　基準幣只是報表口徑，匯率存的是幣對，所以改它不會弄髒既有數字", VerticalAlignment = VerticalAlignment.Center, Foreground = System.Windows.Media.Brushes.Gray });
    }

    // ---------------------------------------------------------------- 幣別字典

    void BuildCurrencyGrid()
    {
        _ccyGrid.Columns.Add(Column("代碼", nameof(LedgerStore.CurrencyRow.Code), 70, readOnly: true));
        _ccyGrid.Columns.Add(Column("名稱", nameof(LedgerStore.CurrencyRow.Name), 110));
        _ccyGrid.Columns.Add(Column("小數位", nameof(LedgerStore.CurrencyRow.Scale), 60));
        _ccyGrid.Columns.Add(Column("別名（匯入用的原文）", nameof(LedgerStore.CurrencyRow.Aliases), 320));
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
    readonly TextBox _newAliases = new() { Width = 190, ToolTip = "匯入時單子上寫的原文，用 | 分隔" };

    UIElement CurrencyAddRow()
    {
        var save = new Button { Content = "存幣別", Width = 70, Margin = new Thickness(6, 0, 0, 0) };
        save.Click += (_, _) =>
        {
            try
            {
                _store.SaveCurrency(_newCode.Text, _newName.Text, int.TryParse(_newScale.Text, out var s) ? s : 2, _newAliases.Text);
                Reload();
                Say($"幣別 {_newCode.Text.Trim().ToUpperInvariant()} 存好了。");
            }
            catch (Exception ex) { Say(ex.Message); }              // 驗證不過就留下來讓人補
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "代碼", VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(_newCode);
        panel.Children.Add(new TextBlock { Text = " 名稱", VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(_newName);
        panel.Children.Add(new TextBlock { Text = " 小數位", VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(_newScale);
        panel.Children.Add(new TextBlock { Text = " 別名", VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(_newAliases);
        panel.Children.Add(save);
        return panel;
    }

    // ---------------------------------------------------------------- 匯率

    void BuildRateGrid()
    {
        _rateGrid.Columns.Add(Column("外幣", nameof(LedgerStore.RateRow.From), 70, readOnly: true));
        _rateGrid.Columns.Add(Column("→ 基準幣", nameof(LedgerStore.RateRow.To), 80, readOnly: true));
        _rateGrid.Columns.Add(Column("匯率", nameof(LedgerStore.RateRow.Rate), 90, readOnly: true));
        _rateGrid.Columns.Add(Column("報價日期", nameof(LedgerStore.RateRow.RateDate), 90, readOnly: true));
        _rateGrid.Columns.Add(Column("來源", nameof(LedgerStore.RateRow.Source), 70, readOnly: true));
        _rateGrid.Columns.Add(Column("⚠", nameof(LedgerStore.RateRow.StaleText), 80, readOnly: true));
        _rateGrid.SelectionChanged += (_, _) =>
        {
            if (_rateGrid.SelectedItem is not LedgerStore.RateRow row) return;
            _rateFrom.Text = row.From; _rateValue.Text = row.Rate.ToString("0.######");
        };
    }

    UIElement RateRow()
    {
        var save = new Button { Content = "人工填", Width = 70, Margin = new Thickness(6, 0, 0, 0),
                                ToolTip = "留空並選一列後按它 = 故意不折算（那個帳戶就不進淨值）" };
        save.Click += (_, _) =>
        {
            var from = _rateFrom.Text;
            if (decimal.TryParse(_rateValue.Text.Replace(",", "").Trim(), out var rate) && rate > 0) _store.SetRate(from, rate);
            else if (string.IsNullOrWhiteSpace(_rateValue.Text)) _store.SetRate(from, null);
            else { Say("匯率要是一個大於 0 的數字（例 32.9），或留空當做不折算。"); return; }
            Reload();
            Say($"{from} → {_store.BaseCurrency} 記好了。");
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "1", VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(_rateFrom);
        panel.Children.Add(new TextBlock { Text = $" = ?  {_store.BaseCurrency}", VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(_rateValue);
        panel.Children.Add(save);
        return panel;
    }

    // ---------------------------------------------------------------- 網路

    UIElement NetworkRow()
    {
        _proxy.Text = _store.RateProxy;

        var fetch = new Button { Content = "抓匯率（帳戶用得到的幣對）", Margin = new Thickness(6, 0, 0, 0) };
        fetch.Click += (_, _) =>
        {
            var source = NewSource();
            Say("抓匯率中…");
            var (updated, errors) = source.Refresh(_store);
            Reload();
            Say(errors.Count == 0 ? $"更新了 {updated} 個幣對。"
                                  : $"更新 {updated} 個；" + string.Join("；", errors));
        };

        var test = new Button { Content = "測試連線", Margin = new Thickness(6, 0, 0, 0),
                                ToolTip = "打一個幣對看回什麼（200/429/404/連不出去）" };
        test.Click += (_, _) => { Say("測試中…"); Say(NewSource().Test(_store)); };

        var split = new Button { Content = "把幣別不符的列拆到對的帳戶", Margin = new Thickness(18, 0, 0, 0),
                                 ToolTip = "舊資料：美金列直接倒在台幣帳戶時用（那些金額會照台幣進淨值）" };
        split.Click += (_, _) =>
        {
            int moved = _store.SplitForeignCurrencyEntries();
            Reload();
            Say(moved == 0 ? "沒有幣別跟帳戶不一樣的列。" : $"搬了 {moved} 筆到各自幣別的帳戶（對不上就從備份還原 data\\backups）。");
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
    }

    void Say(string message) => _status.Text = message;
}
