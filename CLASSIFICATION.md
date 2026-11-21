# CLASSIFICATION.md — 商家→分類 建議子系統設計

> 狀態：**Step 1 已實作（2026-11-21）**，Step 2（規則網格）與 Step 3（種子補漏、glob 重寫 `?險`）未動。
> 實作與本檔不一樣的地方一律記在 **§12**（契約以程式碼為準）。
> 現有程式碼：`dFinance.Classification/MerchantClassifier.cs`＋`Contracts.cs`（純函式，對外只有 `Run`）、
> `dFinance.Data/SuggestionService.cs`（UI 唯一门面）、`dFinance.Data/LedgerStore.cs` 的
> `MerchantFacts` / `CategoryNodes` / `ClassifyRules` / `SetCategoryForGroup`、`dFinance/SuggestDialog.xaml.cs`（綁定＋按鈕）、
> `dFinance.Data/migrations/0011_classify_rule.sql`（表＋新 leaf＋種子）。
> 這份設計改的是「建議怎麼來」，**不碰錢的算法**：錢的口徑永遠只有 `LedgerStore.Live`。

---

## 0. 為什麼要重做

現在是一支寫死在 dll 裡的靜態關鍵字表 + 建議視窗自己猜子類，四個後果（都在這台庫上驗證過）：

1. **規則不可增減**：使用者不能加 `統一超商`、不能關掉判錯的詞，只能等改程式。
2. **8/10 條規則永遠產不出東西**：規則回的是**頂層名**，視窗只在「該頂層恰好一個子類」時才預勾
   （`SuggestDialog.xaml.cs:49` `known = hit.Count == 1`）。餐飲/交通/購物/水電瓦斯/通訊網路/訂閱軟體/醫療保健/居住
   全都已拆子類 ⇒ 認到了也被靜默丟掉，畫面跟「完全沒認到」一模一樣。
   實證：`街口電支－中華電信TAIPEI`(2 筆)、`代收－全盈支付－全家便利商店`(67 筆)、
   `街口電支－新竹縣路邊停車費TAIPEI`(2 筆) 至今 `category_id IS NULL`。
3. **沒有正規化**：`代收－ＳＵＫＩＹＡ 新竹關新店`、`代收－全支付ＴＷＱＲ跨機構購物交易` 判不出來（全形英文），
   `7-11` 與 `7-ELEVEN` 要各寫一條，`A- 中油－…` 跟 `中油－…` 是兩列。
4. **渠道詞混在商家裡**：`代收` 89 筆、`全盈支付` 67、`連加` 37、`網際轉` 25、`街口電支` 6 —— 這些是「怎麼付的」，
   不是「跟誰買」，既不該產生建議，也不該讓同一間店拆成好幾列。

**「非中文怎麼辦」的答案**：關鍵字比對在機制上語言中立，真正的障礙是**同一個商家的字面變體**與**覆蓋率**，
所以解法是（a）正規化、（b）規則可增減、（c）從既有帳學習（不需要認識那串字，只要認得這串字）。
不是「支援中文/英文」這種開關。

---

## 1. 已拍板的決定（本檔就是依此寫成）

| # | 決定 |
|---|---|
| 1 | catch-all leaf 統一命名 `X／其他`；餐飲那個叫 **`餐飲／其他餐飲`**（不叫 `餐飲／餐飲`：報表展開後父列與子列會印兩個「餐飲」，跟小計列難以區分） |
| 2 | 八個已拆子的頂層**全部**補 catch-all leaf（它們現在都是「規則有關鍵字但沒有可標的 leaf」） |
| 3 | 規則支援 `glob`（`*`/`?`）；順帶讓 `?險` 那種寫法變成有意義的規則。放在 Step 3 |
| 4 | learned（從既有帳推）**預設開**，但**不預勾**並顯示來源筆數；門檻：同一商家鍵 **≥2 筆且全部一致** |
| 5 | 「忽略這個商家」（把它趕出建議清單）**v1 不做**，用標成 `X／其他` 代替 |
| 6 | 套用建議**不再寫 `edited_at`**。查證過：重匯的 UPDATE 分支（`StatementImporter.cs:173`）本來就不寫 `category_id`，所以不寫 `edited_at` 不會丢分類，反而讓 parser 修正後金額/日期/商家能刷新。AGENTS 那條「`edited_at` 有值的列只准更新 `source_raw`/`source_page`」不變，變的是「誰有資格寫它」：只有人在帳本格子裡親手改才算 |
| 7 | `priority` 以**數字欄**給使用者編（不用上下移按鈕） |
| 8 | 新模組用**獨立 csproj** `dFinance.Classification`（編譯期擋住「分類邏輯偷偷去碰 DB」） |
| 9 | 規則**不跨資料庫共用**（`classify_rule.category_id` 是本庫分類的外鍵，跨庫要處理 id 對不上，代價不成比例）。要搬就靠規則網格手動重打 |
| 10 | 「（無商家）」那批**繼續進建議清單**，但群組鍵改用 null 判斷，不靠本地化 label 往還 |
| 11 | LLM（llama.cpp + Qwen 1B 那類）**這輪只留一個介面洞**，不做 HTTP、不做設定頁、不做 UI。真做時是另一輪、另一支實作 |
| 12 | 存款單側的轉帳（`網際轉`、`…ACH`）**這輪不做**，記在 §8 N2/N3 |

