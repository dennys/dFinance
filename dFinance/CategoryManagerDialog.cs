using System.Windows;
using dFinance.Data.Localization;
using System.Windows.Controls;
using dFinance.Data;

namespace dFinance;

/// <summary>工具 ? 分類：列出支出／收入分類，可改名、可新增（同名沿用既有）。程式碼建介面，同 AddEntryDialog。</summary>
public sealed class CategoryManagerDialog : Window
{
    readonly LedgerStore _store;
    readonly DataGrid _grid = new() { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, HeadersVisibility = DataGridHeadersVisibility.Column };
    /// <summary>給 <c>--uismoke</c>：視窗是純 code-behind 建的（排版/欄位壞了要靠這個抓）。</summary>
    internal int RowCount => _grid.Items.Count;
    readonly TextBox _newName = new() { Width = 140 };
    readonly ComboBox _newKind = new() { Width = 80, SelectedIndex = 0 };
    readonly ComboBox _newParent = new() { Width = 120, DisplayMemberPath = nameof(LedgerStore.Option.Name), SelectedValuePath = nameof(LedgerStore.Option.Id) };

    public CategoryManagerDialog(LedgerStore store)
    {
        _store = store;
        Title = Loc.T("Category_Title");
        Width = 640; Height = 470;                     // 560 宽会让下面那一列（新增／关闭）被遮一半
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _grid.Columns.Add(new DataGridTextColumn { Header = Loc.T("Category_Direction"), Binding = new System.Windows.Data.Binding(nameof(LedgerStore.CategoryRow.Kind)) { Converter = KindConverter.Instance }, IsReadOnly = true, Width = 60 });
        _grid.Columns.Add(new DataGridTextColumn { Header = Loc.T("Category_Hierarchy"), Binding = new System.Windows.Data.Binding(nameof(LedgerStore.CategoryRow.Indented)), IsReadOnly = true, Width = new DataGridLength(3, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = Loc.T("Category_NameEditable"), Binding = new System.Windows.Data.Binding(nameof(LedgerStore.CategoryRow.Name)), Width = new DataGridLength(2, DataGridLengthUnitType.Star),
            ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Left) } },
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = Loc.T("Category_Uses"), Binding = new System.Windows.Data.Binding(nameof(LedgerStore.CategoryRow.Uses)), IsReadOnly = true, Width = 70,
            // 靠右但要留白：贴着 scrollbar 会让人以为那列被切掉了。
            ElementStyle = new Style(typeof(TextBlock))
            {
                Setters =
                {
                    new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right),
                    new Setter(TextBlock.MarginProperty, new Thickness(0, 0, 10, 0)),
                },
            },
        });
        _grid.CellEditEnding += Grid_CellEditEnding;

        _newKind.ItemsSource = new[] { Loc.T("Entry_Kind_Expense"), Loc.T("Entry_Kind_Income") };
        _newKind.SelectionChanged += (_, _) => ReloadParents();
        var add = new Button { Content = Loc.T("Category_Add_Btn"), Width = 70, Margin = new Thickness(8, 0, 0, 0) };
        add.Click += (_, _) => AddNew();
        var close = new Button { Content = Loc.T("Common_Close"), Width = 70, IsCancel = true, IsDefault = true, Margin = new Thickness(18, 0, 0, 0) };
        close.Click += (_, _) => Close();

        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        addRow.Children.Add(new TextBlock { Text = Loc.T("Category_NewLabel"), VerticalAlignment = VerticalAlignment.Center });
        addRow.Children.Add(_newKind);
        addRow.Children.Add(new TextBlock { Text = Loc.T("Category_ParentLabel"), VerticalAlignment = VerticalAlignment.Center });
        addRow.Children.Add(_newParent);
        addRow.Children.Add(new TextBlock { Text = Loc.T("Category_NameLabel"), VerticalAlignment = VerticalAlignment.Center });
        addRow.Children.Add(_newName);
        addRow.Children.Add(add);
        addRow.Children.Add(close);          // 跟在「新增」同一列：单独占一列只是浪费高度

        var panel = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(addRow, Dock.Bottom);
        panel.Children.Add(addRow);
        panel.Children.Add(_grid);
        Content = panel;

        Reload();
        ReloadParents();
    }

    void Reload() => _grid.ItemsSource = _store.AllCategories();

    void ReloadParents()
    {
        var kind = _newKind.SelectedIndex == 1 ? "income" : "expense";
        var items = new List<LedgerStore.Option> { new(0, Loc.T("Category_TopLevel")) };
        items.AddRange(_store.Categories(kind));        // 不带 leavesOnly：要能選父類（含完整路徑）
        _newParent.ItemsSource = items;
        _newParent.SelectedIndex = 0;
    }

    void Grid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not LedgerStore.CategoryRow row) return;
        if (e.EditingElement is not TextBox box) return;
        var name = box.Text;
        if (name.Trim() == row.Name) return;
        try { _store.RenameCategory(row.Id, name); }
        catch (ArgumentException ex) { MessageBox.Show(ex.Message, Loc.T("Category_Title")); }
        Dispatcher.BeginInvoke(new Action(Reload));
    }

    void AddNew()
    {
        var name = _newName.Text.Trim();
        if (name.Length == 0) { MessageBox.Show(Loc.T("Category_NeedNameMsg"), Loc.T("Category_Title")); return; }
        var parentId = _newParent.SelectedValue is long pid && pid != 0 ? pid : (long?)null;
        try { _store.AddCategory(name, _newKind.SelectedIndex == 0 ? "expense" : "income", parentId); }
        catch (ArgumentException ex) { MessageBox.Show(ex.Message, Loc.T("Category_Title")); return; }
        _newName.Text = "";
        Reload();
        ReloadParents();
    }

    sealed class KindConverter : System.Windows.Data.IValueConverter
    {
        public static readonly KindConverter Instance = new();
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => value as string == "income" ? Loc.T("Entry_Kind_Income") : Loc.T("Entry_Kind_Expense");
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }
}
