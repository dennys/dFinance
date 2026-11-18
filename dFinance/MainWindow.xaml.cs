using System.IO;
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
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.W && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) CloseSelectedAccountTab();
            };
        }

        /// <summary>啟動就開庫、跑遷移、備份一份（留最近 5 份），並把主頁接上庫。</summary>
        void OpenDatabase()
        {
            OpenDatabasePath(Db.DatabasePath);
            if (_db is null) return;
            try { Db.Backup(_db); }
            catch (Exception ex) { MessageBox.Show($"備份失敗（{ex.Message}），帳本照常開啟。", "備份"); }
        }

        /// <summary>換一個資料庫檔（開檔/新建/另存都走這裡）。舊的一律等新庫開成功才關，開失敗就維持原狀。</summary>
        void OpenDatabasePath(string path)
        {
            if (SamePath(path, _db?.DataSource) && _db is not null) return;      // 已經開著同一個檔
            if (File.Exists(path) && !IsDFinanceDatabase(path))
            {
                MessageBox.Show("這個檔不是 dFinance 資料庫。", "開啟資料庫");
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
                HomeTab.Content = NewLedger(null, "首頁");
                BuildCategoryTab();
                Tabs.SelectedItem = HomeTab;
                previous?.Dispose();

                DbPathText.Text = $"資料庫：{Path.GetFullPath(path)}    留底 PDF：{Db.DocumentsDirectory}";
                Title = $"dFinance — {Path.GetFileName(path)}";
                RecentFiles.Add(path);
                RebuildRecentMenu();
            }
            catch (Exception ex)
            {
                if (_db is null)
                {
                    HomeTab.Content = new TextBlock { Text = "資料庫開不起來：\n" + ex.Message, Margin = new Thickness(8) };
                    DbPathText.Text = "資料庫開不起來，匯入按鈕暂時不能用。";
                    ImportLog.Text = "開庫失敗：" + ex.Message;
                }
                else MessageBox.Show($"開不起來：{ex.Message}", "開啟資料庫");
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
                ToolTip = "關閉這個分頁",
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
                ToolTip = "關閉這個分頁（Ctrl+W）",
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
                Title = "新資料庫",
                Filter = "dFinance 資料庫|*.db",
                FileName = "dfinance.db",
                InitialDirectory = CurrentDbFolder(),
            };
            if (dlg.ShowDialog() != true) return;
            if (SamePath(dlg.FileName, _db?.DataSource))
            {
                MessageBox.Show("這是目前開著的資料庫，不用新建。", "新資料庫");
                return;
            }

            try
            {
                File.Delete(dlg.FileName);                   // SaveFileDialog 已經問過「要覆蓋嗎」
                File.Delete(dlg.FileName + "-wal");          // 舊檔的 WAL 留著會把舊資料補回來
                File.Delete(dlg.FileName + "-shm");
                OpenDatabasePath(dlg.FileName);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "新資料庫"); }
        }

        void OpenDb_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "開啟資料庫",
                Filter = "dFinance 資料庫|*.db|所有檔案|*.*",
                InitialDirectory = CurrentDbFolder(),
            };
            if (dlg.ShowDialog() == true) OpenDatabasePath(dlg.FileName);
        }

        void SaveDbAs_Click(object sender, RoutedEventArgs e)
        {
            if (_db is not { } db) return;
            var dlg = new SaveFileDialog
            {
                Title = "另存資料庫",
                Filter = "dFinance 資料庫|*.db",
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
            catch (Exception ex) { MessageBox.Show(ex.Message, "另存資料庫"); }
        }

        void ClearRecent_Click(object sender, RoutedEventArgs e)
        {
            RecentFiles.Clear();
            RebuildRecentMenu();
        }

        void Exit_Click(object sender, RoutedEventArgs e) => Close();

        void CategoryManager_Click(object sender, RoutedEventArgs e)
        {
            if (_db is null) return;
            new CategoryManagerDialog(new LedgerStore(_db)) { Owner = this }.ShowDialog();
            BuildCategoryTab();
        }

        void About_Click(object sender, RoutedEventArgs e)
            => MessageBox.Show(
                $"dFinance {typeof(MainWindow).Assembly.GetName().Version}\n\n" +
                $"資料庫：{_db?.DataSource ?? "（沒有開啟）"}\n資料夾：{Db.DataDirectory}\n\n" +
                "本機記帳，PDF 留底，資料不連網。",
                "關於 dFinance", MessageBoxButton.OK, MessageBoxImage.Information);

        void BuildCategoryTab()
        {
            if (_categoryTab is not null) Tabs.Items.Remove(_categoryTab);
            _categoryTab = null;
            if (_db is not { } db) return;

            var ledgerTab = new LedgerTab(db, null);
            ledgerTab.ShowCategoryReport();
            ledgerTab.DrillDownRequested += OpenDrillDownTab;
            _categoryTab = new TabItem { Header = "分類帳", Content = ledgerTab };
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
                RecentMenu.Items.Add(new MenuItem { Header = "（沒有）", IsEnabled = false });
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
                Filter = "PDF 帳單|*.pdf",
                InitialDirectory = File.Exists(PathBox.Text) ? Path.GetDirectoryName(PathBox.Text) : @"D:\dennys",
            };
            if (dialog.ShowDialog() != true) return;

            PathBox.Text = dialog.FileName;
            await ParseAsync();
        }

        private async void Import_Click(object sender, RoutedEventArgs e)
        {
            var path = PathBox.Text.Trim();
            if (!File.Exists(path)) { ImportLog.Text = "請先選一個存在的 PDF 檔案。"; return; }
            await ImportAsync([path]);
        }

        private async void ImportFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "選一個放帳單 PDF 的資料夾" };
            if (dialog.ShowDialog() != true) return;

            var files = Directory.GetFiles(dialog.FolderName, "*.pdf", SearchOption.AllDirectories);
            if (files.Length == 0) { ImportLog.Text = $"{dialog.FolderName} 裡找不到 PDF。"; return; }
            await ImportAsync(files);
        }

        /// <summary>一個一個匯入；已經入庫的檔案會直接被 sha 擋掉，所以重複按不會記兩次。</summary>
        private async Task ImportAsync(IEnumerable<string> files)
        {
            if (_db is not { } db) return;
            var passwords = UiPasswords();
            ImportButton.IsEnabled = ImportFolderButton.IsEnabled = false;
            try
            {
                var lines = await Task.Run(() => files.Select(f =>
                {
                    try { return new StatementImporter(db).ImportPdf(f, passwords).Summary; }
                    catch (Exception ex) { return $"{Path.GetFileName(f)}：失敗 — {ex.Message}"; }
                }));
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
                ShowResult(new ParsedStatement { Warnings = { "請先選一個存在的 PDF 檔案。" } });
                return;
            }

            ParseButton.IsEnabled = false;
            try
            {
                var password = PasswordBox.Password;
                var statement = await Task.Run(() => StatementParser.Parse(path, password));
                ShowResult(statement);
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
                            ? $"密碼錯誤或這個 PDF 開不了：{ex.Message}"
                            : ex.Message,
                    }
                });
            }
            finally
            {
                ParseButton.IsEnabled = true;
            }
        }

        private void ShowResult(ParsedStatement s)
        {
            Grid.ItemsSource = s.Transactions;
            RawText.Text = s.RawText;

            SummaryText.Text =
                $"發行方：{s.Issuer}    帳期：{s.StatementStartDate:yyyy/MM/dd} ~ {s.StatementEndDate:yyyy/MM/dd}    " +
                $"繳款截止日：{s.DueDate:yyyy/MM/dd}    本期應繳：{s.TotalDue:N0}    最低應繳：{s.MinimumDue:N0}    帳號：{s.AccountMask}";

            var warnings = s.Warnings.ToList();
            if (s.Transactions.Count > 0 && s.TotalDue is not null)
            {
                var sum = s.Transactions.Sum(t => t.Amount);
                warnings.Add(sum == s.TotalDue
                    ? $"明細 {s.Transactions.Count} 筆，合計 {sum:N0} — 與本期應繳相符"
                    : $"明細 {s.Transactions.Count} 筆，合計 {sum:N0} — 和本期應繳 {s.TotalDue:N0} 差 {sum - s.TotalDue:N0}，可能漏解析");
            }
            WarningText.Text = string.Join("\n", warnings);
        }
    }
}