---

## 2. 模組邊界

```
dFinance.Parsing        （不動；不引用 Classification）
dFinance.Classification （新：net8.0，零 NuGet 相依，不碰 SQLite / WPF / resx / HttpClient）
dFinance.Data           （規則 CRUD、由 entry 推 learned、編排 SuggestionService、網路 I/O）
dFinance (WPF)          （綁定與按鈕；只准跟 SuggestionService 說話）
```

| 模組 | 准做 | 不准做 |
|---|---|---|
| `dFinance.Classification` | 字串正規化、規則匹配、學習門檻判定、prompt 組裝與 JSON 解析（吃字串吐記錄） | `Microsoft.Data.Sqlite`、`System.Windows`、`Loc`/resx、`HttpClient`、讀 `settings` |
| `dFinance.Data` | `classify_rule` 遷移與 CRUD、由 `entry` 推 learned 的 SQL（只 dump `EntryFact`）、實作 `IMerchantAdvisor`、`SuggestionService` | 在建議邏輯裡算錢、自己再寫一份比對或正規化 |
| `dFinance` (WPF) | 綁定、按鈕、`--uismoke` 掛點、resx 文案 | 自己拼規則比對、自己查 `classify_rule` |
| `dFinance.Parsing` | 不動 | 不准引用 Classification。`LooksLikePayment` 留在 Parsing —— 它決定**錢的方向**（transfer vs refund），不是分類 |

`MerchantClassifier.cs` 從 `dFinance.Data` 搬到 `dFinance.Classification`，**靜態關鍵字陣列刪掉**（改讀 DB 種子）。

---

## 3. 介面（就這幾個，不多做）

### dFinance.Classification（pure，全部可單測）

```csharp
public enum RuleMatch  { Contains, Exact, Glob, Ignore }        // Ignore ⇔ CategoryId == null
public enum RuleSource { Seed, User, Learned, Advisor }

public sealed record ClassifyRule(long Id, string Pattern, RuleMatch Match, long? CategoryId,
                                  int Priority, bool Enabled, RuleSource Source, string? Note);

/// **整個模組只對外開這一個 function。** 正規化、按正規化鍵群組、剝 ignore 詞、比對、學習門檻、
/// leaf 白名單檢查與 id→path，全部住在它裡面（所以 Data 層不需要再拿正規化自己做群組，B5 由結構上不會兩邊不一致）。
public static class MerchantClassifier
{
    public static ClassifyOutput Run(ClassifyInput input);
}

/// Input：四個清單，全部是「庫的純事實」—— 沒有 DB 連線、沒有語系、沒有網路。
public sealed record ClassifyInput(
    IReadOnlyList<EntryFact> Entries,        // 帳本事實 dump：商家原文、kind、已標的 categoryId（未標 = null）、筆數
    IReadOnlyList<ClassifyRule> Rules,       // classify_rule 全表（含 ignore 詞）
    IReadOnlyList<CategoryLeaf> Leaves,      // id → 全路徑「餐飲／外送」，同時是 leaf 白名單
    ClassifyOptions? Options = null);        // MinLearnVotes = 2、UseAdvisor = false

public sealed record EntryFact(string? Merchant, string Kind, long? CategoryId, int Count);
public sealed record CategoryLeaf(long Id, string Path, string Kind);

/// Output：給 UI 綁的行 + 給規則網格的紅標。
public sealed record ClassifyOutput(IReadOnlyList<SuggestionRow> Rows, IReadOnlyList<BrokenRule> Broken);

public enum SuggestionSource { None, Rule, Learned, Advisor }
public sealed record SuggestionRow(
    string MerchantKey,                       // 正規化鍵（同鍵的原文變體已合併進這一行 → 修 B5）
    IReadOnlyList<string> RawVariants, int Count,
    string Kind,                              // income / expense（B7 的下拉過濾靠它）
    long? CategoryId, string? CategoryPath,   // null = 認不出；Path 給顯示用
    bool PreApply,                            // 只有 user/seed 規則命中才 true（learned/advisor 一律 false）
    SuggestionSource Source, long? RuleId);

public enum BrokenReason { TargetNotLeaf, CategoryGone, IgnoreAteWholeString }
public sealed record BrokenRule(long RuleId, BrokenReason Reason, string Pattern);
```

