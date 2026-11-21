using System.IO;
using dFinance.Data.Localization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using dFinance.Parsing;
using dFinance.Data;
using Microsoft.Data.Sqlite;

namespace dFinance
{
    public partial class MainWindow : Window
    {
        private SqliteConnection? _db;
        private readonly Dictionary<long, TabItem> _accountTabs = [];
        private readonly List<TabItem> _drillDownTabs = [];
        private TabItem? _categoryTab;

        public MainWindow()
        {
            InitializeComponent();
            RebuildRecentMenu();
            OpenDatabase();
            LocalizationManager.Current.ChangeCulture(_db is { } db ? Db.UiLanguage(db) : LocalizationManager.DefaultCulture);
            LocalizationManager.Current.CultureChanged += RebuildForCulture;
            RebuildLanguageMenu();                      // 啟動時已經换過語系（那時候還沒訂閱），選單要自己補一次
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.W && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) CloseSelectedAccountTab();
            };
        }

        // ------------------------------------------------------------ 語系

        /// <summary>工具 → 語言：選了立刻換，不用重開程式。選單本身從 <see cref="LocalizationManager.Languages") 產，加語系只改那一張表。</summary>
        void RebuildLanguageMenu()
        {
            LanguageMenu.Items.Clear();
            foreach (var (code, native) in LocalizationManager.Languages)
            {
                var item = new MenuItem { Header = native, IsChecked = string.Equals(code, LocalizationManager.Current.UiCulture.Name, StringComparison.OrdinalIgnoreCase) };
                item.Click += (_, _) =>
                {
                    LocalizationManager.Current.ChangeCulture(code);
                    if (_db is { } db) Db.SetUiLanguage(db, code);
                };
                LanguageMenu.Items.Add(item);
            }
        }

        /// <summary>
        /// 換語系後重建分頁。XAML 宣告的文字靠綁定自己更新，但 code 裡一次指派出去的欄頭、
        /// 篩選下拉、圖表標題不吃綁定，只能重建。drill-down 分頁帶這篩選條件，重建成本高，
        /// 就關掉請人重開（shortcut: 要留條件就把 from/to 存起來重建時補回）。
        /// </summary>
        void RebuildForCulture()
        {
            RebuildRecentMenu();
            RebuildLanguageMenu();
            if (_db is not { } db) return;

            var open = _accountTabs.Keys.ToList();
            foreach (var item in _accountTabs.Values) Tabs.Items.Remove(item);
            _accountTabs.Clear();
            foreach (var item in _drillDownTabs.ToList()) CloseDrillDownTab(item);

            HomeTab.Content = NewLedger(null, Loc.T("Tab_Home"));
            BuildCategoryTab();
            foreach (var id in open)
            {
                var name = Db.Scalar(db, "SELECT name FROM account WHERE id=$i;", ("$i", id)) as string ?? id.ToString();
                OpenAccountTab(id, name);
            }
            Tabs.SelectedItem = HomeTab;
        }

        /// <summary>啟動就開庫、跑遷移、備份一份（留最近 5 份），並把主頁接上庫。</summary>
        void OpenDatabase()
        {
            OpenDatabasePath(Db.DatabasePath);
            if (_db is null) return;
            try { Db.Backup(_db); }
            catch (Exception ex) { MessageBox.Show(Loc.F("Db_BackupFailed", ex.Message), Loc.T("Db_BackupTitle")); }
        }

        /// <summary>換一個資料庫檔（開檔/新建/另存都走這裡）。舊的一律等新庫開成功才關，開失敗就維持原狀。</summary>
        void OpenDatabasePath(string path)
        {
            if (SamePath(path, _db?.DataSource) && _db is not null) return;      // 已經開著同一個檔
            if (File.Exists(path) && !IsDFinanceDatabase(path))
            {
                MessageBox.Show(Loc.T("Db_NotDFinance"), Loc.T("Db_OpenTitle"));
                return;
            }

            try
            {
                var db = Db.Open(path);
                var previous = _db;
                _db = db;

                // 先收掉舊分頁再關舊連線：收的過程如果還被叫去重查，起碼庫還是開著的。
                foreach (var item in _accountTabs.Values) Tabs.Items.Remove(item);
                _accountTabs.Clear();
                foreach (var item in _drillDownTabs) Tabs.Items.Remove(item);
                _drillDownTabs.Clear();
                new LedgerStore(db).PromoteCategorizedImports();
                HomeTab.Content = NewLedger(null, Loc.T("Tab_Home"));
                BuildCategoryTab();
                Tabs.SelectedItem = HomeTab;
                previous?.Dispose();

                DbPathText.Text = Loc.F("Db_PathLine", Path.GetFullPath(path), Db.DocumentsDirectory);
                Title = $"dFinance — {Path.GetFileName(path)}";
                RecentFiles.Add(path);
                RebuildRecentMenu();
                LocalizationManager.Current.ChangeCulture(Db.UiLanguage(db));      // 這份庫存過的語系（重建分頁由 CultureChanged 接手）
            }
            catch (Exception ex)
            {
                if (_db is null)
                {
                    HomeTab.Content = new TextBlock { Text = Loc.T("Db_OpenFailedHead") + ex.Message, Margin = new Thickness(8) };
                    DbPathText.Text = Loc.T("Db_OpenFailedImport");
                    ImportLog.Text = Loc.T("Db_OpenFailedLog") + ex.Message;
                }
                else MessageBox.Show(Loc.F("Db_OpenFailed", ex.Message), Loc.T("Db_OpenTitle"));
            }
        }

        // ------------------------------------------------------------ 帳本/帳戶分頁

        /// <summary>
        /// 帳戶分頁就是同一個 LedgerTab 傅入 accountId，不另寫一套版面。
        /// 分頁开著不關就不重查：换分頁時只比 LedgerStore.DataVersion，髒了才重載。
        /// </summary>
        LedgerTab NewLedger(long? accountId, string title, bool ledgerMode = false)
        {
            var tab = new LedgerTab(_db!, accountId, ledgerMode);
            tab.OpenAccountRequested += OpenAccountTab;
            tab.DrillDownRequested += OpenDrillDownTab;
            return tab;
        }

        void OpenDrillDownTab(string categoryPath, string? from, string? to)
        {
            if (_db is null) return;
            var name = categoryPath.Split('／')[^1];
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            var item = new TabItem { Header = header, Content = NewLedger(null, name, ledgerMode: true), ToolTip = categoryPath };
            var close = new Button
            {
                Content = "✕", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(3, 0, 3, 0),
                BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent,
                ToolTip = Loc.T("Tab_CloseHint"),
            };
            close.Click += (_, _) => CloseDrillDownTab(item);
            header.Children.Add(close);
            _drillDownTabs.Add(item);
            Tabs.Items.Insert(Tabs.Items.Count - 1, item);
            Tabs.SelectedItem = item;
            ((LedgerTab)item.Content).ApplyDrillDown(categoryPath, from, to);
        }

        void CloseDrillDownTab(TabItem item)
        {
            if (!_drillDownTabs.Remove(item)) return;
            Tabs.Items.Remove(item);
            Tabs.SelectedItem = HomeTab;
        }

        void OpenAccountTab(long accountId, string name)
        {
            if (_accountTabs.TryGetValue(accountId, out var existing)) { Tabs.SelectedItem = existing; return; }
            if (_db is null) return;

            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            var close = new Button
            {
                Content = "✕", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(3, 0, 3, 0),
                BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent,
                ToolTip = Loc.T("Tab_CloseHintKey"),
            };
            close.Click += (_, _) => CloseAccountTab(accountId);
            header.Children.Add(close);

            var item = new TabItem { Header = header, Content = NewLedger(accountId, name), ToolTip = name };
            _accountTabs[accountId] = item;
            Tabs.Items.Insert(Tabs.Items.Count - 1, item);          // 「匯入 PDF」永遠留最後
            Tabs.SelectedItem = item;
        }

        void CloseAccountTab(long accountId)
        {
            if (!_accountTabs.TryGetValue(accountId, out var item)) return;
            _accountTabs.Remove(accountId);
            Tabs.Items.Remove(item);
            Tabs.SelectedItem = HomeTab;
        }

        void CloseSelectedAccountTab()
        {
            foreach (var (id, item) in _accountTabs)
                if (ReferenceEquals(item, Tabs.SelectedItem)) { CloseAccountTab(id); return; }
            foreach (var item in _drillDownTabs.ToList())
                if (ReferenceEquals(item, Tabs.SelectedItem)) { CloseDrillDownTab(item); return; }
        }

        void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.OriginalSource != Tabs) return;                   // 分頁内部的 ComboBox 也会冒泡上来
            if ((Tabs.SelectedItem as TabItem)?.Content is LedgerTab tab) tab.RefreshIfStale();
        }

        /// <summary>給 <c>--uismoke</c> 用的钩子：真的開一個帳戶分頁，把 multi-tab 那条路徑也跑過。</summary>
        internal void SmokeOpenFirstAccountTab()
        {
            (HomeTab.Content as LedgerTab)?.RefreshIfStale();
            if (_db is not { } db) return;
            if (Db.Scalar(db, "SELECT a.id FROM account a JOIN entry e ON e.account_id = a.id WHERE e.deleted_at IS NULL LIMIT 1;") is not long id) return;
            OpenAccountTab(id, (string)Db.Scalar(db, "SELECT name FROM account WHERE id=$i;", ("$i", id))!);
        }

        /// <summary>給 <c>--uismoke</c> 用的钩子：帳戶管理視窗建得出來嗎（只讀，不會寫庫；不 Show 就不會卡 modal）。</summary>
        internal int SmokeOpenAccountsDialog()
        {
            if (_db is not { } db) return -1;
            return new AccountsDialog(new LedgerStore(db)).AccountCount;      // 建得出來 = 控件與資料接得上
        }

        /// <summary>給 <c>--uismoke</c> 用的钩子：幣別/匯率視窗建得出來嗎（不 Show，不會真的去打網路）。</summary>
        internal int SmokeOpenCurrencyDialog()
        {
            if (_db is not { } db) return -1;
            var store = new LedgerStore(db);
            var n = new CurrencyDialog(store).CurrencyRowCount;
            if (string.IsNullOrEmpty(store.BaseCurrency)) throw new InvalidOperationException(Loc.T("Db_BaseCurrencyMissing"));
            return n;
        }

        /// <summary>給 <c>--uismoke</c> 用的钩子：換語系（走的是 CultureChanged 那条路：重建分頁也一起跑过）。</summary>
        internal void SmokeSwitchLanguage(string code) => LocalizationManager.Current.ChangeCulture(code);

        /// <summary>給 <c>--uismoke</c> 看的：功能表上透過 <c>{loc:Loc}</c> 綁出去的文字，用來確認換語系真的更新了 UI。</summary>
        internal string SmokeLanguageMenuHeader() => LanguageMenu.Header as string ?? "";

        /// <summary>給 <c>--uismoke</c> 看的：工具 → 語言 選單真的有列出來嗎（不是空的）。</summary>
        internal int SmokeLanguageItemCount() => LanguageMenu.Items.Count;

        /// <summary>給 <c>--uismoke</c>：把「建議分類」建一次（不 Show、不按套用）。商家分組、最底層限制、語系 key 都在這條路上。</summary>
        internal int SmokeOpenSuggestDialog()
        {
            if (_db is not { } db) return -1;
            return new SuggestDialog(new LedgerStore(db)).Rows.Count;
        }

        /// <summary>給 <c>--uismoke</c>：分類報表建起來、展開一個有子類的分類、按欄排序（回傳展開比收合多幾列）。</summary>
        internal int SmokeCategoryReport()
        {
            if (_db is not { } db) return -1;
            return new LedgerTab(db, null, ledgerMode: true).SmokeReportInteract();
        }

        internal string SmokeReport()
        {
            var home = HomeTab.Content as LedgerTab;
            var extra = _accountTabs.Count > 0 && _accountTabs.Values.First().Content is LedgerTab acct
                ? $"，帳戶分頁 {acct.RowCount} 列" : "，沒有資料可開帳戶分頁";
            return $"首頁 {home?.HomeAccountCount ?? -1} 個帳戶{extra}";
        }

        // ------------------------------------------------------------ 功能表

        void NewDb_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Title = Loc.T("Db_NewTitle"),
                Filter = Loc.T("Db_Filter"),
                FileName = "dfinance.db",
                InitialDirectory = CurrentDbFolder(),
            };
            if (dlg.ShowDialog() != true) return;
            if (SamePath(dlg.FileName, _db?.DataSource))
            {
                MessageBox.Show(Loc.T("Db_SameAsCurrent"), Loc.T("Db_NewTitle"));
                return;
            }

            try
            {
                File.Delete(dlg.FileName);                   // SaveFileDialog 已經問過「要覆蓋嗎」
                File.Delete(dlg.FileName + "-wal");          // 舊檔的 WAL 留著會把舊資料補回來
                File.Delete(dlg.FileName + "-shm");
                OpenDatabasePath(dlg.FileName);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, Loc.T("Db_NewTitle")); }
        }

        void OpenDb_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = Loc.T("Db_OpenTitle"),
                Filter = Loc.T("Db_FilterAll"),
                InitialDirectory = CurrentDbFolder(),
            };
            if (dlg.ShowDialog() == true) OpenDatabasePath(dlg.FileName);
        }

        void SaveDbAs_Click(object sender, RoutedEventArgs e)
        {
            if (_db is not { } db) return;
            var dlg = new SaveFileDialog
            {
                Title = Loc.T("Db_SaveAsTitle"),
                Filter = Loc.T("Db_Filter"),
                FileName = Path.GetFileName(db.DataSource),
                InitialDirectory = CurrentDbFolder(),
            };
            if (dlg.ShowDialog() != true || SamePath(dlg.FileName, db.DataSource)) return;

            try
            {
                using var dest = new SqliteConnection($"Data Source={dlg.FileName}");
                db.BackupDatabase(dest);                     // 用備份 API，WAL 裡還沒落盤的也一起過去
                OpenDatabasePath(dlg.FileName);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, Loc.T("Db_SaveAsTitle")); }
        }

        void ClearRecent_Click(object sender, RoutedEventArgs e)
        {
            RecentFiles.Clear();
            RebuildRecentMenu();
        }

        void Exit_Click(object sender, RoutedEventArgs e) => Close();

        void CurrencyManager_Click(object sender, RoutedEventArgs e)
        {
            if (_db is null) return;
            new CurrencyDialog(new LedgerStore(_db)) { Owner = this }.ShowDialog();
            (HomeTab.Content as LedgerTab)?.RefreshIfStale();          // 基準幣一改，淨值與圖的幣別就不同
            foreach (var tab in _accountTabs.Values) (tab.Content as LedgerTab)?.RefreshIfStale();
        }

        void CategoryManager_Click(object sender, RoutedEventArgs e)
        {
            if (_db is null) return;
            new CategoryManagerDialog(new LedgerStore(_db)) { Owner = this }.ShowDialog();
            BuildCategoryTab();
        }

        void About_Click(object sender, RoutedEventArgs e)
            => MessageBox.Show(
                $"dFinance {typeof(MainWindow).Assembly.GetName().Version}\n\n" +
                Loc.F("About_Line", _db?.DataSource ?? Loc.T("Db_NoneOpen"), Db.DataDirectory),
                Loc.T("Menu_About"), MessageBoxButton.OK, MessageBoxImage.Information);

        void BuildCategoryTab()
        {
            if (_categoryTab is not null) Tabs.Items.Remove(_categoryTab);
            _categoryTab = null;
            if (_db is not { } db) return;

            var ledgerTab = new LedgerTab(db, null);
            ledgerTab.ShowCategoryReport();
            ledgerTab.DrillDownRequested += OpenDrillDownTab;
            _categoryTab = new TabItem { Header = Loc.T("Menu_CategoryReport"), Content = ledgerTab };
            Tabs.Items.Insert(Tabs.Items.IndexOf(HomeTab) + 1, _categoryTab);      // 常駐，緊接在首頁之後
        }

        void CategoryReport_Click(object sender, RoutedEventArgs e)
        {
            if (_categoryTab is not null) Tabs.SelectedItem = _categoryTab;
        }

        void RebuildRecentMenu()
        {
            RecentMenu.Items.Clear();
            var paths = RecentFiles.Load().Where(File.Exists).ToList();
            if (paths.Count == 0)
            {
                RecentMenu.Items.Add(new MenuItem { Header = Loc.T("Common_None"), IsEnabled = false });
                return;
            }
            foreach (var path in paths)
            {
                var item = new MenuItem { Header = path };
                item.Click += (_, _) => OpenDatabasePath(path);
                RecentMenu.Items.Add(item);
            }
        }

        string CurrentDbFolder()
        {
            try
            {
                if (_db?.DataSource is { Length: > 0 } src && src != ":memory:")
                    return Path.GetDirectoryName(Path.GetFullPath(src)) ?? Db.DataDirectory;
            }
            catch { /* 路徑壞掉就用預設資料夾 */ }
            return Db.DataDirectory;
        }

        static bool SamePath(string a, string? b)
        {
            if (string.IsNullOrEmpty(b) || b == ":memory:") return false;
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        /// <summary>開檔前的守門：先唯讀確認裡面有 entry 表，才不會把別的 SQLite 檔塞進 dFinance 的資料表。</summary>
        static bool IsDFinanceDatabase(string path)
        {
            try
            {
                using var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
                c.Open();
                return Db.Scalar(c, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='entry';") is long n && n > 0;
            }
            catch { return false; }
        }

        private async void Pick_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = Loc.T("Import_Filter"),
                InitialDirectory = File.Exists(PathBox.Text) ? Path.GetDirectoryName(PathBox.Text) : @"D:\dennys",
            };
            if (dialog.ShowDialog() != true) return;

            PathBox.Text = dialog.FileName;
            await ParseAsync();
        }

        private async void Import_Click(object sender, RoutedEventArgs e)
        {
            var path = PathBox.Text.Trim();
            if (!File.Exists(path)) { ImportLog.Text = Loc.T("Import_NoFile"); return; }
            await ImportAsync([path]);
        }

        private async void ImportFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = Loc.T("Import_FolderTitle") };
            if (dialog.ShowDialog() != true) return;

            var files = Directory.GetFiles(dialog.FolderName, "*.pdf", SearchOption.AllDirectories);
            if (files.Length == 0) { ImportLog.Text = Loc.F("Import_FolderEmpty", dialog.FolderName); return; }
            await ImportAsync(files);
        }

        /// <summary>一個一個匯入；已經入庫的檔案會直接被 sha 擋掉，所以重複按不會記兩次。</summary>
        private async Task ImportAsync(IEnumerable<string> files)
        {
            if (_db is null) return;
            var passwords = UiPasswords();
            ImportButton.IsEnabled = ImportFolderButton.IsEnabled = false;
            try
            {
                var progress = new Progress<string>(m => ImportLog.Text = m);
                // 在 Task.Run 裡面就要把檔案跑完（foreach/.ToList）：之前交回去的是一條還沒執行的 LINQ，
                // 真正解析與寫庫發生在 join 那一刻 —— 就在 UI 執行緒上，整個視窗凍住。
                // 匯入自己開一條連線：整個 app 共用一條 SqliteConnection 不是執行緒安全的，
                // 匯入中使用者點主頁/切分頁才會撞（WAL 讓讀與寫互不擋）。
                var lines = await Task.Run(() =>
                {
                    using var bg = Db.Open();
                    var importer = new StatementImporter(bg);
                    var results = new List<string>();
                    foreach (var f in files)
                    {
                        try { results.Add(importer.ImportPdf(f, passwords, progress).Summary); }
                        catch (Exception ex) { results.Add(Loc.F("Import_FileFailed", Path.GetFileName(f), ex.Message)); }
                    }
                    return results;
                });
                ImportLog.Text = string.Join("\n", lines);
                LedgerStore.Touch();                                // 讓開著的帳本/帳戶分頁切過去時重查
            }
            finally { ImportButton.IsEnabled = ImportFolderButton.IsEnabled = true; }
        }

        /// <summary>UI 一律要親手打密碼（不讀 .env）；逗號分隔當成多組試。</summary>
        string[] UiPasswords()
            => PasswordBox.Password.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();

        private async void Parse_Click(object sender, RoutedEventArgs e) => await ParseAsync();

        private async Task ParseAsync()
        {
            var path = PathBox.Text.Trim();
            if (!File.Exists(path))
            {
                ShowResult(new ParsedStatement { Warnings = { Loc.T("Import_NoFile") } });
                return;
            }

            ParseButton.IsEnabled = false;
            ParseButton.Content = Loc.T("Import_Parsing");        // 圖片式帳單要 OCR，第一份會慢幾十秒（之後讀快取）
            try
            {
                var password = PasswordBox.Password;
                // OCR 一頁十几秒，不回報進度看起來像卡住。
                var progress = new Progress<string>(m => ImportLog.Text = m);
                var statement = await Task.Run(() => StatementParser.Parse(path, password, Db.DataDirectory, progress));
                ShowResult(statement);
                if (ImportLog.Text.StartsWith("OCR")) ImportLog.Text = "";
            }
            catch (Exception ex)
            {
                Grid.ItemsSource = null;
                RawText.Text = "";
                ShowResult(new ParsedStatement
                {
                    Warnings =
                    {
                        ex.GetType().Name.Contains("Password", StringComparison.OrdinalIgnoreCase)
                            ? Loc.F("Import_BadPassword", ex.Message)
                            : ex.Message,
                    }
                });
            }
            finally
            {
                ParseButton.IsEnabled = true;
                ParseButton.Content = Loc.T("Import_Parse");
            }
        }

        private void ShowResult(ParsedStatement s)
        {
            Grid.ItemsSource = s.Transactions;
            RawText.Text = s.RawText;

            SummaryText.Text = Loc.F("Import_SummaryHead",
                s.Issuer, Fmt(s.StatementStartDate), Fmt(s.StatementEndDate), Fmt(s.DueDate),
                Num(s.TotalDue), Num(s.MinimumDue), s.AccountMask);

            var warnings = s.Warnings.ToList();
            if (s.Transactions.Count > 0 && s.TotalDue is not null)
            {
                var sum = s.Transactions.Sum(t => t.Amount);
                warnings.Add(sum == s.TotalDue
                    ? Loc.F("Import_CheckOk", s.Transactions.Count, Num(sum))
                    : Loc.F("Import_CheckDiff", s.Transactions.Count, Num(sum), Num(s.TotalDue), Num(sum - s.TotalDue)));
            }
            WarningText.Text = string.Join("\n", warnings);
        }

        // 金額與日期先照原格式排好再進字串：格式不跟著語系走（AGENTS：錢只經 Money，格式吃 CurrentCulture）。
        static string Num(decimal? v) => v?.ToString("N0") ?? "";
        static string Fmt(DateOnly? d) => d?.ToString("yyyy/MM/dd") ?? "";
    }
}
