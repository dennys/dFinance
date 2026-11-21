using System.Windows;
using dFinance.Data.Localization;
using System.Windows.Controls;
using dFinance.Data;

namespace dFinance;

/// <summary>
/// 手動記一筆。現金與錢包一定沒有 PDF，這是它們唯一的入口。
/// 程式碼建介面（不用 XAML）：欄位就這幾個，多一個檔案不值得。
/// </summary>
public sealed class AddEntryDialog : Window
{
    readonly LedgerStore _store;
    readonly DatePicker _date = new() { SelectedDate = DateTime.Today };
    readonly ComboBox _kind = new();
    readonly ComboBox _account = new();
    readonly ComboBox _toAccount = new();
    readonly ComboBox _category = new();
    readonly TextBox _merchant = new();
    readonly TextBox _amount = new();
    readonly TextBox _note = new();
    readonly TextBlock _toLabel = new() { Text = Loc.T("Entry_ToAccount") };

    public AddEntryDialog(LedgerStore store, long? defaultAccountId)
    {
        _store = store;
        Title = Loc.T("Entry_Title"); Width = 380; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _kind.ItemsSource = new[] { Loc.T("Entry_Kind_Expense"), Loc.T("Entry_Kind_Income"), Loc.T("Entry_Kind_Refund"), Loc.T("Entry_Kind_Adjustment"), Loc.T("Entry_Kind_Transfer") };
        _kind.SelectedIndex = 0;
        _kind.SelectionChanged += (_, _) => { SyncVisibility(); ReloadCategories(); };

        var accounts = store.Accounts();
        foreach (var box in new[] { _account, _toAccount })
        {
            box.ItemsSource = accounts; box.DisplayMemberPath = "Name"; box.SelectedValuePath = "Id";
        }
        _account.SelectedItem = accounts.FirstOrDefault(a => a.Id == defaultAccountId) ?? accounts.FirstOrDefault();
        _category.DisplayMemberPath = "Name"; _category.SelectedValuePath = "Id";
        _amount.ToolTip = Loc.T("Entry_KindHint");

        var grid = new Grid { Margin = new Thickness(12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var ok = new Button { Content = Loc.T("Common_Save"), Width = 80, IsDefault = true, Margin = new Thickness(0, 10, 8, 0) };
        var cancel = new Button { Content = Loc.T("Common_Cancel"), Width = 80, IsCancel = true, Margin = new Thickness(0, 10, 0, 0) };
        ok.Click += (_, _) => Save();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);

        var i = 0;
        Row(i++, Loc.T("Common_Date"), _date);
        Row(i++, Loc.T("Common_Kind"), _kind);
        Row(i++, Loc.T("Common_Account"), _account);
        Row(i++, Loc.T("Common_Merchant"), _merchant);
        Row(i++, Loc.T("Common_Category"), _category);
        Row(i++, Loc.T("Common_Amount"), _amount);
        Row(i++, Loc.T("Common_Note"), _note);
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(buttons, i); Grid.SetColumn(buttons, 0); Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(buttons);
        Content = grid;

        void Row(int index, string label, Control control)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            control.Margin = new Thickness(0, 4, 0, 4);
            control.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            Grid.SetRow(control, index); Grid.SetColumn(control, 1);
            var t = new TextBlock
            {
                Text = label, Margin = new Thickness(0, 4, 8, 4),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetRow(t, index); Grid.SetColumn(t, 0);
            if (control == _toAccount) { Grid.SetRow(_toLabel, index); Grid.SetColumn(_toLabel, 0); grid.Children.Add(_toLabel); }
            grid.Children.Add(t); grid.Children.Add(control);
        }

        SyncVisibility(); ReloadCategories();
    }

    string Kind => _kind.SelectedIndex switch { 0 => "expense", 1 => "income", 2 => "refund", 3 => "adjustment", 4 => "transfer", _ => "expense" };

    void SyncVisibility()
    {
        bool transfer = Kind == "transfer";
        _toAccount.Visibility = _toLabel.Visibility = transfer ? Visibility.Visible : Visibility.Collapsed;
        _category.Visibility = transfer ? Visibility.Collapsed : Visibility.Visible;
    }

    void ReloadCategories()
        => _category.ItemsSource = _store.Categories(Kind == "income" ? "income" : "expense");

    void Save()
    {
        var dateText = _date.SelectedDate?.ToString("yyyy-MM-dd") ?? _date.Text;
        if (!LedgerStore.TryDate(dateText, out var date) || date is null)
        {
            MessageBox.Show(Loc.T("Entry_BadDate"), Loc.T("Entry_Title")); return;
        }
        if (!decimal.TryParse(_amount.Text.Replace(",", "").Trim(), out var amount))
        {
            MessageBox.Show(Loc.T("Entry_BadAmount"), Loc.T("Entry_Title")); return;
        }

        try
        {
            _store.InsertManual(new LedgerStore.ManualEntry(
                AccountId: Convert.ToInt64(_account.SelectedValue ?? 0),
                Kind: Kind,
                AmountMinor: Money.ToMinor(amount),
                TxnDate: date,
                Merchant: string.IsNullOrWhiteSpace(_merchant.Text) ? null : _merchant.Text.Trim(),
                CategoryId: _category.SelectedValue as long?,
                ToAccountId: _toAccount.SelectedValue as long?,
                Note: string.IsNullOrWhiteSpace(_note.Text) ? null : _note.Text.Trim()));
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Loc.T("Entry_Title"));            // 驗證不過就留下來讓人補，不默默吞掉
        }
    }
}

/// <summary>新增分類。「＋ 分類」用的陽春輸入框，同名會沿用既有那筆。</summary>
public sealed class CategoryDialog : Window
{
    readonly TextBox _name = new();
    readonly ComboBox _kind = new() { SelectedIndex = 0 };

    public string Name => _name.Text.Trim();
    public string Kind => _kind.SelectedIndex == 0 ? "expense" : "income";

    public CategoryDialog()
    {
        Title = Loc.T("Category_Add"); Width = 300; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _kind.ItemsSource = new[] { Loc.T("Entry_Kind_Expense"), Loc.T("Entry_Kind_Income") };

        var ok = new Button { Content = Loc.T("Common_Save"), Width = 70, IsDefault = true, Margin = new Thickness(0, 10, 8, 0) };
        var cancel = new Button { Content = Loc.T("Common_Cancel"), Width = 70, IsCancel = true, Margin = new Thickness(0, 10, 0, 0) };
        ok.Click += (_, _) =>
        {
            if (Name.Length == 0) { MessageBox.Show(Loc.T("Category_NeedName"), Loc.T("Category_Add")); return; }
            DialogResult = true;
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = Loc.T("Common_Name") });
        panel.Children.Add(_name);
        panel.Children.Add(new TextBlock { Text = Loc.T("Common_Kind"), Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(_kind);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => _name.Focus();
    }
}