內部實作（`internal`，不對外）：`MerchantText.Normalize`（NFKD → 去 combining marks → 只留 Letter|Digit →
ToLower(InvariantCulture)，用 .NET 內建的 `NormalizationForm`，不新增套件）、學習門檻判定。
測試照樣打得動（餵 `ClassifyInput`）；真要直接測正規化就 `InternalsVisibleTo`。

### dFinance.Data（實作端）

```csharp
/// 往外面 call HTTPS 的那條路，只留這一個洞（也只這一個方法）。預設 NullAdvisor 回空字典 ⇒ 行為等於「沒有 AI」。
/// 不要 IsAvailable 之類的屬性：要不要顯示按鈕由 Data 讀 settings.llm_enabled 決定，不是問 advisor。
public interface IMerchantAdvisor
{
    /// in：③④ 之後仍認不到的商家（只有商家鍵/原文/幣別/發卡機構，**不准有帳號與 source_raw**）
    /// out：商家鍵 → leaf id（對不上 Leaves 白名單的一律丢）
    IReadOnlyDictionary<string, long> Suggest(IReadOnlyList<MerchantHint> unknowns, CancellationToken ct);
}
public sealed record MerchantHint(string MerchantKey, string RawMerchant, string Currency, string? Issuer);

/// Data 層門面：抓事實 dump → 叫 MerchantClassifier.Run → （有人按按鈕才）問 advisor → 寫回。
/// 它是「UI 唯一允許碰的門面」，但**不是分類契約的一部分**（規則 CRUD 就是普通 DB 存取）。
public sealed class SuggestionService
{
    public SuggestionService(LedgerStore store, IMerchantAdvisor? advisor = null);

    public IReadOnlyList<SuggestionRow> Preview(FilterScope scope);      // 不寫庫
    public int Apply(IReadOnlyList<SuggestionRow> rows);                // 經 LedgerStore，一律 EnsureLeaf

    public IReadOnlyList<ClassifyRule> Rules();
    public void SaveRule(ClassifyRule rule);
    public void DeleteRule(long id);
    public long PromoteToRule(SuggestionRow row);                        // 「存成規則」：learned/advisor → User/Exact
}
// SuggestionRow / BrokenRule 用的是 Classification 定義的那一份，這裡不再重複宣告。
// 編排順序：Preview = dump EntryFact/Rules/Leaves → Run → （要時）拿 Source==None 的行問 advisor
//            → 用 Leaves 驗證 id → 併回去標 Source=Advisor、PreApply=false。
```

---

## 4. 資料模型

### `0010_classify_rule.sql`（新檔；遷移檔只能新增，已套用的不要改）

```sql
CREATE TABLE IF NOT EXISTS classify_rule (
    id          INTEGER PRIMARY KEY,
    pattern     TEXT    NOT NULL,
    match       TEXT    NOT NULL CHECK (match IN ('contains','exact','glob','ignore')),
    category_id INTEGER REFERENCES category(id),
    priority    INTEGER NOT NULL DEFAULT 0,
    enabled     INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0,1)),
    source      TEXT    NOT NULL DEFAULT 'user' CHECK (source IN ('seed','user','learned','advisor')),
    note        TEXT,
    created_at  TEXT    NOT NULL,
    updated_at  TEXT,
    CHECK ((match = 'ignore') = (category_id IS NULL))                 -- ignore 詞不帶分類
);
CREATE INDEX IF NOT EXISTS ix_classify_rule ON classify_rule(enabled, priority, id);
```

- `category_id` 存 **id**（現在存分類名字串是 bug 之源：改名/挪層就無聲失效）。
- **不設 `UNIQUE(pattern)`**：同一關鍵字可能要分流。
- `enabled` 讓使用者關掉比刪掉安全；`source` 欄現在就把 `advisor` 放進 CHECK（留 AI 的位子，沒人會寫它）。
- 目標分類日後被拆成父類 ⇒ **不刪規則**，由 `MerchantClassifier` 回報 `BrokenRule.TargetNotLeaf`，UI 紅標 + 跳過。
- 冪等：照 `0008_seed_income_tree.sql` 的寫法（頂層先插、子類下一條 INSERT 才接得上，全部 `WHERE NOT EXISTS`）。

### 要補的 leaf（決定 1、2）

`category` 新增要守 `trg_category_tree_insert`（父子同 `kind`）。`sort_order` 接在既有段號後面。

