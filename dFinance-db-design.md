# dFinance 資料庫設計

本地端記帳軟體。SQLite 單一檔案，**portable：跟程式同一個 `data\` 資料夾**，整個資料夾拷走就是移機。
不連任何外部服務。

位置解析順序：環境變數 `DFINANCE_DATA` → 開發時 plan/repo 根的 `data\`（避开被 `dotnet clean` 砍掉）→ 發佈後 exe 旁邊的 `data\`。
`Db.DataDirectory` 是可寫屬性，以後做「設定」就存 `settings` 表再蓋它。里头三個東西：`dfinance.db`、`documents\`（留底）、`backups\`。
`data\` 已在 `.gitignore`（帳本與留底 PDF 是個資，不准進版控）。

**DDL 的單一來源是 `dFinance.Data/migrations/0001_init.sql`**（嵌入在 dFinance.Data.dll 裡，啟動時自動套用）。
本文件只講「為什麼這樣設計」與「必須遵守的規則」，不要在這裡重複貼 DDL。

## 定過的基礎決定

| 項目 | 決定 | 為什麼 |
|---|---|---|
| DBMS | SQLite（`Microsoft.Data.Sqlite` 10.0.15，內含原生引擎 3.53.3） | 單機單人、零安裝、可用 `BackupDatabase` 備份 |
| 錢 | **全部 `INTEGER`，結尾一律 `_minor`，帳本單位 = 1/100 元** | SQLite 只有 INTEGER/REAL/TEXT/BLOB。REAL 會飄；TEXT 存小數時 SQL 端 `SUM()` 仍會轉 REAL、而且 `WHERE amount > x` 變成字典序比較。只有整數同時滿足「SQL 聚合精確」與「金額可篩可排」 |
| 投資數量/單價 | 另用 `_minor` scale = 10^8（**尚未實作**：投資三表還在 TODO，`Money.cs` 裡先不擺沒人用的換算，做的時候再補） | 股數要八位小數，跟帳本的 1/100 不能共用 |
| 換算位置 | 只能出現在 `dFinance.Data/Money.cs` | parser 持續用 `decimal`，進出 DB 才轉換，避免 ×100 散落各處 |
| 貨幣小數位 | `currency.scale` **只影響顯示**（讀它的是 `Money.Format`），儲存統一 1/100 | 否則跨幣 `SUM(amount_minor)` 會因各幣位數不同而錯；美金也不會被顯示成整數 |
| 基準幣 | 存 `settings.base_currency`（預設 TWD）；匯率存在幣對上、不依賴它 | 換基準幣只是換報表口徑，不會弄髒既有匯率，也不用改表 |
| 一個帳戶一種幣別 | 匯入時帳戶鍵值 = `(issuer, mask, type, currency)` | 否則 1,250,000 日圓會照台幣進淨值（舊資料有「拆到對的帳戶」按鈕可搬） |
| 國際化 | **UI 文字已走 resx**（預設繁中 + en-US / zh-CN / ja-JP，執行期可切，見 `LOCALIZATION.md`）；
  語系存 `settings.ui_language`（跟資料庫走，不用 AppData）。但**日期仍存 `yyyy-MM-dd`、不收民國年**（民國年只在
  parser 內部換算），`IssuerNames` 是台灣銀行清單（那是資料不是文案） | 換語系不用重開程式，也不用為每個語言開一份 ResourceDictionary；
  但錢與日期格式不跟著語系走（`Money.Format` 固定 invariant，`CurrentCulture` 不碰），否則逗號小數點的語系會把金額搞錯 |
| 日期 | `TEXT`，格式 `yyyy-MM-dd` | 可字串排序、可用 `date()` 系列函式。C# 端用 `DateOnly`，provider 本來就存這個格式 |
| 日期缺失 | **留 NULL，不准塞 `0001-01-01`** | 並把該列 `status` 留 `imported` 等人補。舊程式碼用 `DateOnly` 預設值，進 DB 就會憑空多一筆 |
| 布林 | `INTEGER` + `CHECK (x IN (0,1))` | SQLite 沒有 BOOLEAN |
| 型別名稱 | 只用 INTEGER/REAL/TEXT/BLOB | `DECIMAL(18,2)`、`VARCHAR(100)`、`TIMESTAMP` 這些在 SQLite 不會生效（官方文件明說 facets 不強制），寫了只是註解；約束一律 `CHECK` |
| 外鍵 | 連線字串 `Foreign Keys=True` | **SQLite 預設不啟用外鍵**，忘了開等於 DDL 裡的 REFERENCES 是裝飾 |
| 併發/權限 | 不做 user 表、不做樂觀鎖 | 單機單人（2026-11-18 定案） |
| 加密 | 不加密 | SQLite 官方不支援；要上得換 SQLCipher 並管主密碼，跟「密碼不落檔」承諾打架。靠 Windows 帳號與磁碟加密 |
| 備份 | 啟動時 `BackupDatabase` 留最近 5 份到 `backups/` | 用備份 API 而非複製檔，WAL 未落盤的資料才會進副本 |

## 記帳語意（改程式前先讀這段）

- **一個金流 = `entry` 一張表的一列**。`kind` ∈ `income / expense / transfer / refund / adjustment`。
- **符號**：`CHECK (amount_minor > 0 OR kind IN ('refund','adjustment'))`。退款是負數的 `refund`，不是負數的 expense。
- **`transfer` 一定要有 `to_account_id`**，且不准有 `category_id`（都由 CHECK 擋）。
- **報表基準日是 `txn_date`（消費日）**，不是入帳日、不是請款月。`posting_date` 與 `bill_id` 另存，讓報表可切換「依帳期彙總」。
- **`status`** ∈ `cleared`（已核銷）/ `imported`（匯入待確認分類）/ `pending`（尚未發生）/ `ignored`（這筆不記）/ `duplicate`。
  **報表預設只算 `cleared` + `imported`** —— 這是鐵律，忘記過濾就會把未發生的款項算進支出。
- **尚未發生的款項不要存成 `entry`**：本期應繳是負債變動，放在 `credit_card_bill.total_due / due_date`，
  要顯示就在 UI 從帳單表 UNION 出來。（MMEX 把這類資料放獨立的 `BILLSDEPOSITS_V1`，同樣不進帳本表。）
- **信用卡保留帳戶層**（`account.type='credit_card'`）：消費 = `expense` 掛在卡帳戶、繳款 = `transfer` 銀行→卡。
  如此「本月支出」的 `SUM(expense)` 剛好等於帳單金額（可對帳），「還沒繳多少」看卡的負餘額。
  ⚠️ 現況 parser 還沒把明細裡的繳款列拆出來（`ParsedTransaction.Kind` 只有 `charge`/`credit`），
  所以負額暫時的落庫成 `refund` 並附警告。**在拆出來之前，把繳款列記成 expense 會造成支出雙倍。**
  `shortcut:` 等 parser 拆出繳款/消費（TODO 2b）後改記 `transfer`，屆時「明細合計 ≠ 應繳」那條警告會自然消失。
- **繳費單（電/水/瓦斯/電信/eTag）= 一筆 `expense`**。金額 0 的列直接跳過（計進 `document.skipped_zero`）。
  付款帳戶依 `settings.default_payment_account_id` → 最早的銀行帳戶 → 既有現金帳戶 → 新建現金帳戶（並警告）。

## 來源血統（dFinance 比一般記帳軟體多出來的一層）

座標式 parser 一定会改版，所以每一筆都要能回答「從哪份檔案、哪一頁、哪一行來」：

```
document   一份 PDF 一列：sha256(UNIQUE)、原路徑、原檔名、parser、parser_ver、帳期、警告、superseded_at
entry      帶 source_document_id / source_page / source_raw / source_line_hash / edited_at / orphaned_at
```

- **留底**：原始檔案**原樣**（含原本的密碼保護）存進 `<資料夾>\documents\yyyyMM\{sha256}.pdf`。
  原檔名留在 `document.file_name` 給人看。重解析時請使用者重新打密碼 —— 這樣才不破「密碼只在記憶體過一遍」。
- **去重兩層**：① `document.sha256 UNIQUE` 擋同一份檔案重匯；② `source_line_hash` 擋同內容重複列。
- **`source_line_hash` 只用穩定欄位**（issuer / mask / 卡號 / 帳期止 / 消費日 / 入帳日 / 金額 / 幣別 / 頁碼）。
  **商家名刻意不进哈希** —— parser 之後會清洗商家名，進哈希會讓舊列全部認不出來、變成一堆重複列。
  同鍵重覆（同天同額刷兩次）用 `#2`、`#3` 出現次數後綴區分。
