# 語系（執行期切換，不用重開程式）

> 這頁是實作細節與坑；重點規則已進 `AGENTS.md`（「不能破壞的規則」與「UI 現況」兩節）。
> 支援：`zh-TW`（預設）、`en-US`、`zh-CN`、`ja-JP`。工具 → 語言… 選了立刻換。

## 放在哪

- 字串目錄在 **`dFinance.Data/Localization/Strings*.resx`**，不在 UI 專案。理由：`LedgerStore` 的狀態/型別/分組
  與 `StatementImporter` 的匯入 log 也是給人看的，放同一份才不會兩套目錄對不起來。
- 預設語系 = 繁體中文，由 `dFinance.Data.csproj` 的 `<NeutralResourcesLanguage>zh-TW</NeutralResourcesLanguage>` 宣告。
  **不要**再交一份 `Strings.zh-TW.resx`（會被 neutral 吃掉）；也別把 neutral 改成 `zh-CN`，那樣 `zh-CN` 的
  satellite 會被跳過。
- 選好的語言存在 `settings.ui_language`（跟資料庫走，不用 AppData —— 可攜性硬規則），啟動與換庫時自動套用。

## 怎麼寫

| 場所 | 寫法 |
|---|---|
| XAML | `Content="{Binding [Common_Save], Source={StaticResource Loc}}"` |
| code-behind / Data | `Loc.T("Common_Save")` |
| 帶參數 | `Loc.F("Ledger_Rows", rowCount)`，resx 裡寫 `{0}` |

`Loc` 這個 binding source 是 `App.OnStartup` 塞進 `Application.Resources` 的 `LocalizationManager.Current` 單例。

- **不准用 `{x:Static Strings.Save}`**：那只在載入時取一次值，改了 `CurrentUICulture` 現有控件也不會更新，
  最後只能叫人重開程式。
- **自訂 MarkupExtension 傳回 `Binding` 在這邊不管用**：編譯好的 BAML 不會展開它，會把 `Binding` 物件當成值
  直接指派給屬性（`'Binding' 對屬性 'Text' 來說並非有效的值`）。所以只能老實寫綁定，語法長一點是代價。

## 會自動更新 / 不會自動更新

- XAML 宣告的文字（`Content`、`Header`、`Text`、`ToolTip`、資料欄標題）靠 `Item[]` 通知**會**自己換。
- **code 裡一次指派出去的不會**：`Columns.Add(new DataGridTextColumn { Header = ... })`、篩選下拉的
  `ItemsSource`、`plot.Title(...)`。這些靠 `MainWindow.RebuildForCulture`（訂閱 `LocalizationManager.CultureChanged`）
  重建分頁。drill-down 分頁帶篩選條件，換語系時直接關掉。
  → 新增這類控件時：要么掛進重建路徑，要么改成綁定，否則切語言會留半中半英。
- 判斷分支不准用顯示字串：日期預設用 `DatePreset(Mode, Label)`、報表欄位靠 `SortMemberPath` 認身、
  `SuggestDialog.NoCategory` 是 property 不是 `const`（`const` 會在編譯期固定成繁中）。
- 同一道理，`LedgerStore.AccountTypes` 是 **property 不是 `static readonly` 欄位**：欄位会把標籤固定在
  第一次取用時的語系上。

## 語系不准動的東西

- `ChangeCulture` 只動 `CurrentUICulture`（＋ `DefaultThreadCurrentUICulture`），**`CurrentCulture` 一律不碰**。
  它一起改的話，碰上 de-DE／fr-FR 那種小數點是逗號的語系，沒帶 `InvariantCulture` 的 `decimal.Parse`
  （例如 `FederalCardParser.ToDecimal`）會把金額解錯一百倍。
- 顯示格式固定不變 culture：金額一律走 `Money.Format(minor, scale)`（已是不變 culture），
  不准在 UI 自己 `ToString("N0")`；日期存 `yyyy-MM-dd`、顯示 `yyyy/MM/dd` 都寫死格式。
- **`dFinance.Parsing` 的字串不本地化**：parser 的 warning 照原文進匯入 log —— 跟「parser 不准碰資料庫」
  同一個分層道理（Parsing 不參考資源檔）。要給人看的說明在 UI 層加；`dFinance.Data` 的驗證訊息則走語系檔。

## 加一個語系

1. `LocalizationManager.Languages` 加一條（代碼 + 該語言自己的寫法，語言名不翻譯，否則切換後找不到自己要的）。
2. 交一份 `Strings.<culture>.resx`，**key 必須跟 `Strings.resx` 完全相同**。
3. 跑 `dotnet test` 與 `--uismoke`。

缺譯在執行期是**悄悄退回繁中**（`ResourceManager.GetString` 缺 key 才回 null），所以檢查全靠這兩條：

- `dFinance.Tests/LocalizationTests.cs` —— 各語系 key 有齊、不能是空字串、`{0}` 佔位符要對得上、
  不准整份跟原文一模一樣（忘記翻）。
- `dFinance.exe --uismoke <檔>` —— 四種語系各換一次，確認選單文字真的跟著換（不是只有沒炸），
  並檢查有沒有 `!Key!`（key 打錯字才會出現這個標記）。