| 新 leaf | parent | sort | 為什麼非新增不可 |
|---|---|---|---|
| `交通／停車費` | 交通 | 240 | 指定項；現有 油費/車票/出租接駁 都不是停車 |
| `餐飲／其他餐飲` | 餐飲 | 140 | 全家/超商這種「是吃但歸不到 外食/超商飲料/外送」的收斂列 |
| `購物／其他` | 購物 | 340 | catch-all |
| `水電瓦斯／其他` | 水電瓦斯 | 440 | catch-all |
| `通訊網路／其他` | 通訊網路 | 530 | catch-all |
| `訂閱軟體／其他` | 訂閱軟體 | 640 | catch-all |
| `醫療保健／其他` | 醫療保健 | 730 | catch-all |
| `居住／其他` | 居住 | 1030 | catch-all |

> 報表影響（已讀 `PivotTree` 確認）：`Build()` 只過濾「整棵都沒資料的**頂層**」，`Visible()` 不過濾子列
> ⇒ 展開父類時會看到 0 元子列。這跟現在展開 餐飲 會看到全部 3 個子類是同行為，不是新增的壞現象。

### 種子規則（`source='seed'`、`enabled=1`；比對前 pattern 也過同一套正規化（模組內部））

`priority` 小的先贏（取代現在「寫在陣列第幾行」）。

| pattern | 目標 leaf | 備註 |
|---|---|---|
| `中油`、`台灣中油`、`加油` | `交通／油費` | 既有 |
| `停車` | `交通／停車費` | 🆕 |
| `悠遊卡`、`一卡通`、`高鐵`、`台鐵`、`臺鐵`、`捷運` | `交通／車票` | |
| `Uber`、`Taxi`、`計程車` | `交通／出租接駁` | |
| `麥當勞`、`肯德基`、`星巴克`、`咖啡`、`CAFE`、`餐廳`、`小吃`、`便當`、`麵`、`飯`、`火鍋` | `餐飲／外食` | |
| `7-ELEVEN`、`7-11`、`統一超商`、`全家`、`萊爾富`、`OK超商`、`超商`、`路易莎` | `餐飲／超商飲料` | 🆕 `統一超商`(16 筆) 現在完全不中 |
| `Foodpanda`、`UberEats`、`Uber Eats` | `餐飲／外送` | 刪掉現有不會中的 `Foodpanda外送交通`（B4） |
| `台電`、`臺灣電力` | `水電瓦斯／電費` | |
| `台水`、`臺灣自來水` | `水電瓦斯／水費` | |
| `天然氣`、`瓦斯` | `水電瓦斯／瓦斯` | |
| `中華電信`、`CHT`、`遠傳`、`台灣大哥大`、`亞太電信` | `通訊網路／行動門號` | 🆕 `CHT` |
| `Hinet` | `通訊網路／固寬網路` | |
| `Netflix`、`Spotify`、`YouTube`、`Disney` | `訂閱軟體／影音訂閱` | 刪現有 `Netflix網路`（B4） |
| `Apple.com/bill`、`APPLE.COM`、`Google`、`Microsoft`、`OpenAI`、`ChatGPT`、`Adobe`、`Notion` | `訂閱軟體／生產工具` | |
| `iCloud`、`Dropbox` | `訂閱軟體／雲端儲存` | |
| `診所`、`醫院`、`掛號` | `醫療保健／門診掛號` | |
| `藥局`、`藥妝`、`康是美`、`屈臣氏`、`丁丁藥局` | `醫療保健／藥局藥妝` | |
| `momo`、`蝦皮`、`Shopee`、`PChome`、`博客來`、`Amazon`、`Yahoo購物` | `購物／網購` | |
| `Costco`、`好市多`、`全聯`、`家樂福`、`大潤發`、`愛買` | `購物／採買` | |
| `IKEA`、`宜家`、`Uniqlo` | `購物／服飾家居` | |
| `房租`、`管理費`、`物業` | `居住／房租`、`居住／管理費` | |
| `保險`、`人壽`、`產險` | `保險`（頂層就是 leaf） | **刪 `?險`**（B1；Step 3 有了 glob 之後可以用 `?險` 重寫） |
| `稅`、`罰鍰`、`規費` | `稅金`（頂層 leaf） | 庫裡 `牌照稅` 已能中 |
| `薪資` | `薪資收入／本薪` | 🆕 income 那側現在零規則（B8） |
| `利息` | `利息收入` | 🆕 |
| （收斂列，priority 很大）餐飲線索但認不到子類 | `餐飲／其他餐飲` | 讓「是吃的」至少有落點 |

### 種子 ignore 詞（`match='ignore'`、`category_id NULL`；括號內是這台庫的實測筆數）