- **比對範圍**：`credit_card` 可以跳檔案比（卡＋帳期＋金額＋日期就是身份，重匯要能對回去）；
  存款單與繳費單**只在同一份檔案內比**（`StatementImporter.FindExisting` 的 `scope`）—— 它們沒有可靠身份，
  跳檔案比會把兩張同月同額的不同戶號繳費單合成一筆 —— 那不是標記，是**第二筆根本不會被插入**，而且無警告。
  哈希裡也沒有戶號：台電住戶編號/自來水戶號/電信繳費編號現在只存在 `document.raw_text`（見 TODO）。
- **跳檔案只做「疑似重複」標記**（`LedgerStore.DuplicateGroupKey`：同日＋同額＋同幣別、而且出現在 >1 份檔案，
  只算 `a.type='credit_card'`）：主頁狀態篩選的 `dupgroup` ＋底部警示列按鈕，兩筆都留在帳本裡由人決定。
  判錯最多多一則警告，擋下來是少一筆真交易 —— 這就是兩側一個硬擋、一個只標的理由。
  **已知代價**（同一張單子第二次取得時要想清楚）：**同樣 bytes** 再匯一次會被 `document.sha256` 擋掉
  （「同一份檔案已匯入過」）；但這些電子單子是**伺服器即席產生**的 PDF（自來水 e-bill 的 Producer 是
  `EVO HTML to PDF Converter`、檔名自帶 UUID；台電 e-Bill 是 `PReS Connect` 產單系統），
  重新下載/補發/e-mail 重寄就可能給出不同 bytes → 不同 sha256，第一關過關。
  卡帳有跳檔案哈希所以对得到舊列；存款單與繳費單對不到 → **整份存成兩份而且沒有任何警告**（概況與淨值直接雙倍）。
  要堵這個洞要先有可靠身份（戶號進 `AccountMask`，`BaseHash` 才分得出來）或用餘額校驗把它照出來（見 TODO b）。
