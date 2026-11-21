using System.Windows;
using Classify = dFinance.Classification;
using dFinance.Data;
using dFinance.Data.Localization;

namespace dFinance;

/// <summary>
/// 未分類商家的分類建議：可勾選套用、改分類或取消。這一視窗只准跟 <see cref="SuggestionService"/> 說話
/// —— 正規化、群組、規則比對全部在 <c>dFinance.Classification</c> 裡（自己在這裡算 hit.Count 就是當初 B2 的所在地）。
/// </summary>
public partial class SuggestDialog : Window
{
    public static string NoCategory => Loc.T("Suggest_NoCategory");        // 不是 const：會跟著語系變，判斷一律用這個 property

    readonly long? _accountId;
    readonly SuggestionService _svc;

    public sealed class SuggestRow
    {
        public required Classify.SuggestionRow Data { get; init; }
        public required string Merchant { get; init; }
        public required int Count { get; init; }
        public required string Source { get; init; }
        public bool Apply { get; set; }
        public string Category { get; set; } = NoCategory;
        /// <summary>這一側的最底層分類（含「（不標）」）。混列收支會讓報表的塊小計與總計行對不上（B7）。</summary>
        public required List<string> CategoryOptions { get; init; }
        public required IReadOnlyDictionary<string, long> Ids { get; init; }
        public long? Chosen => Ids.TryGetValue(Category, out var id) ? id : null;
    }

    public List<SuggestRow> Rows { get; }
    public int Applied { get; private set; }

    public SuggestDialog(LedgerStore store, string? from = null, string? to = null, long? accountId = null)
    {
        _accountId = accountId;
        _svc = new SuggestionService(store);
        var output = _svc.Preview(from, to, accountId);

        // leaf 顯示的是全路徑（「餐飲／外送」），規則回的也是 leaf id ⇒ 兩邊對得上，不需要再拿名字去猜。
        var sides = new Dictionary<string, (List<string> Names, Dictionary<string, long> Ids)>
        {
            ["expense"] = Leaves("expense"), ["income"] = Leaves("income"),
        };
        (List<string>, Dictionary<string, long>) Leaves(string kind)
        {
            var items = store.Categories(kind, leavesOnly: true);
            var ids = new Dictionary<string, long>();
            foreach (var c in items) ids[c.Name] = c.Id;                     // 同一側不重名（UNIQUE 擋了），不用怕覆寫
            var names = new List<string> { NoCategory };
            names.AddRange(items.Select(c => c.Name));
            return (names, ids);
        }

        Rows = output.Rows.Select(r =>
        {
            var (names, ids) = sides[r.Kind];
            var suggested = r.PreApply && r.CategoryId is long cid ? names.FirstOrDefault(n => ids[n] == cid) : null;
            return new SuggestRow
            {
                Data = r,
                // 同正規化鍵的原文變體已經合成一列（B5）：讓人看得见「這幾列其實是同一間店」。
                Merchant = r.NoMerchant ? Loc.T("Store_Unnamed")
                           : r.RawVariants.Count <= 1 ? r.RawVariants.FirstOrDefault() ?? ""
                           : Loc.F("Suggest_Variants", r.RawVariants[0], r.RawVariants.Count - 1),
                Count = r.Count,
                Source = r.Source switch
                {
                    Classify.SuggestionSource.Rule => Loc.T("Suggest_Source_Rule"),
                    Classify.SuggestionSource.Advisor => Loc.T("Suggest_Source_Advisor"),
                    _ => "",
                },
                CategoryOptions = names,
                Ids = ids,
                Apply = suggested is not null,                              // 只有 seed/user 規則命中才預勾
                Category = suggested ?? NoCategory,
            };
        }).ToList();

        DataContext = this;
        InitializeComponent();
        Title = from is null && to is null ? Loc.T("Suggest_Title") : Loc.F("Suggest_TitleRange", from ?? Loc.T("Suggest_Earliest"), to ?? Loc.T("Suggest_Latest"));
        ApplyButton.Content = Loc.T("Suggest_Apply");
        CancelButton.Content = Loc.T("Common_Cancel");
        HintText.Text = Loc.F("Suggest_Hint", Rows.Sum(r => r.Count), Rows.Count);
        BrokenText.Text = output.Broken.Count == 0 ? ""
            : Loc.F("Suggest_Broken", output.Broken.Count) + string.Join("、", output.Broken.Take(3).Select(b => b.Pattern));
        ApplyCol.Header = Loc.T("Suggest_Apply");
        MerchantCol.Header = Loc.T("Common_Merchant");
        CountCol.Header = Loc.T("Suggest_Count");
        SourceCol.Header = Loc.T("Suggest_Source");
        CategoryCol.Header = Loc.T("Common_Category");
    }

    void Apply_Click(object sender, RoutedEventArgs e)
    {
        var failed = new List<string>();
        foreach (var r in Rows)
        {
            if (!r.Apply || r.Chosen is not { } categoryId) continue;
            try { Applied += _svc.Apply(r.Data, categoryId, _accountId); }
            // 批次寫入一定會有「database is locked」那種零星失敗：逐列成敗、講得出是哪一列，
            // 而且要留在視窗上（關了就是一格看起來改好了、其實沒改的格子）。
            catch (Exception ex) { failed.Add($"{r.Merchant}：{ex.Message}"); }
        }
        if (failed.Count > 0)
        {
            MessageBox.Show(Loc.F("Suggest_ApplyFailed", string.Join("\n", failed)), Loc.T("Common_AppError"));
            return;
        }
        DialogResult = true;
    }
}