`代收`(89)、`全盈支付`(67)、`連加`(37)、`網際轉`(25)、`信用卡`(11，只當前綴)、`現金`(9)、
`街口電支`/`街口支付`(6)、`電支`(6)、`全支付`(2)、`ＴＷＱＲ`(2)、`ＡＴＭ`(1)、`A-`、`～`、`＊`。

> `一卡通` 同時是交通關鍵字與支付渠道名 ⇒ 鐵則：**ignore 與 keyword 撞字時 keyword 贏**，
> 且 ignore 只在 pattern 出現在字串**開頭或分隔符之後**才剝（它只負責剝前綴，不是全域刪除）。這條要有測試。

---

## 5. 比對管線（`MerchantClassifier.Run` 內部）

```
raw merchant
 ① 正規化（模組內部）              NFKD、去變音符、只留 Letter|Digit、lower(Invariant)
 ①'按正規化鍵群組                   同鍵的原文變體合成一列（B5）
 ② 剝 ignore 詞                     反覆剝到不動點；剝完變空 ⇒ 退回原字串（否則整列都是渠道名的商家會整列消失）
 ③ 規則比對                          exact(user) > exact(learned) > contains/glob by (priority, id)
 ④ leaf 檢查                         目標不在 leafIds ⇒ Hit=null + BrokenRule(TargetNotLeaf)，紅標、不預勾、不隱藏
 ⑤ advisor（預設關）                 ③④ 之後仍未知的湊批丟 IMerchantAdvisor，只收既有 leaf id
```

正規化後比較一律 `StringComparison.Ordinal`，**不准用 `CurrentCulture`** —— 跟 AGENTS「語系只換 UI，不換錢與日期」
同一條線（換成 de-DE/tr-TR 不能讓比對結果變，測試要鎖）。

正規化一次救得回來的實測案例：`代收－ＳＵＫＩＹＡ 新竹關新店`、`代收－全支付ＴＷＱＲ跨機構購物交易`、
`代收－連加＊統一超商`(16)、`A- 中油－…` 與 `中油－…` 合成同一鍵、`街口電支－中華電信TAIPEI` → 能預勾 `通訊網路／行動門號`。

learned（決定 4）：
- 資料來自 `entry`（口徑照 `LedgerStore.Live`，排除 `transfer`/`refund`），SQL 只回 `EntryFact(商家原文, kind, categoryId, 筆數)`，
  正規化群組與門檻判斷都在 `Run` 裡（≥2 筆且全部一致）—— SQL 不准自己判門檻，否則口徑會分兩處。
- UI 一律**不預勾**、顯示「自動學習 N 筆」，並提供「存成規則」正則化它（不然人改一筆帳，建議就悄悄飄移）。
- 優先級排在 user exact 之後、keyword 之前：使用者明說的規則永遠贏過推導。

---

## 6. LLM 的路（這輪只做洞，不做實作）

- 本輪只交付 `IMerchantAdvisor` + `NullAdvisor`。`advisor is null` 或 `!IsAvailable` 時**行為完全不變**。
- 真做時的落點與約束（先寫死在介面文件，避免之後有人塞進比對迴圈）：
  1. 實作放 `dFinance.Data`，跟 `RateSource` 同側（網路 I/O 不進 Classification），用 `HttpClient`，**不新增套件**。
  2. 設定進 `settings`（`llm_endpoint` / `llm_model` / `llm_enabled`，預設關），
     **只有人按按鈕才發**（跟 `rate_proxy_url` + 「測試連線」同一個模式）。
  3. `--uismoke` 與測試一律走 `NullAdvisor`；照 OCR 的 house pattern（`Samples.OcrReady`：模型不在就跳過並警告）。
  4. **不准送 `source_raw`**（單子原始行有帳號/戶號）；只送 `MerchantHint`（商家原文＋幣別＋發卡機構）。
  5. 輸出**只能是既有 leaf 的 id**，送進 `leafIds` 驗證，對不上就丟 —— 不准讓模型發明分類名、更不准順手插分類。
  6. AI 來的一切是 `source='advisor'`、`enabled=0`、**永不預勾**；採納時轉成 `user`/`exact` 規則
     —— 智慧落在規則表（可重現、可審、下次免費），不存在某筆帳的狀態裡。
- Classification 裡只做可離線測的部分：prompt 組裝 + JSON 解析 + id 驗證（餵 canned 字串，不打網路）。

---

## 7. UI / 落地

- 規則編輯放進**現有** `SuggestDialog`（多加一個分頁或一顆「規則…」按鈕），純 code-behind DataGrid，
  照 `CurrencyDialog` 的寫法。理由：發現缺規則的地方就是建議清單本身，兩步內要能補完。
  **不開新視窗、不加選單項。**
