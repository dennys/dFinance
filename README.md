# dFinance

本地端記帳軟體：讀銀行/帳單 PDF → 拆成記帳資料 → 存進本地 SQLite，並留底原始單據。

`.NET 8` / WPF / PdfPig / Microsoft.Data.Sqlite / RapidOcrNet+PDFtoImage（圖片式帳單的 OCR，見下面「OCR」）。沒有雲端服務，不需要連線，**portable**（整個 `data\` 資料夾拷走就是移機）。

## 現況

```
dotnet build -c Release    # 0 錯誤
dotnet test                # 83 個測試
```

已可用的三條路徑：

1. **解析**：UI 讀檔 → 表格（已用 UI 自動化驗過：開檔 → 填密碼 → Parse → 欄位與交易列都正確顯示）。
2. **匯入**：UI 按「匯入資料庫」/「批次匯入資料夾…」，或 CLI `devcheck <pdf或資料夾> - --import`。
   實測 24 份樣本：24 份留底、185 筆交易入庫、9 份信用卡帳單（其中 6 份「明細合計 == 本期應繳」在 SQL 直接對得上）；
   同一份檔案重跑會被 sha256 擋掉，不會記兩次。
3. **首頁**（啟動就停在這裡）：左邊淨值明細（銀行 → 信用卡 → 其它分組，點一列就開該帳戶分頁），
   右上是每月總資產折線（ScottPlot），右下是未來帳單（未到期的 entry 與未繳卡費）；
   上面有本月概況（收支/淨額/淨值/卡未繳/未折算），底部是警示列（孤兒列/疑似重複/餘額不符，點了就篩出來）。
4. **帳戶分頁**：按首頁的列（或「開帳戶分頁」）就開一個分頁；有篩選（日期/分類/狀態/商家）、
   可編輯格子（分類/商家/備註/消費日）、右側來源血統（含「開啟留底 PDF」）。
   同一帳戶只開一個、開著不重查，切來切去不重載（Ctrl+W 關）。
5. **帳戶管理**（主頁按「帳戶…」）：新增/編輯/歸檔帳戶（現金、錢包、外幣帳戶都只能從這裡建）、
   期初餘額與期初日期、信用卡的額度與自動扣繳銀行、還有**餘額盤點**（人當場數的錢）。
   盤點寫進 `account_balance(source='manual')` 就是「餘額不符」那條校驗的輸入，對不上會問你要不要記一筆 `adjustment` 補齊。
6. **幣別與匯率**（功能表「工具 → 幣別…」）：選基準幣（淨值與每月總資產都用它表達）、維護幣別字典
   （顯示小數位與匯入用的別名「美金|美元」）、看匯率表（幣對/報價日期/來源/過期 ⚠）、
   **人工填**或按「抓匯率」從 Yahoo 抓（要連網，公司網先填 proxy），另有「測試連線」跟「把幣別不符的列拆到對的帳戶」。
7. **語言**（功能表「工具 → 語言…」）：繁體中文 / English / 简体中文 / 日本語，**選了立刻換，不用重開程式**。
   文字在 `dFinance.Data/Localization/Strings*.resx`（四語齊全、有測試在擋），選好的語言存進
   `settings.ui_language`（跟資料庫走，不寫 AppData）。開發者要看的細節：[LOCALIZATION.md](LOCALIZATION.md)。

資料落在 `data\`（`dfinance.db` + `documents\` 留底 + `backups\` 自動留 5 份）。
位置解析順序：環境變數 `DFINANCE_DATA` → repo 根 `data\` → exe 旁邊 `data\`（發佈後就是自帶資料）。已 gitignore。
功能表「檔案」可以換資料庫（新資料庫/開啟/另存/最近開啟）；最近清單是 `data\recent.json`（一樣可攜，不寫 AppData）。

## 支援到哪

| 銀行 | 單子 | 狀態 | 已知問題 |
|---|---|---|---|
| 聯邦 | 信用卡帳單 | ✅ | |
| 聯邦 | 數位存款電子對帳單 | ✅ | 只有收入/支出欄，餘額沒建模 |
| 王道 | 存款對帳單 | ✅ | 只解析活期明細，定期存單沒算進總資產 |
| 台新 | 信用卡帳單 | ✅ | 繳款/退款列讓合計不等應繳金額（正常，會警告） |
| 台北富邦 | 信用卡帳單 | ✅ | 同上 |
| 兆豐 | 信用卡帳單 | ✅ | 同上；帳號末四碼被遮時拿不到卡號 |
| 兆豐 | 存款對帳單（`MegaAttachmentFile *.pdf`） | ✅ | 一份含多個帳號與多個幣別（已逐列、逐幣別歸戶）；餘額欄未建模 |
| 永豐 | 信用卡帳單 | ✅ | 明細只有 MM/DD，年份靠繳款截止日推 |
| 永豐 | 電子綜合對帳單 | ✅ | 只解析存款明細那段；帳戶概要、別家銀行信用卡應繳列未建模 |
| 國泰世華 | 信用卡帳單 | 🟡 部分 | 手上樣本是已全額繳清的月份，只有繳款一筆可驗；需要一份有消費明細的 |
| 中國信託 | 信用卡帳單 | ✅ （要 OCR） | 消費列已驗（11507：900+99=999、卡號 1648）；繳清月份那份只有繳款一筆 |
| 中國信託 | 存款對帳單 | ✅ （要 OCR） | 交易明細在中间那一頁（標題「一般帳戶資產交易明細」），**不能靠頁數找**；上期結餘+Σ交易 會校驗期末餘額 |
| 匯豐 | 信用卡帳單 | ✅ （要 OCR） | 中文是圖片；金額/日期仍以文字層為準，OCR 只補標籤與商家 |
| 渣打 | 信用卡帳單 | ⛔ 不做 | 文字層是亂碼（字型沒 ToUnicode），已決定放棄 |

非銀行的繳費單也能拆成一筆記帳用的交易（電費、水費、天然氣、電信、eTag），見 `UtilityBillParser`。
水費那份本期 0 元（退款抵掉），會警告「記帳上可忽略」。

⛔ 只剩渣打那兩份不做（文字層是亂碼）。中信/匯豐都要 OCR，模型沒放好就只會警告。

## OCR（圖片式帳單）

中信/匯豐這類單子的中文是被畫成一格一格的小圖，文字層讀不到 → 用 OCR。

- 引擎：`RapidOcrNet`（PP-OCRv5 ONNX，CPU、**完全離線**）。流程在 `dFinance.Parsing/Ocr/OcrReader.cs`：
  整頁柵格化（150 dpi，實測與 300 準度相同）→ OCR → 把讀到的字換算回 PDF 座標，
  與文字層的列**合併**後才送給 parser（`PageData.WithOcr`）。所以 parser 那边不用分兩種來源。
- **錢不給 OCR**：金額/日期只取文字層的（OCR 對數字實測 97% 準，錢不能賭在上面）；
  OCR 只負責標籤（本期應繳總額、繳款截止日…）、表頭與商家。對不上時會警告，由人比對原單。
- 模型**不進版控也不抓網**（app 不会自己下載）。要自帶 OCR 就把這四個檔放 `data\ocr_models\`：

  | 檔 | 大小 | SHA256 |
  |---|---|---|
  | `ch_PP-OCRv5_rec_mobile.onnx` | 16.6 MB | `5825fc7ebf84ae7a412be049820b4d86d77620f204a041697b0494669b1742c5` |
  | `ppocrv5_dict.txt` | 74 KB | 辨識器的字典型，要與 rec 同語言，配錯會吐亂碼 |
  | `ch_PP-OCRv5_mobile_det.onnx` | 4.8 MB | 偵測器（RapidOcrNet 的 nupkg 內附，直接拷過去就行） |
  | `ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx` | 1.0 MB | 方向分類器（同上） |

  前兩個的來源：`https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/…`（會 302 到 CDN，要跟著轉址）。
  放的位置也可以是環境變數 `DFINANCE_OCR_MODELS`。**沒放就整個 OCR 跳過**（不 crash），
  解析結果會警告「需要 OCR：把 … 放進 data\ocr_models\」。
- 快取：OCR 結果存 `data\ocr\{檔案 sha256}-p{頁}-ocr{版}.json`（在 `data\` → 移機一起帶走）。
  一份四頁的單子第一遍約 40–60 秒（CPU），之後讀快取就是 1 秒。
  UI 會報進度：「OCR 辨識中… 第 x/y 頁」，批次匯入時還會報目前正在跑哪個檔案。
- 副作用：exe 目錄多了約 27 MB native dll（`onnxruntime.dll` 16 + `libSkiaSharp.dll` 11）與 `pdfium.dll`；
  `onnxruntime` 需要 VC++ 2019 runtime。SkiaSharp 釘在 3.119.x（跟 ScottPlot 同一支），
  所以 `PDFtoImage` 用 5.2.0（5.3 起會抬到 4.150.x）。

## 架構

```
dFinance.Parsing/
  StatementParser        唯一的進入點：PDF 路徑 + 密碼 → ParsedStatement
  Parsers/               一個版面一個 class，都實作 IStatementParser
    ParserFactory   依序問誰認得這張單，第一個人贏
    StatementTable               靠表頭文字定位欄位座標的共用工具
  Ocr/OcrReader.cs       圖片式帳單：柵格化→OCR→換回 PDF 座標（與文字層的列合併），結果快取在 data\ocr\
