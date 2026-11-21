using System.Windows;
using System.Windows.Controls;
using dFinance.Data;

namespace dFinance;

/// <summary>
/// 帳戶管理。匯入只會自動建帳戶，所以現金/錢包/外幣帳戶、期初餘額、卡的額度與自動扣繳，
/// 還有「我實際有多少錢」的盤點，都只能從這裡進。
/// 一樣是程式碼建介面（跟 <see cref="AddEntryDialog"/> 同一套路，多一個 XAML 檔案不值得）。
/// </summary>
public sealed class AccountsDialog : Window
{
    readonly LedgerStore _store;
    readonly ListBox _list = new() { Width = 250 };
    readonly TextBox _name = new();
    readonly ComboBox _type = new();
    readonly ComboBox _currency = new();
    readonly TextBox _rate = new() { ToolTip = "1 個外幣 = 多少台幣。留空 = 故意不折算（那個帳戶就不進淨值）" };
    readonly TextBox _issuer = new() { ToolTip = "聯邦商銀 / 台灣電力公司 / JKO Pay…" };
    readonly TextBox _mask = new() { ToolTip = "帳號或卡號末幾碼" };
    readonly TextBox _opening = new() { ToolTip = "開始記帳那天有多少錢；卡的期初通常是 0，欠錢靠交易累成負數" };
    readonly DatePicker _openingDate = new();
    readonly TextBox _limit = new();
    readonly ComboBox _autopay = new() { DisplayMemberPath = "Name", SelectedValuePath = "Id" };
    readonly TextBlock _info = new()
    {
        Foreground = System.Windows.Media.Brushes.Gray, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 10, 0, 0),
    };
    readonly Button _archive = new() { Content = "歸檔", Width = 70 };
    readonly Button _count = new() { Content = "餘額盤點…", Width = 90 };
    TextBlock _rateLabel = new(), _limitLabel = new(), _autopayLabel = new();

    long _id;                                       // 0 = 還在新建
    bool _dirty;                                    // 關掉時讓主頁知道要重查
    bool _filling;                                  // 灌表單途中別讓 SelectionChanged 插進來改可見度

    public AccountsDialog(LedgerStore store)
    {
        _store = store;
        Title = "帳戶管理"; Width = 780; Height = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _type.ItemsSource = LedgerStore.AccountTypes.Select(t => t.Label).ToList();
        _type.SelectedIndex = 1;                                        // 預設銀行
        _currency.ItemsSource = store.Currencies();
        _currency.SelectedItem = "TWD";
        _type.SelectionChanged += (_, _) => SyncFields();
        _currency.SelectionChanged += (_, _) => SyncFields();
        _autopay.ItemsSource = store.Accounts().Where(a => a.Type is "bank" or "wallet").ToList();
        _list.SelectionChanged += (_, _) => FillForm();

        var left = new DockPanel { Margin = new Thickness(12, 12, 8, 0) };
        var add = new Button { Content = "＋ 新增帳戶", Margin = new Thickness(0, 6, 0, 0) };
        add.Click += (_, _) => ClearForm();
        DockPanel.SetDock(add, Dock.Bottom);
        left.Children.Add(add);
        left.Children.Add(_list);

        var form = new Grid { Margin = new Thickness(0, 12, 12, 0) };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int i = 0;
        Row(i++, "名稱", _name);
        Row(i++, "類型", _type);
        Row(i++, "幣別", _currency);
        _rateLabel = Row(i++, "匯率", _rate);
        Row(i++, "發行機構", _issuer);
        Row(i++, "末幾碼", _mask);
        Row(i++, "期初餘額", _opening);
        Row(i++, "期初日期", _openingDate);
        _limitLabel = Row(i++, "信用額度", _limit);
        _autopayLabel = Row(i++, "自動扣繳", _autopay);
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_info, i); Grid.SetColumn(_info, 0); Grid.SetColumnSpan(_info, 2);
        form.Children.Add(_info);

        var save = new Button { Content = "存檔", Width = 70, Margin = new Thickness(0, 0, 4, 0), IsDefault = true };
        var close = new Button { Content = "關閉", Width = 70, IsCancel = true };
        save.Click += (_, _) => Save();
        _archive.Click += (_, _) => Archive();
        _count.Click += (_, _) => Count();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        buttons.Children.Add(save); buttons.Children.Add(_archive); buttons.Children.Add(_count); buttons.Children.Add(close);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(buttons); root.Children.Add(left); root.Children.Add(form);
        Content = root;

        Reload();
        return;

        TextBlock Row(int index, string label, Control control)
        {
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            control.Margin = new Thickness(0, 3, 0, 3);
            control.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            Grid.SetRow(control, index); Grid.SetColumn(control, 1);
            var t = new TextBlock { Text = label, Margin = new Thickness(0, 3, 8, 3), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(t, index); Grid.SetColumn(t, 0);
            form.Children.Add(t); form.Children.Add(control);
            return t;
        }
    }

    /// <summary>給 --uismoke 看的：清單載入到幾個帳戶（0 就是 AccountsAll 没吐东西）。</summary>
    public int AccountCount => ((IEnumerable<LedgerStore.AccountEdit>)_list.ItemsSource).Count();

    void Reload()
    {
        var accounts = _store.AccountsAll();
        _list.ItemsSource = accounts;
        _list.SelectedItem = accounts.FirstOrDefault(a => a.Id == _id);
        if (_list.SelectedItem is null) ClearForm();
    }

    string TypeValue => _type.SelectedIndex >= 0 ? LedgerStore.AccountTypes[_type.SelectedIndex].Value : "bank";
    string CurrencyValue => _currency.SelectedItem as string ?? _store.BaseCurrency;

    /// <summary>信用額度/自動扣繳只有信用卡用得到，匯率只有外幣用得到 —— 不對的組合別擺在那裡讓人填。</summary>
    void SyncFields()
    {
        if (_filling) return;
        bool card = TypeValue == "credit_card", foreign = CurrencyValue != _store.BaseCurrency;
        _rateLabel.Text = $"匯率（1 {CurrencyValue} = ? {_store.BaseCurrency}）";   // 方向寫在標籤上，不然人不知道在填什麼
        _limit.Visibility = _limitLabel.Visibility = card ? Visibility.Visible : Visibility.Collapsed;
        _autopay.Visibility = _autopayLabel.Visibility = card ? Visibility.Visible : Visibility.Collapsed;
        _rate.Visibility = _rateLabel.Visibility = foreign ? Visibility.Visible : Visibility.Collapsed;
    }

    void ClearForm()
    {
        _filling = true;
        _id = 0;
        _name.Text = ""; _issuer.Text = ""; _mask.Text = ""; _opening.Text = ""; _openingDate.Text = "";
        _limit.Text = ""; _rate.Text = ""; _autopay.SelectedItem = null;
        _type.SelectedIndex = 1; _currency.SelectedItem = _store.BaseCurrency;
        _filling = false;
        _list.SelectedItem = null;
        SyncFields();
        _info.Text = "新帳戶：填好按「存檔」。現金/錢包沒有 PDF，只能從這裡建。";
        _archive.IsEnabled = _count.IsEnabled = false;
    }

    void FillForm()
    {
        if (_list.SelectedItem is not LedgerStore.AccountEdit a) return;
        _filling = true;
        _id = a.Id;
        _name.Text = a.Name; _issuer.Text = a.Issuer; _mask.Text = a.Mask;
        _opening.Text = Money.FromMinor(a.OpeningBalanceMinor).ToString("0.##");
        _openingDate.Text = a.OpeningDate ?? "";
        _limit.Text = a.CreditLimitMinor is { } lim ? Money.FromMinor(lim).ToString("0.##") : "";
        _autopay.SelectedValue = a.AutopayAccountId;
        if (!_currency.Items.Contains(a.Currency))                        // 匯入可能帶進字典裡沒的幣別
            _currency.ItemsSource = _store.Currencies().Append(a.Currency).Distinct().ToList();
        _currency.SelectedItem = a.Currency;
        _type.SelectedIndex = Array.FindIndex(LedgerStore.AccountTypes, t => t.Value == a.Type);
        _rate.Text = _store.Rates().TryGetValue(a.Currency, out var r) ? r.ToString("0.####") : "";
        _filling = false;
        SyncFields();

        var counted = _store.NetWorth().Accounts.FirstOrDefault(x => x.Id == a.Id);
        _info.Text = $"系統推導餘額 {Money.Format(a.BalanceMinor, a.Scale)} {a.Currency}"
                   + (a.ManualBalanceMinor is { } mb ? $"　｜　最近盤點 {Money.Format(mb, a.Scale)}（{a.ManualBalanceDate}）" : "　｜　還沒盤點過")
                   + (counted is null ? "　｜　已歸檔，不計入淨值" : $"　｜　{counted.CountedText}");
        _archive.Content = a.IsArchived ? "還原" : "歸檔";
        _archive.IsEnabled = _count.IsEnabled = true;
        _name.Focus();
    }

    void Save()
    {
        if (!TryMinor(_opening.Text, out var opening)) { MessageBox.Show("期初餘額要是一個數字。", "帳戶管理"); return; }
        long? limit = null;
        if (!string.IsNullOrWhiteSpace(_limit.Text))
        {
            if (!TryMinor(_limit.Text, out var lim)) { MessageBox.Show("信用額度要是一個數字。", "帳戶管理"); return; }
            limit = lim;
        }
        decimal? rate = null;
        if (!string.IsNullOrWhiteSpace(_rate.Text))
        {
            if (!decimal.TryParse(_rate.Text.Replace(",", "").Trim(), out var r) || r <= 0)
            { MessageBox.Show("匯率要是一個大於 0 的數字（例 32.9）。", "帳戶管理"); return; }
            rate = r;
        }

        var edit = new LedgerStore.AccountEdit(
            _id, _name.Text, TypeValue, CurrencyValue, Blank(_issuer.Text), Blank(_mask.Text), opening,
            string.IsNullOrWhiteSpace(_openingDate.Text) ? null : _openingDate.Text,
            _store.GetAccount(_id)?.IsArchived == true, limit, _autopay.SelectedValue as long?, 0, null, null);
        try
        {
            if (_id == 0) _id = _store.CreateAccount(edit);
            else _store.UpdateAccount(edit);
            _store.SetRate(edit.Currency, rate);
            _dirty = true;
            Reload();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "帳戶管理");                        // 驗證不過就留下來讓人補，不默默吞掉
        }
    }

    void Archive()
    {
        if (_store.GetAccount(_id) is not { } a) return;
        _store.SetArchived(a.Id, !a.IsArchived);
        _dirty = true;
        Reload();
    }

    void Count()
    {
        if (_store.GetAccount(_id) is not { } a) return;
        if (new BalanceCountDialog(_store, a) { Owner = this }.ShowDialog() == true) { _dirty = true; Reload(); }
    }

    static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>空白當 0（期初沒填就是 0），其它打不進數字就擋下來講人話。</summary>
    static bool TryMinor(string? text, out long minor)
    {
        minor = 0;
        if (string.IsNullOrWhiteSpace(text)) return true;
        return decimal.TryParse(text.Replace(",", "").Trim(), out var v) && (minor = Money.ToMinor(v)) == minor;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_dirty) LedgerStore.Touch();                                    // 開著的帳本分頁切過去時會重查
        base.OnClosed(e);
    }
}

