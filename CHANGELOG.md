# 變更紀錄

格式照 [Keep a Changelog](https://keepachangelog.com/zh-TW/1.1.0/)；日期是 commit 的日期，
尚未提交的东西在「未發布」。**做完成的事一律寫進本檔，`TODO.md` 只留還沒做的。**

## 未發布

### 修復（依帳期彙總：幣別不符的列會讓應繳算兩次）
- 同一份帳單的明細若有**另一種幣別**的列（舊庫混进去的，或是工具→幣別… 的「拆帳戶」把列搬到別個帳戶但 `bill_id` 留著），
  原本會拆成兩列、**每列都扣一次單上應繳** → 合計的應繳直接翻倍；而且差額是拿不同幣別的金額直接相減（違反同一把尺）。
- 現在只有跟卡帳戶**同幣別**的列算進那一期（一列就是一份帳單），幣別不符的列另以「（幣別不符）」呈現、
  並計進 footer 那句「沒算進任何帳期」；差額只准在同一幣別內相減。`BillPeriodTests` +2 條（共 275），
  其中一條直接用「拆帳戶」那顆按鈕的路徑跑（那是實際上會踩到的入口）。

### 修復（2026-11-20 審查第二輪：幣別口徑、崩潰、顯示）
- **「記一筆」不再把外幣帳戶的筆默默寫成 TWD**：`AddEntryDialog` 的幣別照所選帳戶帶；
  `LedgerStore.InsertManual` 再多挡一道（幣別≠帳戶幣別就講人話；轉帳的兩個帳戶也必須同幣別）。
- **首頁 KPI 的收支改用基準幣口徑**（`LedgerStore.Totals`）：以前把台幣欄位與美金欄位直接相加。
  現在跟 `NetWorth`／`CategoryPivot` 同一把尺：先照幣別加總、折算只經 `Money.Convert`；
  完全沒匯率的幣別不計入金額，但會報「未折算列 N 筆」（宁可不計也不要 1:1，也不要讓人以為這個月沒花錢）。
- **每月總資產折線跟左邊淨值是同一個數了**：沒有消費日的列在淨值那邊被当成「早就在帳上」，折線却把它們丢掉
  （實測 −5,935 vs −4,290）。現在折線把它們算進第一點之前（同一個假設，同一支 `Undated`）。
- **「把幣別不符的列拆到對的帳戶」不再炸**：目標帳戶（例「兆豐 … USD」）常常早就存在（匯入本來就照
  一帳戶一幣別建），舊程式直接插同名 → 撞 `account.name` 的 UNIQUE，整個動作一筆都没搬到。
  现在沿用既有帳戶（同幣別、未歸檔才算），名字被占用就加號碼，而且整個動作包在一條交易裡
  （要嘛全部搬完，要嘛一個都不動，不會留一堆新建的空帳戶）。
- **分類報表不會再整棵消失**：`PivotTree` 以前用「整年相加 = 0」決定要不要進表，1 月刷 1,000、2 月全額退款
  那種兩個月都有數却被藏起来，結果小計加起来跟最下面的總計列對不上。現在看「有没有任何一個月有數」。
- **UI 不再被 `database is locked` 打掛**：`LedgerTab` 的寫入一律经 `Write()`（失敗就講訊息並重查，
  不會留一個看起來改好了的格子）；匯入期間把其他分頁 disable；`Db.Open` 連線字串加 `Default Timeout=5`
  （以前撞鎖要等 30 秒才丢例外）；`App` 补 `DispatcherUnhandledException`（`--uismoke` 不接，否则測試会把崩潰吞掉）。
- **顯示不再四捨五入錢**：帳本金額欄改用 `AmountText`（讀 `currency.scale`）、血統顯示雨位小數（存的是 1/100）、
  未來帳單帶幣別後缀、匯入預覽 `N0`→`N2`、餘額校驗的差額照該帳戶的小數位、折線軸跟基準幣的小數位。
- **繳費單的日期改走共用的 `PdfText`**：`UtilityBillParser` 自己寫了一份日期樣式，不收 U+2010–U+2015 那幾種橫線
  （同一支檔案裡 `IsMoney` 已經共用過，樣式不准有第二份）；`StatementParser.Version` 4 → 5。
- **密碼不再出現在錯誤訊息**：`Imp_BadPassword` 只講試過幾組，不重複人剛打的密碼。
- 死代碼：`MainWindow.xaml` 匯入預覽的「類別」欄綁的是已經删掉的 `ParsedTransaction.Kind`（空白欄），拿掉；
  `LedgerStore.TopCategories` 零呼叫者。

### 新增
- **功能表 → 報表 → 依帳期彙總**（新功能表項目 + 常駐分頁 `BillPeriodTab`）：一列 = 一份信用卡帳單，
  並列「筆數 / 消費合計 / 單上應繳 / 差額 / 繳款 / 繳銷 / ⚠」，用途是把「明細合計」拿去對紙本帳單的應繳
  —— 對不上的那幾期就是 parser 漏列的候選。
  - 歸期靠 `entry.bill_id`（外鍵），**不用日期區間**（跨期消費與 `txn_date` 為 NULL 的列會被日期區間漏掉）。
  - 消費合計 = `Live` 且 `kind ≠ 'transfer'` 直接相加：pending/deleted 不計、繳款（轉帳）不算消費
    （另進「繳款」欄，不然支出會 double count）、退款在 DB 裡是負的所以直接加就是淨消費。
  - 差額 0 → «—»（對上了，不是少一筆）；單上沒印應繳 → 差額留白（不准把沒印當成 0）。
  - 列上的金額**照單上的幣別**（這張表要跟紙本對帳），只有合計列按 `exchange_rate` 折成基準幣；
    完全沒匯率的期不進合計列、計數講清楚（同一把尺：`Money.Convert`）。
  - 挂不上帳期的卡帳列（卡號被遮那種）不會静靜吃掉：以「（無帳期）」列顯示 + footer 報數。
  - 篩選：每張卡最近 N 期（window function，0=全部）+「只看對不上的」（會留下缺匯率的期）。
  - 點一列 = 開那張卡的帳戶分頁並套用該期範圍；點「繳銷」那一格 = `MarkBillPaid`（跟首頁同一支）。
  - 資料只有 `LedgerStore.BillPeriods()` 這一支 SQL（口徑不准在 UI 拼第二次）；不建 rollup 表。
  - 本機現況：9 期裡 7 期對不上，但**不是這張表算錯** —— 聯邦那兩期（122 筆）差額是 «—»，
    對不上的全是舊匯入時代把**繳款列存成 `refund`** 的那幾期（被當成退款扣掉、「繳款」欄全空）。
    設好自動扣繳帳戶後重匯那些單子就會變成 `transfer`，差額才會收斂。
- **分類帳加「金額 / 百分比」切換**：報表工具列多一個下拉（金額／百分比）。占比的分母是**同側塊**
  （支出塊／收入塊）該月小計，不是最下面那列「收−支」—— 用收−支的話，同一筆消費的占比會跟著當月
  有沒有收入而變，那就不是占比而是相對盈虧。
  - 規則全在 `PivotTree.PercentOf`（純函式，`PivotTreeTests` 钉四條）：塊該月為 0 → `—`（不是 0%，
    0 代筆是「沒有」、這裡是「除不出來」）；退款保留負號（分母取絕對值，符號看分子，塊被抵成負數也不會
    變 −100%）；塊自己恒 100.0%；總計列（收−支）不給百分比；小數位 1、格式 InvariantCulture
    （語系换过去不會把 12.3% 變成 12,3%）。
  - 切模式只重灌欄位（換綁定的 converter），不重查庫也不重建列；排序、drill down、展開完全不變
    （格子裡還是 long minor，照數值排）。`--uismoke` 會切到百分比再讀一格真的文字（綁错 converter 會變空白）。
- **剩下兩份存款單也有單上餘額了**：中信存款單拿明細最後一列的「帳戶餘額」（它本來就在算餘額鏈校驗，
  只是校驗完就把數丢掉）、永豐綜合對帳單同理（樣本在 `statement_old`，期末 0 元跟單上那張資產表對得上）。
  這兩家以前的「期初＋交易累加 ＝ 單上餘額」校驗等於**沒有輸入**，漏解析只剩「0 筆才警告」一條防線。
- **存款單上印的餘額現在會進 `account_balance`（自動防呆）**：聯邦讀「臺幣存款帳戶餘額　餘額基準日…」那張表
  （找不到就退回明細最後一列的餘額欄）、王道讀「帳號：… 餘額 1 元」與「美元：餘額 0.71 元」（台幣/外幣各一個帳戶）、
  兆豐同時讀明細裡的「115/09 月底餘額」與頁首帳戶總覽的「截至 115-10-31 為止」（兩個 as_of 都存，期初期末各校驗一次）。
  匯入寫成 `source='statement'`，`ON CONFLICT … WHERE source='statement'` —— **不准蓋掉人当场盤的 `manual` 數**。
  這才是偵測漏解析的網：「交易累加 ＝ 單上餘額」，比「解析到 0 筆才警告」強得多。
  - 順手修掉 `BalanceMismatches()` 的口徑：以前拿**全部交易**累加去比快照，但單子上印的是「那天結算的數」
    （兆豐 10 月的單子印的是 09 月底餘額），期初快照會天天誤報對不上。現在只比 `as_of` 當天（含）以前的交易，
    算式還是共用同一支 `DerivedBalancesCore`（口徑不准有第二份）。
  - `devcheck - - --balances`：不用開 UI 就能列快照與對不上的帳戶；`--import` 現在會把 warnings 印出來。
  - 尚待做：中信存款單**總覽那欄**的餘額（現在取的是明細最後一列的「帳戶餘額」，那張總覽因帳號被遮成
    `29953**3590*` 還沒拿来用）、王道「本月無交易」的薄單子（parser 回 null → 餘額跟著丢）。
- **卡帳的繳款列拆成 `transfer`，不再落成 `refund`**：`ParsedTransaction.IsPayment` 由 `StatementParser.LooksLikePayment`
  統一判（詞表是各家單子自己印的寫法：「已繳款金額-本行帳戶扣款」「兆豐商銀自扣入帳」「…自動轉帳扣繳台新信用卡款」
  「兆豐銀扣」），在 `StatementParser.Parse` 標一次，八支卡帳 parser 不用各寫一份。現金回饋（「現金回饋」不含繳款字）
  不是繳款 —— 它會直接抵應繳，要留在消費合計裡。
  - 「明細合計 ＝ 本期應繳」收進 `StatementParser.WarnIfDueMismatch()`，繳款（轉帳）不計入消費合計：
    永豐／台新／中信／兆豐那幾條「合計不會等於應繳」的警告自然消失，**留下來的那條現在只代表真的漏列**。
  - 轉帳方向：扣款帳戶 → 卡帳帳戶（卡帳的 derived 是負的，轉入才會往 0 走）；`amount_minor` 取絕對值（CHECK 要正數）。
  - 扣款帳戶 = 卡的 `autopay_account_id` → `settings.default_payment_account_id`（兩個都要没歸檔）。
    兩個都拿不出來就**留 refund 並警告叫人去設**，宁願性質錯也不要憑空猜一個帳戶扣它（猜錯就是悄悄讓那個帳戶少一筆錢）。
    重匯時 `UPDATE entry` 現在會連 `account_id`/`to_account_id` 一起寫，所以設好扣款帳戶再重匯一次就能把舊 refund 轉成 transfer。
  - `StatementParser.Version` 2 → 3（重匯會認出新舊列）。
  - 注意：全新的資料庫若第一份匯入的是卡帳（還沒有任何銀行帳戶），繳款列會留 refund + 警告；
    先匯存款單或在「帳戶…」設好自動扣繳帳戶再重匯就會變轉帳。
- **繳費單現在會抽「誰的帳」**（台電用戶編號 / 自來水水號 / 中華電信用戶號碼）放進 `AccountMask`，
  去重的穩定鍵（`source_line_hash`）因此有可靠身份：同一張單子從網銀**重下載/補發**（這些電子單子是伺服器即席產生的，
  同樣內容也會给不同 bytes）不會再被記兩次；同月同額的兩張不同戶號單子（幫房客繳的那种）仍然各存一筆。
  抽不到戶號就退回「只在同一份檔案內比」—— 宁願存兩份，也不要悄悄吃掉一筆。
- **四種語言、執行期切換**（繁體中文／English／简体中文／日本語）：「工具 → 語言…」點選立刻換，
  不用重開程式、不用重建視窗。介面文字全進 `dFinance.Data/Localization/Strings*.resx`（345+ 條 × 4 語），
  選好的語言存 `settings.ui_language`（跟資料庫走，不寫 AppData）。規則與驗收方式見 [LOCALIZATION.md](LOCALIZATION.md)。
- `--uismoke` 現在會把四種語系各換一次，確認選單文字真的跟著換、語言選單有列出來、而且沒有打錯的 key（`!Key!`）。
- **分類帳報表改成樹狀 pivot**（`dFinance.Data/PivotTree.cs` + `LedgerStore.CategoryPivot()` 取代 `CatMonthTotals`）：
  父列小計＝直屬＋整個子樹、子項只在自己 parent 底下排、按某一欄排序時父列帶著子列一起動；
  格子裡點分類欄＝展開/收合。種子分類拆出第三層（`0006_seed_category_subtree.sql`：餐飲→外食/超商飲料/外送…）。
- **未分類商家的建議與套用的修正**（`SuggestDialog` + `MerchantClassifier`）：可以只對某一個帳戶套用
  （`SetCategoryForMerchant(id, accountId)`），「未分類商家」清單改 `GROUP BY` 商家＋帳戶，沒名字的列會標成未命名。
- `devcheck <資料夾> - --autocat`：一鍵把未分類的交易全部標到最底層 leaf（大量改資料前會先備份）。
- **帳單可以標已繳**：主頁「未來帳單」那張表加了「已繳」按鈕（只有帳單列露得出來）。
  以前全 repo 沒有任何地方寫 `credit_card_bill.paid_at`，「卡未繳」與「未來帳單」只會一直累積，繳完也不會消失。

### 修改
- **parser 交出來的幣別改照單上原文**（永豐卡「臺幣」、永豐存款單取段落標題 `帳號:…(新臺幣)`、
  台新卡單上印的 `TW` 不再在 parser 裡對成 `TWD`）——對成代碼一律是匯入端 `EnsureCurrency` 靠 `currency.aliases` 做的事。
  落庫結果不変（aliases 本來就有那些詞），變的是口徑只有一個：新增 parser 不用再猜該交原文還是代碼。
  兩個例外（是版面判定不是對映）寫在 `Models.cs` 的 `Currency` 註解：繳費單只印「元」、中信的幣別藏在圖片標題。
- **文件分工**：`AGENTS.md`「不能破壞的規則」收了 5 條原本只在 TODO「要留意的限制」裡的鐵律
  （去重範圍要看身份、重匯時 `edited_at` 有值只准更新 `source_raw`、取代/孤兒只對信用卡、
  本期應繳≠消費合計不准湊數字、解析到 0 筆一定要警告）；`TODO.md` 第 3 節只剩產品定位的限制。
- **金額與日期格式不再跟著語系走**：`Money.Format` 改用固定（invariant）格式，帳本裡幾處自己 `ToString("N0")`
  的改走 `Money.Format`。換語言只換文字，`1,250,000` 不會因為切成 de-DE 就變成 `1.250.000`。
- `CategoryManagerDialog.cs`、`SuggestDialog.xaml.cs`、`MerchantClassifier.cs` 從 Big5 存成 UTF-8（之前是靠編譯器退回系統 ANSI 語系碼才沒變亂碼）。
- `LedgerStore.AccountTypes` 由 `static readonly` 欄位改成 property —— 欄位會把標籤凍在第一次取用時的語系。
- 判斷分支不再靠顯示字串：日期預設改用 `DatePreset(Mode, Label)`、分類帳報表的欄位靠 `SortMemberPath` 認身
  （原來比的是「欄頭等於『合計』」，翻譯下去就壞）、`SuggestDialog.NoCategory` 由 `const` 改 property。
- 存款單的 `ParsedTransaction.Kind` 統一成 `charge`/`credit`（王道原本自己用 `debit`）；
  `Models.Kind` 的註解寫清楚它是**給人看的**（匯入一律看金額正負號）與兩組語意。
- `migrations/0004_category_tree.sql` 轉存 UTF-8（原本是 Big5/ANSI）。`Db.Scripts()` 一律照 UTF-8 讀，
  所以它觸發條件裡的 `RAISE(ABORT, '…')` 訊息在資料庫裡是一團取代字元 —— 邏輯沒壞（比較的都是 ASCII），
  但跨收支方向建子分類時使用者看到的是亂碼。已套過的庫不會重跑 0004，所以另有 `0007_fix_category_tree_triggers.sql`
  把兩個 trigger DROP + CREATE 一次（邏輯逐字相同，只修訊息文字）。

### 修復（存款存入被記成支出 —— 舊 TODO a0）
- `StatementImporter` 不再只照金額正負號定 `entry.kind`：存款對帳單（`StatementKind.Deposit`）的正數是「存入」
  → `income`，負數是扣款 → `expense`（卡帳/繳費單那兩側口徑不變，照單據種類定方向）。
  之前薪資 104,181 入帳會變一筆 `expense`，`DerivedBalancesCore` 把它**減掉** —— 銀行餘額與淨值一起歪
  （鐵證：王道美金 8 筆明細合計 +0.50、單上餘額 0.71、derived 卻算出 −0.50）。
- `migrations/0009_deposit_statement_kind.sql` 把既有入庫資料照同一規則翻正：只動 `source='import'` 且帳戶是
  `bank` / `term_deposit` 的列（手動記的、卡帳的、其它種類帳戶一筆都不動）。負額的 `refund` 翻成 `expense` + 正額
  —— `refund` 在 derived 走「減 amount」，負負得正會把一筆提款變成加錢。分類一起清掉：那些是照「支出」標的，
  方向反了留著就是錯帳（重標用「建議分類」）。`source_line_hash` **不重算**（它照單上的金額算，重算會讓重匯把同一筆再插一次）。
- 測試把兩條路徑釘住：同一筆 99.00 在存款單上正數進 `income`、負數進 `expense`；兆豐存款單 40,000/5,000 各一行。
- 副作用：`--fakein`（造假收入）不再是「這庫全是支出」的代償，它現在只是報表 income 那側的測試資料；
  收支報表與分類帳的收入塊終於會有真數。
- 套用情況（2026-11-20）：**本機 `data\dfinance.db` 已跑過 0009**；要回就拿遷移前的
  `data\backups\dfinance-20261119-172625.db`。本機淨值 52,930,106 → 52,932,094、`--balances` 全部對得上。
  哈希實測 185 筆 import 列 = 185 個不同哈希（遷移後重匯不會插兩份）。
  存款單的負額就是提款/扣款，不再進「有負額」那條警告（它只對卡帳有意義）。

### 修復（2026-11-19 程式碼審查那 20 項，逐條對應）
- **批次匯入不再凍住整個視窗**：`Task.Run` 交回去的是一條還沒執行的 LINQ，真正解析與寫庫發生在
  字串 join 那一刻（＝UI 執行緒）。現在在 `Task.Run` 裡面用 `foreach` 跑完，而且匯入**自己開一條連線** ——
  整個 app 共用一條 `SqliteConnection` 不是執行緒安全的，匯入中點主頁/切分頁就會撞（WAL 讓讀與寫互不擋）。
- **留底 PDF 改在交易之前寫**、路徑與交易同一個交易落地。以前是 commit 之後才寫檔，寫檔失敗會留下
  「有帳、沒留底」，而重匯又被 `sha256` 擋死 → 那份單子永久沒有原始單據可查。
- **格子裡改分類會立刻更新顯示**（`Row.CategoryId` 只存 Id、TextBlock 綁的是 `Category`，以前只 `Raise` 不改值，
  資料存進去了但格子還是舊字串，看起來像沒儲存）。
- 「＋ 記一筆」與盤點後的 `adjustment` 現在會 `Touch()` —— 這是唯一漏推版本號的寫入，其他開著的帳戶分頁會一直顯示舊資料。
- **已有交易的帳戶鎖定「幣別」與「類型」**：餘額是按「帳戶幣別 × 匯率」折算的，台幣帳戶改成 USD 會讓
  既有交易被當成美金再乘一次匯率（淨值瞬間幾十倍）而且看不出從何時開始錯。要改請新建帳戶、把舊的歸檔。
- **繳費單的預設付款帳戶不會再選到已歸檔帳戶**（那條路徑沒驗 `is_archived`；記進去等於悄悄從淨值消失）。
- **明細溢出到下一頁不會整頁漏掉**：新增 `StatementTable.DetailRows()` ＝表頭頁表頭以下 ＋ 之後每頁全部。
  原本 9 支 parser 用「Top 比第一頁表頭小」當條件，而表頭不一定會重複（富邦三份都只出現一次）；
  中信卡與匯豐卡更嚴，是 `r.Page == header.Page`（第 2 頁整個不讀）。
- **金額的負號收 U+2212 與全形減號**（字型常輸出這兩個）。以前只有王道/台新收，聯邦與共用那組只收 ASCII 連字號 →
  繳款/退款列會無聲消失，只剩「合計與應繳差 N 元」這種看不出原因的警告。順帶把四份金額 regex 收斂成 `PdfText` 一份
  （聯邦/繳費單那兩份是複製品；台新/王道形狀規則真的不同，留著並在註解寫為什麼）。
- **表頭找不到欄位改成記警告**（`Row.Label()/LabelLike()/MissingLabels()`）。原本 7 支 parser 用 `.First()`，
  銀行把「地區」改成「國家」這種事會讓整份檔案一筆都進不去，而且 UI 只顯示一行英文例外。
- 淨值總表的「⚠ 含 N 筆幣別不符的列」補上 `status IN ('cleared','imported')` —— 這是唯一沒走 `LedgerStore.Live`
  口徑的財務查詢，標成忽略/重複的列照樣被計進去。
- 概況的「待分類」改成與清單同口徑（清單刻意保留沒有消費日的列，KPI 卻把它們濾掉 → 會出現「待分類 0 筆」
  而下面躺著幾列紅色 `?` 等補日期）。金額仍只加落在區間內的列。
- **軟刪過的檔案可以重匯**：`document` 的插入改成 `INSERT … ON CONFLICT(sha256) DO UPDATE`，
  沿用那一列並重置 `is_deleted/imported_at/superseded_at`。以前 `sha256` 是全域 UNIQUE，
  等「刪除這次匯入」做出來，同一份 PDF 第二次匯入會直接撞 UNIQUE。
- 「重設」按鈕現在會重查（本來就在預設值的控件不會發事件，清空了搜尋框卻還卡在上次的结果）。
- `--uismoke`（或 `DFINANCE_NO_BACKUP=1`）**不再吃掉那 5 份備份額度** —— 跑幾輪測試就把真正要救命的備份擠掉了。
  `devcheck` 刻意仍然備份：`--autocat` 那種大量改資料前，要的就是那份備份。
- 匯入進度訊息「辨識中：…」進語系檔（`Imp_Identifying`），原本是在 `dFinance.Data` 裡硬編碼繁中。
- `IStatementParser` 的註解把工廠類別名改對（`StatementParserFactory` → `ParserFactory`）。

### 修復
- 存款單/繳費單的**跳檔案去重**不再一律關死：`StatementImporter.FindExisting` 改成看「哈希裡有沒有可靠身份」
  （`crossFile`）決定範圍 —— 卡號/帳號/戶號有其一就跳檔案比，都沒就只在同一份檔案內比。
- **海外消費現在會警告**（富邦/匯豐）：兩者的金額取自「台幣金額」欄、幣別取自外幣欄，海外消費又還沒有樣本可驗，
  直接入帳會變成「外幣幣別 × 台幣金額」（淨值可能被匯率重算）。`orig_amount_minor`/`orig_currency` 明確標成未實作。

### 移除
- **`ParsedTransaction.Kind`（charge/credit）拿掉了**：15 個 parser 寫的公式全是 `Amount` 正負號的複製，
  匯入端從頭到尾不讀它，但卡帳的 `charge`=消費、存款單的 `charge`=存入語意相反 —— 不帶新資訊又會被讀錯的欄位就是陷阱。
  方向一律看 `Amount` 的正負號（負＝退款/支出/減帳）；`StatementParser.Version` 3 → 4。
- 沒人在用的東西：`WordBox.X`、`PageData.PageCount`、`Money.AssetScale`/`ToAssetUnits`/`FromAssetUnits`
  （只有它自己的測試在驗它；投資三表還在 TODO，要時再補，`dFinance-db-design.md` 已標「尚未實作」）。

### 測試
- **新增 `ParserContractTests`：跨 parser 的輸出契約**（兩個 Theory 跑遍兩個樣本夾的所有 PDF）。
  各銀行一支測試鎖的是「那份單解析得對不對」，這裡鎖的是「輸出像不像同一個 `ParsedStatement`」：
  類名尾綴要是 Card/Deposit/Bill（匯入端 `Classify` 吃這個）、0 筆一定要伴隨警告、商家/原始行不準為空、
  `Date` 不准留 `default`、餘額不准缺 `as_of`，外加**每個幣別 token 都要能在 `currency`/`aliases` 對到代碼**
  （對不到匯入端會退回基準幣 —— 非台幣的錢被當成基準幣是百倍級的錯）。
- 繳費單身份與重匯範圍：`NoticeBillTests.繳費單要抓到戶號當作可靠身份`（三份真實單子）、
  `ImportTests` 新增「同一張繳費單重下載不會記兩次 / 同月同額不同戶號各存一筆 / 抽不到戶號宁願存兩份」。
- **測試不會再「空白通過」**：`dFinance.Tests` 升到 xunit v3（4.0.3），樣本找不到改成 `Assert.Skip(原因)`，
  OCR 模型沒放走 `Samples.RequireOcr()` 跳過。之前是 `if (s is null) return;`（49 處）—— 樣本被改名/搬走時
  測試全部變成綠燈等於沒測（2026-11-19 樣本搬到 `statement_old` 就燒過一次，當時 40 多個測試是空的）。
  `Samples` 現在同時找 `D:\dennys\statement` 與 `statement_old`（可用 `DFINANCE_STATEMENT_DIR` 覆寫）。
- 新增 `PdfTextRowsTests.cs`：不用 PDF 就能鎖住兩種「靜靜漏列」的坑（跨頁明細、各種負號）——真實樣本剛好都沒有。
- 这一輪新增/改寫的測試（共 258 條，README 原本寫 83）：
  `ImportTests.存款單的正數是存入不是支出負數才是支出`（薪資被記成支出就是這條）、
  `ImportTests.存款單的多個帳號分別歸到自己的帳戶` 改成照 `kind` 数（金額存正數）、
  `LedgerStoreTests` 加四條：概況按幣別折算、沒匯率的不計入金額但報筆數、
  沒有消費日的列要進折線最後一點（跟淨值同一個數）、人工輸入幣別跟帳戶不同会被挡（轉帳兩個帳戶同幣別）、
  `ForeignCurrencyTests` 加兩條鎖拆帳戶：目標帳戶已存在要沿用（以前撞 UNIQUE 整個按鈕炸掉）、
  名字被别的幣別占用时要另取名（不會把美金列塞进人民幣帳戶）、
  `PivotTreeTests.整年加起來是零但每月有數的還是要進表`。

## 2026-11-19

### 新增
- **OCR（圖片式帳單）**：匯豐卡、中信卡、中信存款單的中文是被畫成小圖，文字層讀不到 →
  `RapidOcrNet`（PP-OCRv5 ONNX，CPU、完全離線）整頁柵格化辨識後換算回 PDF 座標，與文字層的列合併，
  既有的 `StatementTable`/`ValueFor` 照樣能用。**金額與日期只取文字層**，OCR 只補標籤/表頭/商家。
  結果依 sha256 快存在 `data\ocr\`；模型不放版控（`data\ocr_models\` 或 `DFINANCE_OCR_MODELS`），
  沒放就只警告。中信存款單另有「上期結餘 + Σ交易 = 期末餘額」校驗。
  新 parser：`HsbcCardParser`、`CtbcCardParser`、`CtbcDepositParser`；測試沒放模型會跳過。
- 解析時會報進度（「OCR 辨識中… 第 x/y 頁」，批次匯入也報檔案名），幾十秒不會看起來像卡住。
- **幣別與匯率通用化**：匯率改存**幣對**（`exchange_rate(from_ccy, to_ccy, rate_date, source)`，「1 from = ? to」）、
  基準幣進 `settings.base_currency`、幣別字典加 `scale`（顯示小數位）與 `aliases`（匯入用的原文），
  並做「一個帳戶一種幣別」（匯入鍵值含幣別）。新增「工具 → 幣別…」視窗：基準幣、幣別字典、匯率表、
  人工填／Yahoo 抓匯率／測試連線／proxy、把幣別不符的列拆到對的帳戶。
- 兆豐**存款對帳單** parser（一份含多帳號多幣別，逐列逐幣別歸戶）。
- 分類改成**任意深度的樹**（父分類任意層級，報表與下拉跟著走）。

## 2026-11-18

### 新增
- 首頁改成 dashboard：左邊淨值明細（銀行 → 信用卡 → 其它）、右上每月總資產折線（ScottPlot）、
  右下未來帳單；功能表與可攜的「最近開啟」（`data\recent.json`）。
- 帳本分頁可 drill-down（點分類帳的月欄開一個帶篩選的分頁）＋常駐「分類帳」分頁。
- 第一版：PDF → 解析 → SQLite → WPF 帳本（錢用 `long` 的 1/100 元、日期 TEXT、自動備份 5 份、可攜 `data\`）。
- **匯入落庫**：留底 PDF（原樣、sha256 檔名）、來源血統、檔案級去重（`document.sha256`）、
  列級去重（`source_line_hash`；只有信用卡跳檔案比，存款單/繳費單只在同一份檔案內比，見 `dFinance-db-design.md`）、
  重匯保留人工改過的列、同帳期取代與孤兒標記（只對信用卡做）。UI 有「匯入檔案」與「批次匯入資料夾」，
  命令列有 `devcheck <資料夾> - --import`。
- **帳本主頁**（`LedgerTab` 一個控制項兼主頁與帳戶分頁）：篩選（帳戶/日期/分類/狀態/商家）、
  概況（本月收支/淨額/待分類/淨值/資產/負債/卡未繳/未折算）、格子裡改分類商家備註消費日、
  忽略/重複/軟刪除/還原、右側來源血統＋開留底 PDF、底部警示列（孤兒/疑似重複/餘額不符）。
- **帳戶分頁（multi-tab）**：同一帳戶只開一個、開著不重查（靠 `LedgerStore.DataVersion` 決定要不要重載）。
- **帳戶管理 UI**（`AccountsDialog`，主頁按「帳戶…」）：新增/編輯/歸檔帳戶（`cash`/`wallet`/外幣帳戶都能自建）、
  `opening_balance`+`opening_date`、信用卡的 `credit_limit_minor` 與 `card.autopay_account_id`。
  歸檔是軟刪（真刪會斷血統），主頁帳戶下拉也把已歸檔的列出來（標清楚）。
- **餘額盤點**：`account_balance(source='manual')` 的輸入有了，`BalanceMismatches()` 看「每個帳戶最近一次快照」
  （statement 與 manual 都算，同日以盤點為準）；對不上會問要不要記一筆 `adjustment` 補齊（調帳走調整、金額唯讀）。
- **淨值明細（總表）**：主頁「淨值明細」展開就是每個帳戶的餘額/折算後/計不計入淨值。
- **分類種子**（`0002_seed_category.sql`）＋「＋ 分類」；之前 `category` 是空的，「改分類」是改個空下拉。
- **匯率有人寫了**：`0003_seed_rate.sql` seed USD 32.9（`OR IGNORE`，人在 UI 改過就不會被蓋），帳戶管理裡可改可清空
  （留空＝故意不計入淨值）。
- `--uismoke` / `devcheck/smoke.ps1`：不用人按就能驗主頁；`LocalizationTests` 擋缺譯/空字串/佔位符對不上。

### 基礎（第一版就定死的決定，理由在 `dFinance-db-design.md` 與 README）
- 錢用整數 `_minor`（1/100 元）儲存、日期 TEXT、CHECK/FK 真的在擋（連線要 `Foreign Keys=True`，SQLite 預設不啟用）。
- 金額唯讀（調帳走 `adjustment`）、非台幣帳戶不計入淨值、餘額用 derived 而快照只做校驗。
- portable：資料在 `data\`（`DFINANCE_DATA` 可覆寫），已 gitignore；`.env`、樣本 PDF、`data\` 不進版控，資料庫不加密。
