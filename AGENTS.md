# AGENTS.md

dFinance 是本機記帳軟體：銀行/帳單 PDF → 解析 → SQLite → WPF 帳本。.NET 8、WPF、PdfPig、Microsoft.Data.Sqlite。
功能細節看 `README.md`，資料庫規則看 `dFinance-db-design.md`，待辦看 `TODO.md`。

## 指令（repo 根目錄）

```powershell
dotnet build dFinance.sln -c Release
dotnet test dFinance.Tests -c Release
dotnet run --project devcheck -c Release -- "<pdf>" - --rows      # dump 文字層（--words 帶座標）
dotnet run --project devcheck -c Release -- - - --rates [proxy]   # 抓 Yahoo 匯率寫進庫（這台機器要給 proxy）
dFinance\bin\Debug\net8.0-windows\dFinance.exe --uismoke "$env:TEMP\uismoke.txt"   # 不用人按的 UI 驗證
```

`devcheck/smoke.ps1` 裡的路徑還是舊的 `D:\dennys\dFinance`，要跑之前先改成實際 repo 路徑。

## 可攜性（硬規則）

- **所有狀態放 `data\`**：`dfinance.db`、`documents\`（留底 PDF）、`backups\`、`recent.json`（最近開啟）。
  不要寫 AppData、登錄檔、雲端或任何 repo 外的固定路徑。
- 資料夾解析：`DFINANCE_DATA` → repo 根 `data\` → exe 旁邊 `data\`（見 `Db.ResolveDataDirectory`）。
- `data\`、`.env`、樣本 PDF 不進版控。
- 密碼只在記憶體裡過一遍：不寫 log、不落檔；UI 一律要人親手打（只有測試/devcheck 讀 `.env`）。

## 專案結構

- `dFinance.Parsing/`：PDF 解析。`StatementParser` 是唯一入口；`Parsers/` 一個版面一個 class，
  `ParserFactory` 依序問誰認得。**類名尾綴 Card / Deposit / Bill 同時被 `StatementImporter` 拿來判斷單據種類。**
- `dFinance.Data/`：`Db`（開庫/遷移/備份、`BaseCurrency`）、`migrations/*.sql`（嵌入 dll，啟動自動套用）、`Money`、
  `StatementImporter`、`LedgerStore`（清單、概況、淨值、每月總資產、未來帳單、匯率、寫入）、`RateSource`（Yahoo 抓匯率）。
- `dFinance/`：WPF。`MainWindow`（功能表＋分頁＋匯入 PDF）、`LedgerTab`（首頁 dashboard 與帳戶分頁共用一支）、
  `AccountsDialog`、`CurrencyDialog`（工具→幣別…：基準幣/幣別字典/匯率）、`Dialogs`、`RecentFiles`。
- `dFinance.Tests/`：xUnit。樣本測試讀 repo 根 `.env` 與 `D:\dennys\statement`，檔案不在就跳過。

## 不能破壞的規則

- 錢只用 `long` 的 `*_minor`（1/100 元），換算只經 `dFinance.Data.Money`；不要用 `double` 算錢。
- 日期存 TEXT `yyyy-MM-dd`；不確定就 NULL，不可塞 `0001-01-01`。
- 報表口徑只有一個：`LedgerStore.Live`（排除 deleted / pending / ignored / duplicate）。UI 不准自己拼 WHERE 算錢。
- 餘額是 derived（期初＋交易）；`account_balance` 只做校驗，不覆蓋 derived 數字。
- 非基準幣帳戶沒有匯率就**不計入淨值**（基準幣 = `settings.base_currency`），不要假設 1:1、也不要湊第三種幣。
- 匯率存在**幣對**上（`exchange_rate(from_ccy,to_ccy,rate_date,source)`，「1 from = ? to」）：改基準幣只是換 where，
  不准把匯率存成「對某個基準幣的單值」；同一天的 `manual` 優先於 `yahoo`（抓的蓋不掉人填的）。
- **一個帳戶一種幣別**：匯入時帳戶鍵值是 `(issuer, mask, type, currency)`，列級幣別不准被單子層吃掉
  （否則 1,250,000 日圓會被當 1,250,000 台幣進淨值）。
- 顯示小數只經 `Money.Format(minor, scale)` 讀 `currency.scale`；UI 不准自己 `ToString("N0")`（美金會被顯示成整數）。
- **parser 不准碰資料庫**：單子上的幣別原文（「美金」）照原文交出去，對代碼是 `StatementImporter.EnsureCurrency`
  用 `currency.aliases` 做的事。
- 遷移檔只能新增（下一支是 `0006_*.sql`），已套用的不要改 —— `migration` 表只記 version/name/applied_at，
  改了內容不會被發現，只會讓既有庫跟新開的庫長得不一樣。
- 帳本 UI 的金額唯讀；要調帳記一筆 `adjustment`。

## UI 現況

- **首頁**（`LedgerTab` 傳 `accountId = null`）＝ dashboard：左邊淨值明細（銀行 → 信用卡 → 其它；
  點一列就開該帳戶分頁）、右上 ScottPlot 每月總資產折線、右下未來帳單（未到期的 entry ＋ 未繳信用卡帳單）。
  首頁沒有帳本清單與篩選列。
- **帳戶分頁**：同一支 `LedgerTab`（傳 `accountId`），有篩選、可編輯格子、來源血統；「帳戶」欄只是 hide
  （留著給未來全部帳戶的檢視用）。
- 換資料庫走 `MainWindow.OpenDatabasePath`：新庫開成功才關舊連線；開檔前用唯讀連線確認是 dFinance 的庫。
- **工具 → 幣別…**（`CurrencyDialog`，純 code-behind 視窗）：基準幣、幣別字典（scale/別名）、匯率表、
  人工填/抓匯率/測試連線/proxy、把幣別不符的列拆到對的帳戶。只有人按按鈕才會連網。
- 文案、註解用繁體中文，跟現有程式一致。

## 注意

- `dotnet test` 若 `NoticeBillTests` 失敗且訊息是 `No data found ...`，是本機缺樣本 PDF（MemberData 是空的），不是程式壞了。
- 動完 UI 或 parser 至少跑 `dotnet build` + `dotnet test` + `--uismoke`。
- ScottPlot.WPF 5.1.59 在 `net8.0-windows` 會有 NU1701（SkiaSharp.Views.WPF 選到 net462 資產）警告；
  實測可正常渲染，不要為此改 TFM。
- ScottPlot 的標題/標籤是中文時要 `plot.Font.Automatic()`，不然預設字型（Segoe UI）沒有 CJK glyph，會變亂碼。
- Yahoo 匯率：**不帶 User-Agent 直接 429**；這台機器沒直連路徑，不填 proxy（`settings.rate_proxy_url`）就會
  `The SSL connection could not be established`。驗證走 `devcheck - - --rates http://127.0.0.1:13128`
  （先 `ssh -fNL 13128:10.191.190.5:3128 v5@dgx1`），不要假設跑得起來。
- `--uismoke` 已經把「幣別…」視窗也建一次（不 Show、不打網路），改那個視窗壞了會被抓到。