dFinance.Data/            資料庫層（只依賴 dFinance.Parsing）
  Db.cs                  開庫（Foreign Keys=True、WAL）、跑遷移、備份、data\ 位置解析
  migrations/*.sql       結構的單一來源（嵌入 dll，啟動自動套用，套過的記在 migration 表）
  Money.cs               錢的單位換算只准出現在這裡（*_minor = 1/100 元的整數）；顯示的小數位走 `Money.Format(minor, scale)`
  StatementImporter.cs   留底 + 來源血統 + 去重 + 重匯時保留人工改過的分類；也管幣別對代碼與帳戶歸戶（含幣別）
  LedgerStore.cs         帳本清單與概況的單一出口：報表口徑（Live）、淨值（含幣對匯率換算）、餘額校驗、警示計數、匯率、寫入都走它
  RateSource.cs          抓 Yahoo 匯率（`{from}{to}=X`）：要帶 User-Agent、proxy 可設定、失敗不動現有數字
dFinance/                WPF：功能表＋TabControl〔首頁｜匯入 PDF〕＋開出來的帳戶分頁
  LedgerTab.xaml(.cs)    首頁（dashboard）與帳戶分頁是同一個控制項（只差有無 accountId）：首頁是淨值/折線/未來帳單，
                         帳戶分頁是篩選/可編輯格子/血統
  RecentFiles.cs         最近開啟清單（存在 data\recent.json，可攜）
  Dialogs.cs             ＋記一筆、＋分類 兩個小輸入框
  AccountsDialog.cs      帳戶管理（新增/編輯/歸檔、期初餘額、卡額度與自動扣繳）＋餘額盤點視窗
  CurrencyDialog.cs      工具→幣別…：基準幣、幣別字典（小數位/別名）、匯率表、人工填/抓匯率/proxy、拆幣別不符的列
  MainWindow             功能表（換資料庫/關於）＋分頁管理＋解析工作台（住在「匯入 PDF」分頁）
devcheck/                開發用：dump 文字層（`--ocr` 會把 OCR 讀到的字也 dump 出來，前面標 ~），或 `--import` 批次入庫，或 `- - --rates [proxy]` 抓匯率
```

新增一家銀行 = 在 `Parsers/` 新增一個 class 並掛進 `ParserFactory`。
 parser 的命名規則（Card / Deposit / Bill）寫在 `IStatementParser` 的註解 ——
**類名尾綴同時也被匯入器用來判斷單據種類**（Card→信用卡帳戶、Deposit→銀行帳戶、Bill→預設付款帳戶）。
資料庫設計與必須遵守的規則（型別、金額符號、`status`、重匯與去重）寫在 [dFinance-db-design.md](dFinance-db-design.md)。

要新 parser 前先 dump 文字層（照版面分組、帶座標，才看得出欄位怎麼對）：

```bash
cd devcheck/bin/Release/net8.0 && ./devcheck.exe "D:/dennys/statement/<檔案>" - --rows   # 或 --words
```

## 密碼

測試與 devcheck 會讀 repo 根目錄的 `.env`（已 gitignore，格式見 `.env.example`），
把每個檔案拿去試每個 PDF，試到開為止 —— KEY 名字隨便取，同一行用逗號放多組密碼。

三個要當心：

- `.env` 在 repo 根目錄，不在 `dFinance.Tests/` 底下（測試從執行情況目錄往上找）。
- 值前後不要留空格（照原樣讀入，含空白就會一直解不開）。
- 全部密碼都試過還是打不開 → 測試會失敗並講明是哪一份，不會靜靜跳過。

UI 不讀 `.env`：UI 一定要你親手打密碼。

## 已知限制

- 介面有四種語言，但**譯稿是程式寫的，沒給母語人士校過**（尤其 en/ja 的講法）。缺譯不會報錯 ——
  會悄悄退回繁體中文，只能靠 `dotnet test` 的 `LocalizationTests` 與 `--uismoke` 抓。
  語言只換文字：金額與日期格式不跟著語系走（`Money.Format` 固定不變 culture）。
- OCR 只做圖片式帳單（見「OCR」那一節）；不做 LLM。⛔ 渣打那兩份不做（文字層是亂碼）。
- 卡片帳單的「本期應繳」與消費合計常差在繳款/退款列 —— 這是對的，parser 會警告而不是偷改數字。
- 存款單的餘額欄、帳戶概要、股票/基金資產都還沒建模（見 TODO）。
- **繳款列還沒拆成 `transfer`**（parser 只有 `charge`/`credit`）：繳款只落在卡那一側、銀行端沒扣，
  所以**淨值會高估**；拆出來（TODO 2b）之後才會自己對。
- 淨值只換算**有匯率**的幣對：匯率在「工具 → 幣別…」裡人工填或按按鈕從 Yahoo 抓（不自己按時開就不連網），
  抓來的蓋不掉人填的；留空就是故意不計入（只講「N 個帳戶未折算」與缺哪幾對）。
  非台幣的銀行單子（沒 parser）只能手動記帳 —— 這期的「通用」只到錢與幣別層。
- **舊資料庫裡可能還有「幣別跟帳戶不符」的列**（王道的美金列直接倒在台幣帳戶那類），那些金額是照帳戶幣別進淨值的；
  淨值明細會在該列標「⚠ 含 N 筆幣別不符的列」，按「工具 → 幣別… → 把幣別不符的列拆到對的帳戶」就修好（對不上可從 `data\backups` 還原）。
- 「餘額不符」的校驗現在有兩個輸入：存款單解析出的餘額、還有人工盤點 —— 盤點打錯就會誤報，
  但那不是 bug，是提醒（改盤點值或期初餘額都會让它消失）。

## 接受的邊界（做「通用」時故意留的）

- 儲存是 1/100 元 → **小數三位以上的幣別（KWD/BHD 那種）會丟精度**。台幣/美金/日圓/人民幣/歐元/英鎊都不受影響。
- 缺幣對就**不折算**，不用第三種幣湊（不鏈式）；要算就在「幣別…」把那一對填起來或抓起來。
- **不做貨幣符號表**（$ / ¥ / NT$ 各地寫法不同），只依 `currency.scale` 做小數位。
- **國際化延後**：UI 文案繁中寫死、日期要收民國年、`IssuerNames` 是台灣銀行清單。
  所以這期的「通用」= 錢與幣別層通用（只要那個幣在字典裡，建帳/匯率/淨值/報表都不用改程式），
  文件層（各銀行 PDF）仍是台灣限定。

## 安全

- 除了匯率，不連任何外部服務（OCR 在本地跑、不會自己抓模型；翻譯/LLM 不在這一版）。匯率也**只有人在「幣別…」裡按按鈕才連**：
  Yahoo chart API，一定要帶 User-Agent（不帶直接 429）、公司網要填 proxy（`settings.rate_proxy_url`），
  抓失敗一律不動現有數字。
- 密碼只在記憶體裡過一遍，不落任何檔案、不寫 log。UI 不讀 `.env`，一定要親手打（逗號分隔可打多組）。
- 留底的 PDF 是**原樣**（原本有密碼保護就還是加密），重解析時要重新打密碼。
- 資料庫本身**不加密**（SQLite 官方不支援；要上得換 SQLCipher 並管主密碼）；靠 Windows 帳號與磁碟保護。
- `data\`（帳本與留單）、樣本 PDF 與 `.env` 一律不進版控。

## 開發

```bash
dotnet build dFinance.sln -c Release
dotnet test dFinance.Tests -c Release

# 批次匯入整個資料夾（同一份檔重跑會被擋，可放心重跑）
cd devcheck && dotnet run -c Release -- "D:\dennys\statement" - --import

# 驗主頁 UI（不用人按）：開主頁 + 一個帳戶分頁，印出載入到幾列
powershell -ExecutionPolicy Bypass -File devcheck/smoke.ps1 -Configuration Release
#   就是 dFinance.exe --uismoke <結果檔>；資料夾只讀不寫（啟動會多一份備份）

# 驗解析那條路徑（要人看著）：先開著 dFinance.exe 拿到 pid，再叫它選檔 → 打密碼 → Parse
powershell -ExecutionPolicy Bypass -File devcheck/uicheck.ps1 -ProcessId <pid> -PdfPath <pdf> -Password <密碼>
```

下一步與缺的檔案記在 [TODO.md](TODO.md)；走過什麼記在 [CHANGELOG.md](CHANGELOG.md)；
改到介面文字請先讀 [LOCALIZATION.md](LOCALIZATION.md)（不准用 `{x:Static}` 取字串、
code 裡硬編中文切語言不會更新）。
