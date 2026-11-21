using System.IO;
using System.Windows;
using dFinance.Data;
using dFinance.Data.Localization;

namespace dFinance
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// <c>dFinance.exe --uismoke 結果檔</c>：把主頁與一個帳戶分頁開起來再關掉，把結果寫進檔案。
        /// 給腳本驗 UI 用（XAML 打不開、控件接錯、裝載時炸掉都會被抓到），不需要人按。
        /// </summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            // 語系單例進應用資源：XAML 綁字串就寫 {Binding [Key], Source={StaticResource Loc}}。
            // 不用 {x:Static} 是因為它只在載入時取一次值，換語系不會更新；綁索引子 + INPC 才會動。
            Resources["Loc"] = LocalizationManager.Current;
            base.OnStartup(e);

            // 未處理例外在 WPF 就是整個程式結束：剛改的格子、改一半的寫入跟著丢。
            // 最常見來源是兩條連線抢寫（批次匯入在背景另一條連線）→ "database is locked"。
            // --uismoke 不接：那個是要讓例外真的爆出去給腳本看，吞了就是假 OK。
            if (Array.IndexOf(e.Args, "--uismoke") < 0)
                DispatcherUnhandledException += (_, args) =>
                {
                    MessageBox.Show(args.Exception.Message, Loc.T("Common_AppError"));
                    args.Handled = true;
                };

            int i = Array.IndexOf(e.Args, "--uismoke");
            if (i < 0) return;

            var report = i + 1 < e.Args.Length ? e.Args[i + 1] : "uicheck.txt";
            try
            {
                var w = new MainWindow();
                w.Show();
                w.SmokeOpenFirstAccountTab();
                var before = w.SmokeLanguageMenuHeader();
                var swapped = new List<string>();
                foreach (var (code, native) in LocalizationManager.Languages)
                {
                    w.SmokeSwitchLanguage(code);            // 真換一次語系：綁定沒更新就是白做（不是只驗證沒炸）
                    var header = w.SmokeLanguageMenuHeader();
                    if (header == native) throw new InvalidOperationException($"{code} 的選單文字還是 {header}");
                    if (w.SmokeLanguageItemCount() != LocalizationManager.Languages.Count)
                        throw new InvalidOperationException($"{code}：語言選單只有 {w.SmokeLanguageItemCount()} 項");
                    if (LocalizationManager.Current.UiCulture.Name != code)
                        throw new InvalidOperationException($"{code}：UiCulture 是 {LocalizationManager.Current.UiCulture.Name}");
                    swapped.Add($"{code}={header}");
                }
                if (LocalizationManager.MissingKeys.Count > 0)
                    throw new InvalidOperationException("找不到這些語系 key：" + string.Join(", ", LocalizationManager.MissingKeys.Keys));
                w.SmokeSwitchLanguage(LocalizationManager.DefaultCulture);
                var line = "OK  " + w.SmokeReport() + $"，帳戶管理開得來（{w.SmokeOpenAccountsDialog()} 個帳戶）"
                         + $"，幣別/匯率開得來（{w.SmokeOpenCurrencyDialog()} 種幣別）"
                         + $"，建議分類開得來（{w.SmokeOpenSuggestDialog()} 個商家）"
                         + $"，分類報表展開/排序得動（{w.SmokeCategoryReport()}）"
                         + $"，語系換得過去（{before} → {string.Join(" / ", swapped)}）";
                w.Close();
                File.WriteAllText(report, line + "  data=" + Db.DataDirectory);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(report, "FAIL " + ex);
                Shutdown(1);
            }
        }
    }
}