- 「未分類商家」每列兩顆小按鈕：`套用`（現有）、`存成規則`（`PromoteToRule`）。
- 規則網格欄：`enabled`、`pattern`、`match`、`分類`（leaf 全路徑下拉；目標非 leaf ⇒ 紅標「會跳過」＋一句說明）、
  `priority`（數字）、`來源`（seed/user/learned/advisor）、`備註`。
- 文案全進 `dFinance.Data/Localization/Strings*.resx` × 4 語系，參數走 `Loc.F`；
  **不准 `x:Static`、不准 code 裡硬編中文**（規則內容的 pattern/note 是使用者資料，不進 resx）。
  分類名稱本身是種子資料（繁中），四種語系下都顯示中文 —— 這是現況，不在這輪解決。
- `SuggestDialog` 縮成「綁定 + 按鈕」：現在它自己載 leaf、自己算 `hit.Count`（就是 B2 的所在地）。
- `--uismoke`：`SmokeOpenSuggestDialog` 順帶建規則分頁，並跑一次「新增一條 exact 規則 → 建議從無變有」、
  「把規則目標拆成父類 → 出現紅標」。壞了會被抓到（跟 `SmokeCategoryReport` 一樣的做法）。

---

## 8. Bug 清單（先記，等下按分期修）

| # | 位置 | 現象 | 證據 | 修法 |
|---|---|---|---|---|
| **B1** | `MerchantClassifier.cs:19` `("?險")` | `?` 是實心字元，`Contains` 是 literal ⇒ 這條永不中 | 代碼 | 先刪；Step 3 有 glob 後用 `?險` 重寫 |
| **B2** | `SuggestDialog.xaml.cs:49` `known = hit.Count == 1` | 已拆子類的頂層建議**靜默失效**：不預勾、不顯示，跟沒認到無法區分 | 8 個頂層都 ≥2 子類；`街口電支－中華電信TAIPEI` 認到通訊網路卻 2 筆未分類至今 | 規則直指 leaf id ＋ `BrokenRule` 明報 |
| **B3** | `MerchantClassifier.Rules` 用分類**名**字串 | 改名/挪層就無聲失效；同名已逼出「不能用 ToDictionary」的繞法（`SuggestDialog:33-35`） | 代碼 | 存 id + FK |
| **B4** | 種子 `Foodpanda外送交通`、`Netflix網路` | 不會存在的商家名，佔著規則；若有人改成 `Foodpanda` 放交通會把 `餐飲／外送` 搶走 | 庫內比對不到任何 entry | 種子清掉 |
| **B5** | `LedgerStore.cs:1021`（群組）與 `:1380`（套用）都用**原文**商家字串 | 同一間店拆多列、要按 N 次 | `中油－…` 4+4、`游麵館` 3+2、`CHT` 兩組 | 正規化鍵群組 + `WHERE merchant IN (…)` 批次套用 |
| **B6** | 無正規化 | 全形 `ＳＵＫＩＹＡ`/`ＴＷＱＲ`、標點空白變體、`7-11` vs `7-ELEVEN`、`CAFE`/`CAFÉ` | 庫裡實測 | 模組內正規化（§5 ①） |
| **B7** | `SuggestDialog.xaml.cs:37` 下拉混列 expense+income leaf，群組又不分 kind | 能把 income 列（`薪資 倍利`）標成 expense leaf ⇒ `CategoryPivot` 的塊按 `category.kind`、最下面「收−支」行按 `entry.kind`（`LedgerStore.cs:1206`），塊小計與總計行會互相對不上。帳本格子的下拉是對的（`LedgerStore.cs:183` 照 kind），只有建議視窗漏了 | 代碼路徑 | 群組帶 kind、下拉照 kind 過濾 |
| **B8** | 種子覆蓋缺口 | `統一超商`(16)、`薪資`(2)、`利息`(2)、`FEE`/`手續費`(7/2)、`PAYPAL`(4)、`CHT` | 庫裡未分類清單 | §4 種子補（排在「使用者能自己加規則」之後，才算治本） |
| **B9** | `dFinance.Tests` | 建議分類這整塊**零測試**；`--uismoke` 只建視窗數商家數 | `ls dFinance.Tests` | §9 |
| **B10** | `LedgerStore.cs:1386`（`SetCategoryForMerchant` 寫 `edited_at`） | 批量套用建議 = 讓那批列對未來 parser 修正免疫（重匯只更新 `source_raw`/`source_page`） | 代碼 | 決定 6：套用建議不寫 `edited_at`；只有帳本格子親手改才算人工編輯 |
| **B11** | 「（無商家）」列用 `Loc.T("Store_Unnamed")` 做群組鍵再比回 null（`LedgerStore.cs:1037`、`1382`） | 語系來回換時 null 與字串會錯位 | 代碼 | 群組鍵改用 null 判斷（決定 10） |
| **N2** | 不是 bug，是隔壁的洞 | 存款單側永不做轉帳：`StatementImporter.cs:129` 要求 `kind == Card`。`網際轉`(25) 實質是帳戶間移轉，現在記 expense/income，淨值對但折線一进一出各一筆 | 庫裡實測 | 獨立一輪：要做「同戶同名帳戶配對」，比建議分類危險，**不准猜** |
| **N3** | 同 N2 的兄弟項 | `信用卡 永豐消金ACH`(3)、`信用卡 台新銀行ACH`(2)、`信用卡 北富信卡ACH`(2) 是存款單上的卡繳款，實質轉帳 | 庫裡實測 | 跟 N2 同一輪（決定 12：這輪不做） |