- **重匯規則（同一份帳單的修正版）**：逐列用哈希對旧列 →
  `edited_at IS NULL` → 覆蓋解析欄位；`edited_at` 有值 → **只更新 `source_raw`/`source_page`，分類/商家/狀態/備註一個都不動**；
  這次沒對到的旧列 → 標 `orphaned_at`，**不准刪**（可能只是新版漏解析），UI 要能列出來。
- **取代只對信用卡做**（靠 `credit_card_bill` 的 `UNIQUE(card_id, period_end)` 當自然鍵）。存款單/繳費單不做取代。

## 主頁（帳本清單）的讀取與寫入規則

實作在 `dFinance.Data/LedgerStore.cs`，UI（`LedgerTab`）不准自己拼 WHERE。

- **共用口徑**：`LedgerStore.Live` = `deleted_at IS NULL AND status IN ('cleared','imported')`。
  清單、KPI、淨值、警示計數全部用它（忘了就是未發生的款項被算進支出）。
- **餘額当场算**（derived）：`opening_balance + Σ(收入/調整) − Σ(支出/繳款/退款) + Σ(轉入)`，
  一個 `GROUP BY` 算完所有帳戶（不是每帳戶一支查詢），不建 rollup 表。
  `account_balance`（存款單快照）只做**校驗**：`BalanceMismatches()` 就是 parser 漏列的偵測器。
- **非基準幣帳戶沒有匯率就不計入淨值**（匯率存幣對，讀的是「帳戶幣別 → `settings.base_currency`」），
  只回報「N 個帳戶未折算」與缺哪幾對（`NetWorthReport.MissingPairs`）—— 宁可不計，也不用 1:1 把淨值算錯。
- **一個帳戶一種幣別**：匯入時帳戶鍵值是 `(issuer, mask, type, currency)`，同一帳號的美金/日圓列會分建兩個帳戶。
  舊資料（王道、兆豐）用「工具→幣別…」裡的「把幣別不符的列拆到對的帳戶」一键搬。
