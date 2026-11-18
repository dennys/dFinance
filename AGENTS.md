# AGENTS.md

dFinance 是本機記帳軟體：銀行/帳單 PDF → 解析 → SQLite → WPF 帳本。.NET 8、WPF、PdfPig、Microsoft.Data.Sqlite。
功能細節看 `README.md`，資料庫規則看 `dFinance-db-design.md`，待辦看 `TODO.md`。

## 指令（repo 根目錄）

```powershell
dotnet build dFinance.sln -c Release
dotnet test dFinance.Tests -c Release
dotnet run --project devcheck -c Release -- "<pdf>" - --rows      # dump 文字層（--words 帶座標）
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
- `dFinance.Data/`：`Db`（開庫/遷移/備份）、`migrations/*.sql`（嵌入 dll，啟動自動套用）、`Money`、
  `StatementImporter`、`LedgerStore`（清單、概況、淨值、每月總資產、未來帳單、寫入）。
- `dFinance/`：WPF。`MainWindow`（功能表＋分頁＋匯入 PDF）、`LedgerTab`（首頁 dashboard 與帳戶分頁共用一支）、
  `AccountsDialog`、`Dialogs`、`RecentFiles`。
- `dFinance.Tests/`：xUnit。樣本測試讀 repo 根 `.env` 與 `D:\dennys\statement`，檔案不在就跳過。

## 不能破壞的規則

- 錢只用 `long` 的 `*_minor`（1/100 元），換算只經 `dFinance.Data.Money`；不要用 `double` 算錢。
- 日期存 TEXT `yyyy-MM-dd`；不確定就 NULL，不可塞 `0001-01-01`。
- 報表口徑只有一個：`LedgerStore.Live`（排除 deleted / pending / ignored / duplicate）。UI 不准自己拼 WHERE 算錢。
- 餘額是 derived（期初＋交易）；`account_balance` 只做校驗，不覆蓋 derived 數字。
- 非台幣帳戶沒有匯率就**不計入淨值**，不要假設 1:1。
- 遷移檔只能新增（`0004_*.sql`…），已套用的不要改。
- 帳本 UI 的金額唯讀；要調帳記一筆 `adjustment`。

## UI 現況

- **首頁**（`LedgerTab` 傳 `accountId = null`）＝ dashboard：左邊淨值明細（銀行 → 信用卡 → 其它；
  點一列就開該帳戶分頁）、右上 ScottPlot 每月總資產折線、右下未來帳單（未到期的 entry ＋ 未繳信用卡帳單）。
  首頁沒有帳本清單與篩選列。
- **帳戶分頁**：同一支 `LedgerTab`（傳 `accountId`），有篩選、可編輯格子、來源血統；「帳戶」欄只是 hide
  （留著給未來全部帳戶的檢視用）。
- 換資料庫走 `MainWindow.OpenDatabasePath`：新庫開成功才關舊連線；開檔前用唯讀連線確認是 dFinance 的庫。
- 文案、註解用繁體中文，跟現有程式一致。

## 注意

- `dotnet test` 若 `NoticeBillTests` 失敗且訊息是 `No data found ...`，是本機缺樣本 PDF（MemberData 是空的），不是程式壞了。
- 動完 UI 或 parser 至少跑 `dotnet build` + `dotnet test` + `--uismoke`。
- ScottPlot.WPF 5.1.59 在 `net8.0-windows` 會有 NU1701（SkiaSharp.Views.WPF 選到 net462 資產）警告；
  實測可正常渲染，不要為此改 TFM。
- ScottPlot 的標題/標籤是中文時要 `plot.Font.Automatic()`，不然預設字型（Segoe UI）沒有 CJK glyph，會變亂碼。