---

## 9. 測試

`dFinance.Tests/ClassificationTests.cs`（pure：不需 DB、不啟 WPF）
- 正規化：全形/半形、變音符（`CAFÉ`→`cafe`）、標點空白、全形破折號
- ignore：反覆剝到不動點、剝光退回原字串、**ignore 撞 keyword 時 keyword 贏**、只在開頭/分隔符後才剝
- 比對：`priority` 先贏、exact 勝 keyword、glob（Step 3）
- learned：門檻、不一致不採納、筆數來源顯示
- leaf：目標非 leaf ⇒ `BrokenRule(TargetNotLeaf)` 而非靜默 null；分類被刪 ⇒ `CategoryGone`
- 語系：`LocalizationManager.ChangeCulture("tr-TR")` / `"de-DE"` 之後比對結果不變（鎖住「語系不影響非 UI 邏輯」）
- advisor：canned JSON 的 prompt/parse，含「回一個不存在的 id」要被丢

DB 端一支（`ClassificationDbTests`）
- `0010` 跑兩次冪等（照 0008 的 `WHERE NOT EXISTS`）
- 種子 `category_id` 全部是 leaf；新 leaf 的 `kind` 與父同（`trg_category_tree_insert` 會擋，測試要證明遷移不炸舊庫）
- `Apply()` 之後 `SetCategoryForMerchant` 只動 `category_id IS NULL` 的列（不覆蓋人已標的）、**不寫 `edited_at`**

顺帶：grep 測試與 `devcheck --autocat` 有沒有硬假設種子分類的數量/名字（`--autocat` 是多桶輪流，多幾個 leaf 不會壞）。

---

## 10. 分期

| 步 | 內容 | 治好的 bug | 狀態 |
|---|---|---|---|
| **1** | 新 csproj `dFinance.Classification`（對外只有 `MerchantClassifier.Run`）+ `0011`（表＋新 leaf＋種子）+ `SuggestionService` + `SuggestDialog` 瘦身 | B1 B2 B3 B4 B6 B9 | ✅ 2026-11-21（顺手把 B5 B7 B10 B11 也做完了） |
| **2** | 規則網格（增刪改／enabled／priority／備註）+ 「存成規則」 | — | 未動（正規化群組與批次套用、不寫 edited_at 已在 Step 1） |
| 2b | learned 層 | — | ❌ **使用者決定這輪不做**：库裡分類是 `--autocat` 的假資料（全家 67 筆被拆成 3 類、統一超商 16 筆被拆成 13 類），學出來的是垃圾。重啟前先清庫重練（見 §12） |
| **3** | 種子補漏（`FEE`/`手續費`、`PAYPAL`、`CHT`…）、glob 重寫 `?險` | B8 B1(重寫) | 未動（引擎已支援 glob，只是種子還不用它） |
| **4** | 另一輪：`LlamaCppAdvisor`（HTTP 實作）**或** 存款單側轉帳（N2/N3） | — | 未動（只留了 `IMerchantAdvisor`＋`NullAdvisor` 那個洞） |

驗收每步都跑：`dotnet build dFinance.sln -c Release`、`dotnet test dFinance.Tests -c Release`、
`dFinance.exe --uismoke`（改完 UI 一定要）。Step 1 實測：建置 0 錯、**318 條測試全綠**（新增 30 條）、`--uismoke` OK。

---

## 11. 未驗證（落地前要自己補）

- 本檔是讀代碼＋**唯讀**查庫得出的，沒跑 build/test。
- 種子對應表（尤其 `超商飲料`／`網購`／`服飾家居`／`行動門號` 那幾條的歸屬）是按本機庫的商家名推的，**要人過一遍**。
- B7 的「塊 ≠ 總計行」是讀 `CategoryPivot` 代碼推出的，沒做實際標錯實驗。
- 正規化對 `NormalizationForm.FormKD` 的少數 CJK 相容字（`㈱`→`株式会社`）會展開 —— 對關鍵字比對是好事，
  但正規化後的字串**只能用來比對，不准回寫 `entry.merchant`**（單子原文要留，跟 `source_raw` 口徑一致）。

