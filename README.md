# dFinance

本地端記帳軟體：讀銀行/帳單 PDF → 拆成記帳資料 → 存進本地 SQLite，並留底原始單據。

`.NET 8` / WPF / PdfPig / Microsoft.Data.Sqlite。沒有雲端服務，不需要連線，**portable**（整個 `data\` 資料夾拷走就是移機）。

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
3. **看帳**（主頁，啟動就停在這）：篩選（帳戶/日期/分類/狀態/商家）＋概況（本月收支、淨額、待分類、
   淨值/資產/負債、卡未繳、未折算帳戶）＋「淨值明細」展開就是總表（每個帳戶有多少錢、折算後多少、計不計入淨值）
   ＋可編輯格子（分類/商家/備註/消費日）＋右側來源血統（含「開啟留底 PDF」）
   ＋底部警示列（孤兒列/疑似重複/餘額不符，點了就篩出來）。要對比兩個帳戶就按「開帳戶分頁」（或雙擊帳戶欄），
   分頁各自留著自己的資料，切來切去不重查（Ctrl+W 關）。
4. **帳戶管理**（主頁按「帳戶…」）：新增/編輯/歸檔帳戶（現金、錢包、外幣帳戶都只能從這裡建）、
   期初餘額與期初日期、信用卡的額度與自動扣繳銀行、還有**餘額盤點**（人當場數的錢）。
   盤點寫進 `account_balance(source='manual')` 就是「餘額不符」那條校驗的輸入，對不上會問你要不要記一筆 `adjustment` 補齊。

資料落在 `data\`（`dfinance.db` + `documents\` 留底 + `backups\` 自動留 5 份）。
位置解析順序：環境變數 `DFINANCE_DATA` → repo 根 `data\` → exe 旁邊 `data\`（發佈後就是自帶資料）。已 gitignore。

## 支援到哪

| 銀行 | 單子 | 狀態 | 已知問題 |
|---|---|---|---|
| 聯邦 | 信用卡帳單 | ✅ | |
| 聯邦 | 數位存款電子對帳單 | ✅ | 只有收入/支出欄，餘額沒建模 |
| 王道 | 存款對帳單 | ✅ | 只解析活期明細，定期存單沒算進總資產 |
| 台新 | 信用卡帳單 | ✅ | 繳款/退款列讓合計不等應繳金額（正常，會警告） |
| 台北富邦 | 信用卡帳單 | ✅ | 同上 |
| 兆豐 | 信用卡帳單 | ✅ | 同上；帳號末四碼被遮時拿不到卡號 |
| 永豐 | 信用卡帳單 | ✅ | 明細只有 MM/DD，年份靠繳款截止日推 |
| 永豐 | 電子綜合對帳單 | ✅ | 只解析存款明細那段；帳戶概要、別家銀行信用卡應繳列未建模 |
| 國泰世華 | 信用卡帳單 | 🟡 部分 | 手上樣本是已全額繳清的月份，只有繳款一筆可驗；需要一份有消費明細的 |
| 中國信託 | 存款／信用卡 | ⛔ 需要 OCR | 文字層是圖片與 Type3 字型，讀不到字 |
| 匯豐 | 信用卡帳單 | ⛔ 需要 OCR | 文字層 0 個中文字 |
| 渣打 | 信用卡帳單 | ⛔ 需要 OCR | 文字層是亂碼（字型沒 ToUnicode） |

非銀行的繳費單也能拆成一筆記帳用的交易（電費、水費、天然氣、電信、eTag），見 `UtilityBillParser`。
水費那份本期 0 元（退款抵掉），會警告「記帳上可忽略」。

⛔ 那三份要 OCR，這一版不做 OCR，所以支援不了。

## 架構

```
dFinance.Parsing/
  StatementParser        唯一的進入點：PDF 路徑 + 密碼 → ParsedStatement
  Parsers/               一個版面一個 class，都實作 IStatementParser
    ParserFactory   依序問誰認得這張單，第一個人贏
    StatementTable               靠表頭文字定位欄位座標的共用工具
dFinance.Data/            資料庫層（只依賴 dFinance.Parsing）
  Db.cs                  開庫（Foreign Keys=True、WAL）、跑遷移、備份、data\ 位置解析
  migrations/*.sql       結構的單一來源（嵌入 dll，啟動自動套用，套過的記在 migration 表）
  Money.cs               錢的單位換算只准出現在這裡（*_minor = 1/100 元的整數）
  StatementImporter.cs   留底 + 來源血統 + 去重 + 重匯時保留人工改過的分類
  LedgerStore.cs         帳本清單與概況的單一出口：報表口徑（Live）、淨值（含匯率換算）、餘額校驗、警示計數、寫入都走它
dFinance/                WPF：TabControl〔帳本｜匯入 PDF〕＋開出來的帳戶分頁
  LedgerTab.xaml(.cs)    主頁與帳戶分頁是同一個控制項（只差有無 accountId）：篩選/概況/可編輯格子/血統/警示
  Dialogs.cs             ＋記一筆、＋分類 兩個小輸入框
  AccountsDialog.cs      帳戶管理（新增/編輯/歸檔、期初餘額、卡額度與自動扣繳）＋餘額盤點視窗
  MainWindow             原来的解析工作台（現在住在「匯入 PDF」分頁）＋分頁管理
devcheck/                開發用：dump 文字層，或 `--import` 批次入庫
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

- 沒做 OCR 與 LLM，⛔ 那三份無解。
- 卡片帳單的「本期應繳」與消費合計常差在繳款/退款列 —— 這是對的，parser 會警告而不是偷改數字。
- 存款單的餘額欄、帳戶概要、股票/基金資產都還沒建模（見 TODO）。
- **繳款列還沒拆成 `transfer`**（parser 只有 `charge`/`credit`）：繳款只落在卡那一側、銀行端沒扣，
  所以**淨值會高估**；拆出來（TODO 2b）之後才會自己對。
- 淨值的外幣部分只換算**有匯率**的幣別：匯率是人工填的（USD 由遷移 seed 32.9，其餘在「帳戶…」裡填），
  留空就是故意不計入（只講「N 個帳戶未折算」）；匯率自動更新（Yahoo）之後再做。
  定存/投資也還沒建模。
- **王道那份的 9 筆美金列記在台幣帳戶裡**（parser 把外幣活期那段倒進同一個帳戶），所以金額是照台幣算進餘額的；
  淨值明細會在那一列標「⚠ 含 N 筆幣別不符的列」。真正的修法（依幣別拆帳戶）在 TODO。
- 「餘額不符」的校驗現在有兩個輸入：存款單解析出的餘額、還有人工盤點 —— 盤點打錯就會誤報，
  但那不是 bug，是提醒（改盤點值或期初餘額都會让它消失）。

## 安全

- 不連任何外部服務（OCR/翻譯/LLM 都不在這一版；日後的匯率匯入也是預設關閉、明確開啟才連）。
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

下一步與缺的檔案記在 [TODO.md](TODO.md)。