- **卡未繳查 `credit_card_bill`**（`paid_at IS NULL`），不從 `entry` 推。
- **可編輯的只有 分類/商家/備註/消費日**，四個都一併寫 `edited_at`（重匯只准更新 `source_raw`）。
  **金額唯讀**：改了就和留底 PDF 對不上、餘額校驗永遠失敗；修正用 `kind='adjustment'` 另記一筆。
  補日期只准 `yyyy/MM/dd`（`TryDate`），挡下來而不是猜。
- **刪除是軟刪除**（`deleted_at`），並給「已刪除」篩選值 + 還原；真刪會連血統一起不見。
- **疑似重複只做信用卡、只標記**：同 `(txn_date, amount_minor, currency)` 出現於 ≥ 2 份檔案才入列，
  一律不改資料（要排報表才手動標 `status='duplicate'`）。
- **分頁靠版本號重載**：每個寫入推 `LedgerStore.DataVersion`；分頁切回來時比對它，髒了才重查
  （multi-tab 對比不會每次重讀）。

## 表清單

**已實作（`0001_init.sql` 結構 + `0002` 分類種子 + `0003` USD seed + `0004` 分類樹 + `0005` 幣對匯率/幣別別名/基準幣）**
`migration` `settings` `currency` `member` `account` `category` `document` `card` `credit_card_bill`
`entry` `account_balance` `exchange_rate`

`account_balance` 不只是存餘額：**「既有交易累加 == 最近一次帳單餘額快照」是偵測 parser 漏列的自動校驗**，
比「解析到 0 筆才警告」強得多（漏 3 筆也會被抓到）。目前 parser 還沒有把餘額欄解析出來（TODO 2a）。

`exchange_rate` 明記幣對 `(from_ccy, to_ccy, rate_date, source)`：「1 from = ? to」，所以換基準幣只是換 where，
舊數字的意義不會靜靜改變（也就不用清匯率）。同一天的 `manual` 優先於 `yahoo` —— 抓來的蓋不掉人填的。
來源 `RateSource`（Yahoo chart API，符號 `{from}{to}=X`）：**只有人按按鈕才連網**，失敗一律不動現有數字。
實測兩個坑：不帶 User-Agent 直接 429；公司網不填 proxy（`settings.rate_proxy_url`）就連不出去。
海外消費**優先取帳單上已印的換算後金額**（`amount_minor`），原幣金額只放 `orig_amount_minor`/`orig_currency` 做顯示。
單子上的幣別原文（「美金」「日圓」）靠 `currency.aliases` 對成代碼 —— parser 不查資料庫，只交原文。

**尚未建表（等要做時再發 `0002_*.sql`）**

| 表 | 用途 | 對應 MMEX 的 |
|---|---|---|
| `rule` | 商家字串 → 分類（`contains`/`exact`/`regex` + priority）。沒有它，匯入後 100% 未分類 | — |
| `entry_split` | 一筆拆多分類/多人。用整數儲存才能證明「拆完加總恰好等於母筆」 | `SPLITTRANSACTIONS_V1` |
| `tag` / `entry_tag` | 自由標記 | `TAG_V1`/`TAGLINK_V1` |
| `budget` | 分類月度預算 | `BUDGETTABLE_V1`/`BUDGETYEAR_V1` |
| `recurring` | 定期扣繳/分期（尚未發生的另存，不進 `entry`） | `BILLSDEPOSITS_V1` |
| `asset*` | 投資（Phase 4）。注意：**現金側要由 `asset_transaction` 推導，不要另寫 `entry`，否則股息會double count** | `STOCK_V1`/`STOCKHISTORY_V1` |

## 命名與慣例

- 外鍵欄 `<表>_id`；布林 `is_*`；時間 `*_at`；日期 `*_date`；金額 `*_minor`。
- 每個欄位都要能回答「誰寫的」：parser 產出、人工輸入、還是匯入 —— `entry.source` 與 `*_minor` 的註解就是這個用途。
- 遷移：檔名 `<編號>_<名字>.sql`，一個腳本一個交易，套過就記在 `migration`。改結構發新檔，不要改舊檔。
- 測試用臨時 `.db`；**刪檔前一定要 `SqliteConnection.ClearAllPools()`**，否則連線池鎖住刪不掉。
