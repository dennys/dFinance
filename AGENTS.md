# AGENTS.md

dFinance 是本機記帳軟體：銀行/帳單 PDF → 解析 → SQLite → WPF 帳本。.NET 8、WPF、PdfPig、Microsoft.Data.Sqlite。
功能細節看 `README.md`，資料庫規則看 `dFinance-db-design.md`，待辦看 `TODO.md`。

## 指令（repo 根目錄）

```powershell
dotnet build dFinance.sln -c Release
dotnet test dFinance.Tests -c Release
dotnet run --project devcheck -c Release -- "<pdf>" - --rows      # dump 文字層（--words 帶座標）
dotnet run --project devcheck -c Release -- - - --rates [proxy]   # 抓 Yahoo 匯率寫進庫（這台機器要給 proxy）
dotnet run --project devcheck -c Release -- - - --autocat   # 假分類：把沒標/標在父類的列輪流塞到最底層（測報表用）
dotnet run --project devcheck -c Release -- - - --fakein    # 假收入 24 筆（報表 income 那一側）；砍：DELETE FROM entry WHERE merchant LIKE '%（假）'
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
- `dFinance.Data/`：`Db`（開庫/遷移/備份、`BaseCurrency`、`UiLanguage`）、`migrations/*.sql`（嵌入 dll，啟動自動套用）、`Money`、
  `StatementImporter`、`LedgerStore`（清單、概況、淨值、每月總資產、未來帳單、匯率、寫入）、`RateSource`（Yahoo 抓匯率）、
  `Localization/`（`Strings*.resx` 四種語系＋`LocalizationManager`/`Loc`，介面文字全在這裡）。
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
- **entry 只能標最底層分類**（下面沒有子分類的那個）：標在父類會讓分類報表的父列小計 ≠ 子列相加。
  標分類的下拉一律用 `Categories(kind, leavesOnly: true)`，寫入前 `LedgerStore.EnsureLeaf` 會擋；
  篩選下拉要含父類（筛「交通」要連子類一起筛到），所以用不帶參數的那一支。
- **parser 不准碰資料庫，也不准碰語系檔**：單子上的幣別原文（「美金」）照原文交出去，對代碼是 `StatementImporter.EnsureCurrency`
  用 `currency.aliases` 做的事；parser 的 warning 照原文進匯入 log，不進 resx。
  例外只允許「版面判定」不是對映（繳費單只印「元」、中信幣別在圖片標題）；**不准在 parser 裡把原文對成代碼**。
  `ParsedTransaction` **沒有 `Kind`**（2026-11-20 砍：它只是正負號的複製，而且卡帳/存款單語意相反），方向只看 `Amount` 正負號。
- **語系只換 UI，不換錢與日期**：`LocalizationManager.ChangeCulture` 只動 `CurrentUICulture`，
  `CurrentCulture` 不准一起改（碰上小數點是逗號的語系會把金額解錯一百倍）；帶參數的字串走 `Loc.F("Key", a1, a2)`
  跟 resx 裡的 `{0}`，不准在程式裡拼句子（語序會破）。細節見 `LOCALIZATION.md`。
- 遷移檔只能新增（下一支是 `0006_*.sql`），已套用的不要改 —— `migration` 表只記 version/name/applied_at，
  改了內容不會被發現，只會讓既有庫跟新開的庫長得不一樣。
- 帳本 UI 的金額唯讀；要調帳記一筆 `adjustment`。
- **去重的比對範圍由「有沒有可靠身份」決定**（`StatementImporter.FindExisting` 的 `crossFile`）：
  卡號/帳號/繳費戶號進得了哈希才准跳檔案比；身份為空就只在同一份檔案內比。
  跳檔案判到重複是**不插入**（不是標記），判錯就是悄悄少一筆；「疑似重複」（`LedgerStore.DuplicateGroupKey`）才是只做標記，
  而且只算信用卡。繳費單的戶號由 `UtilityBillParser.Identity` 抽（只能抽**跨期不變**的：通知單號/單據號碼/列印日期不准用）。
- **重匯時 `edited_at` 有值的列只准更新 `source_raw`/`source_page`**，分類/商家/金額/狀態一個都不動
  （要單欄位重覆蓋得先做欄位級編輯追蹤）。這次沒對到的舊列標 `orphaned_at`，**不准刪**。
- **同帳期取代與孤兒標記只對信用卡做**（靠 `credit_card_bill` 的 `UNIQUE(card_id, period_end)`）；存款單/繳費單不做取代。
- **卡帳對帳用的是「扣掉繳款（轉帳）後的消費合計 ＝ 本期應繳」**（`StatementParser.WarnIfDueMismatch`）：
  對不上就是漏列，要警告，不准為了湊數字改解析結果。現金回饋不是繳款（它直接抵應繳，要留在消費合計裡）。
- **解析是座標＋關鍵字式，改版會靜靜漏列**：解析到 0 筆一定要進 `Warnings`，不准把警告拿掉讓測試變綠。
- **單據種類決定錢的方向**（`StatementImporter`）：存款單（`StatementKind.Deposit`）正數 = `income`、負數 = `expense`；
  卡帳才是看正負號（負 = `refund`、繳款 = `transfer`）。`source_line_hash` 一律照**單子上的金額**算，
  不准照存進 DB 的那個（`0009` 改過 kind/正負號，照 DB 算會讓重匯把同一筆插兩份）。
- **人工輸入的幣別必須等於帳戶幣別**：`InsertManual` 會挡（轉帳的兩個帳戶也要同幣別）。舊庫混進去的外幣列
  由 `SplitForeignCurrencyEntries()` 搬走：在**一條交易**裡跑，目標帳戶（同幣別、未歸檔）已存在就沿用，不准插同名。
- **概況（`Totals`）、淨值、分類報表、每月折線是同一把尺**：先照幣別加總、折算只經 `Money.Convert`；
  完全沒匯率的幣別不計入金額、只報筆數。沒有消費日的列在淨值與折線都要算（当成期初，同一支 `Undated`）。
- **分類報表「進不進表」看有没有任何一個月有數**（`PivotTree`），不是看整年相加＝0：退款抵掉的月份被藏起来，
  小計加起来就會跟最下面的總計列對不上（`PivotTreeTests` 鎖住）。
- **依帳期彙總**（`LedgerStore.BillPeriods`）歸期只靠 `entry.bill_id`，**不准改用日期區間**（跨期消費與 `txn_date` 為 NULL 的列會跑掉）；
  消費合計排除 `transfer`（繳款不是消費，加進去會 double count）、退款照負的直接加；**列照單上幣別**（這張表要跟紙本對帳）、
  只有合計折算基準幣，缺匯率的期不進合計但要計數；`bill_id IS NULL` 的卡帳列以「（無帳期）」呈現，不准静靜吃掉。
- **UI 對帳本的寫入一律走 `LedgerTab.Write()`**：批次匯入在背景另一條連線，撞鎖的例外沒接就是整個程式挂掉；
  失敗要講訊息並重查，不准留一個看起來改好了的格子。

## UI 現況

- **首頁**（`LedgerTab` 傳 `accountId = null`）＝ dashboard：左邊淨值明細（銀行 → 信用卡 → 其它；
  點一列就開該帳戶分頁）、右上 ScottPlot 每月總資產折線、右下未來帳單（未到期的 entry ＋ 未繳信用卡帳單）。
  首頁沒有帳本清單與篩選列。
- **帳戶分頁**：同一支 `LedgerTab`（傳 `accountId`），有篩選、可編輯格子、來源血統；「帳戶」欄只是 hide
  （留著給未來全部帳戶的檢視用）。
- 換資料庫走 `MainWindow.OpenDatabasePath`：新庫開成功才關舊連線；開檔前用唯讀連線確認是 dFinance 的庫。
- **工具 → 幣別…**（`CurrencyDialog`，純 code-behind 視窗）：基準幣、幣別字典（scale/別名）、匯率表、
  人工填/抓匯率/測試連線/proxy、把幣別不符的列拆到對的帳戶。只有人按按鈕才會連網。
- **工具 → 語言…**：四種語言（繁中/English/简体中文/日本語）點選立刻換。XAML 綁的是
  `{Binding [Key], Source={StaticResource Loc}}`，code 用 `Loc.T`/`Loc.F`；規則、坑與驗收方式在 `LOCALIZATION.md`。
- 文案、註解用繁體中文，跟現有程式一致；給使用者看的文字一律走 `dFinance.Data/Localization/Strings*.resx`
  （XAML `{Binding [Key], Source={StaticResource Loc}}`、code 用 `Loc.T`/`Loc.F`），**不准用 `{x:Static}` 取字串**，
  也別在 code 裡硬編中文 —— 那等於又寫死一個語系。規則與坑寫在 `LOCALIZATION.md`。
- 換語系不重開程式：XAML 宣告的文字靠綁定自己換，**code 裡一次指派出去的（DataGrid 欄頭、下拉、圖表標題）不會**，
  靠 `MainWindow.RebuildForCulture` 重建分頁；新增這類控件時要嘛走綁定，要嘛掛進重建路徑。

## 注意

- OCR（圖片式帳單，見 README「OCR」）：只在文字層沒中文時跑。模型放 `data\ocr_models\`
  （或 `DFINANCE_OCR_MODELS`），沒放就只警告 —— 測試裡的 OCR 那幾組會自動跳過（`Samples.OcrReady`）。
  **鐵則：金額/日期只取文字層，OCR 只補標籤、表頭、商家。** 找段落要看**標題**（中信存款單的
  「一般帳戶資產交易明細」在第 3 頁中間），不能靠頁數；認不出幣別的段落跳過並警告，不猜。
- `PDFtoImage` 釘在 5.2.0、SkiaSharp 釘在 3.119.x：5.3 起 `PDFtoImage` 會把 SkiaSharp 抬到 4.150.x → ScottPlot 會炸。
  動 `dFinance.Parsing.csproj` 前先看 README 的「OCR」那一節。
- 改 parser 的解析規則要把 `StatementParser.Version` +1（落庫寫進 `document.parser_ver`，重匯/舊版標記靠它）。
- 除錯圖片式單子：`devcheck "<pdf>" - --ocr` 會 dump OCR 讀到的字（前面標 `~`）與合併後的列。

- `dotnet test` 若 `NoticeBillTests` 失敗且訊息是 `No data found ...`，是本機缺樣本 PDF（MemberData 是空的），不是程式壞了。
- 動完 UI 或 parser 至少跑 `dotnet build` + `dotnet test` + `--uismoke`。
- ScottPlot.WPF 5.1.59 在 `net8.0-windows` 會有 NU1701（SkiaSharp.Views.WPF 選到 net462 資產）警告；
  實測可正常渲染，不要為此改 TFM。
- ScottPlot 的標題/標籤是中文時要 `plot.Font.Automatic()`，不然預設字型（Segoe UI）沒有 CJK glyph，會變亂碼。
- Yahoo 匯率：**不帶 User-Agent 直接 429**；這台機器沒直連路徑，不填 proxy（`settings.rate_proxy_url`）就會
  `The SSL connection could not be established`。驗證走 `devcheck - - --rates http://127.0.0.1:13128`
  （先 `ssh -fNL 13128:10.191.190.5:3128 v5@dgx1`），不要假設跑得起來。
- `--uismoke` 已經把「幣別…」視窗也建一次（不 Show、不打網路），改那個視窗壞了會被抓到。
- `--uismoke` 也會建「建議分類」視窗並跑一次分類報表的展開/排序（`SmokeOpenSuggestDialog`、`SmokeCategoryReport`），壞了會被抓到。
- **分類帳報表**＝分類樹（列）× 月（欄）：樹、小計、排序、展開的規則都在 `dFinance.Data/PivotTree`（純函式，`PivotTreeTests` 鎖住），
  `LedgerTab` 只負責欄位、點擊展開、drill down；資料口徑只有一個：`LedgerStore.CategoryPivot`。欄位排序是 UI 自己排的
  （`e.Handled = true`），因為內建排序不會讓父列帶著子列走。
  版面固定三塊：支出塊 → 收入塊（兩塊的先後不受排序影響）→ 最後一列**總計＝收−支**（負的月份就是虧）；
  收−支的符號在 `CategoryPivot` 裡照交易 `kind` 算（退款是負的支出，負負得正），不靠分類標記。非基準幣按現有匯率折算，完全沒匯率的才跳過並警示。