/// <summary>
/// 餘額盤點：人當場數的錢寫進 account_balance（source='manual'）。
/// 這是餘額校驗的輸入之一 —— 對不上就在主頁底部講，但不覆蓋交易推出來的數字。
/// 差額可以順手記一筆 adjustment（金額唯讀，調帳一律走調整）。
/// </summary>
public sealed class BalanceCountDialog : Window
{
    readonly LedgerStore _store;
    readonly LedgerStore.AccountEdit _account;
    readonly TextBox _amount = new();
    readonly DatePicker _date = new() { SelectedDate = DateTime.Today };

    public BalanceCountDialog(LedgerStore store, LedgerStore.AccountEdit account)
    {
        _store = store; _account = account;
        Title = $"餘額盤點 — {account.Name}"; Width = 400; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock
        {
            Text = $"系統從交易算出來的是 {Money.Format(account.BalanceMinor, account.Scale)} {account.Currency}。" +
                   "對不上通常不是算錯，是漏了交易或期初填錯。",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock { Text = "我實際有", Margin = new Thickness(0, 10, 0, 0) });
        panel.Children.Add(_amount);
        panel.Children.Add(new TextBlock { Text = "盤點日期", Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(_date);

        var ok = new Button { Content = "存檔", Width = 80, IsDefault = true, Margin = new Thickness(0, 12, 8, 0) };
        var cancel = new Button { Content = "取消", Width = 80, IsCancel = true, Margin = new Thickness(0, 12, 0, 0) };
        ok.Click += (_, _) => Save();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Content = panel;
        Loaded += (_, _) => _amount.Focus();
    }

    void Save()
    {
        if (!decimal.TryParse(_amount.Text.Replace(",", "").Trim(), out var amount))
        { MessageBox.Show("餘額要是一個數字。", "餘額盤點"); return; }
        var dateText = _date.SelectedDate?.ToString("yyyy-MM-dd") ?? _date.Text;
        if (!LedgerStore.TryDate(dateText, out var date) || date is null)
        { MessageBox.Show("日期要用 yyyy/MM/dd。", "餘額盤點"); return; }

        var counted = Money.ToMinor(amount);
        try
        {
            _store.AddBalanceSnapshot(_account.Id, date, _account.Currency, counted);
            var diff = counted - _account.BalanceMinor;
            if (diff != 0 && MessageBox.Show(
                    $"盤點 {Money.Format(counted, _account.Scale)}，交易累加 {Money.Format(_account.BalanceMinor, _account.Scale)}，差 {Money.Format(diff, _account.Scale)}。\n\n" +
                    "要記一筆調整把差額補齊嗎？（選「否」就只留快照，主頁底部會把它列成餘額不符）",
                    "餘額盤點", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _store.InsertManual(new LedgerStore.ManualEntry(_account.Id, "adjustment", diff, date,
                                                                 null, null, null, "盤點調整", _account.Currency));
            }
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "餘額盤點");
        }
    }
}
