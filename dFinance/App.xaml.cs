using System.IO;
using System.Windows;
using dFinance.Data;

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
            base.OnStartup(e);
            int i = Array.IndexOf(e.Args, "--uismoke");
            if (i < 0) return;

            var report = i + 1 < e.Args.Length ? e.Args[i + 1] : "uicheck.txt";
            try
            {
                var w = new MainWindow();
                w.Show();
                w.SmokeOpenFirstAccountTab();
                var line = "OK  " + w.SmokeReport() + $"，帳戶管理開得來（{w.SmokeOpenAccountsDialog()} 個帳戶）"
                         + $"，幣別/匯率開得來（{w.SmokeOpenCurrencyDialog()} 種幣別）";
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