---

## 12. 實作紀錄（Step 1，2026-11-21）—— 跟本檔不一樣的以這裡為準

**契約的改動**（§3 那几個 record 實際長這樣）：

| 本檔寫的 | 實作 | 為什麼 |
|---|---|---|
| `0010_classify_rule.sql` | **`0011_classify_rule.sql`** | `0010_balance_snapshot_document.sql` 已存在。TODO 0b-1 的 `dup_cleared` 順勢改成 `0012` |
| `CategoryLeaf(Id, Path, Kind)` | `CategoryNode(Id, Path, Kind, IsLeaf)` | 要把「目標被刪」與「目標變成父類」講成兩個紅標（`CategoryGone` vs `TargetNotLeaf`），只列 leaf 就分不開 |
| `ClassifyOptions(MinLearnVotes, UseAdvisor)` | 砍了 | learned 這輪不做、advisor 要不要問由 Data 讀 `settings` 決定 ⇒ 這個 record 沒有剩餘職責 |
| `EntryFact(Merchant, Kind, CategoryId, Count)` | `EntryFact(Merchant, Kind, Count)` | `CategoryId` 只為 learned 存在。留一個填不到值的欄位等於騙人下次來填（`shortcut:` 已標在 code） |
| `RuleSource.Seed/User/Learned/Advisor` | 砍掉 `Learned` | learned 是 Run 當下從 entry 推的、不入庫；表 CHECK 留 `'learned'` 就是留一個「以後把它快取進表」的口子（口徑變兩處） |
| `BrokenReason.IgnoreAteWholeString` | 砍了 | §5 ② 說剝光就退回原字串 ⇒ 不會「壞」，這個值永遠不會被觸發 |
| `MerchantHint(MerchantKey, RawMerchant, Currency, Issuer)` | `MerchantHint(MerchantKey, RawVariants, Kind)` | 比對的輸入（`EntryFact`）根本沒有幣別/發卡機構，開個填不出來的欄位更糟。真做 LLM 時連 `EntryFact` 一起補 |
| 群組鍵 = 正規化鍵 | 群組鍵 = **剝完渠道詞的鍵** | 否則 `A- 中油－…` 舆 `中油－…` 仍是兩列（人就要按兩次），而 §5 自己拿它當「正規化救回來」的例子 |

**種子實際改的三條**（使用者拍板）：`瓦斯`/`天然氣` priority **100**、`中油` **200**（`代繳瓦斯費－中油 …` 是瓦斯不是油費，
拿它當 `ClassificationTests` 的固定案例）；🆕 `SUKIYA`/`食其家` → `餐飲／外食`（本機 3 筆，正規化後是 `sukiya`）；
規則表加 `kind`。`屈臣氏`/`康是美`、`統一超商` 照本檔（使用者確認過）。

**種子沒做的**：§4 那條「收斂列：餐飲線索但認不到子類 → `餐飲／其他餐飲`」—— 「餐飲線索」要自己編關鍵字，
編錯就是在替使用者的錢選分類。留給規則網格（Step 2）由人自己加。

**順手修掉的兩條（本檔沒記）**：
1. `devcheck --autocat` 舊寫法經 `LedgerStore.SetCategory` 寫 `edited_at`，把本機 **238/248** 筆真帳標成「人改過」
   ⇒ 以後重匯只准更新 `source_raw`，parser 修正永遠刷不到。現在 autocat 走不寫 `edited_at` 的 UPDATE，
   也不再自己拿頂層名對 leaf 路徑（那是第二份私有規則語意）。
2. `SuggestDialog.Apply_Click` 舊的批次迴圈完全沒 try/catch（全域 handler 會吞掉並彈訊息框，但已寫進一半、
   視窗留著、勾選框看起來像改好了）。現在逐列成敗，有失敗就留在視窗上。

**「代收」那 89 筆結案**（§0/決定 5 不用改）：它只是前綴，剥完之後 87 筆靠既有種子就救回來，
真正認不到的只有 `代收－全支付ＴＷＱＲ跨機構購物交易` **2 筆** ⇒ 不需要「忽略這個商家」，
那 2 筆就讓人選一次（Step 2 的「存成規則」把它固定下來）。

**庫現況（做 Step 2/learned 前要處理）**：本機 248 筆的 `category_id` 是 `--autocat` 輪流塞的假資料，
而且沒有欄位能區分人標的舆 autocat 標的（都是 `edited_at` 有值）。使用者的決定是 **砍庫重匯 PDF**。

