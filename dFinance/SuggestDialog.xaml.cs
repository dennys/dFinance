using System.Windows;
using dFinance.Data.Localization;
using dFinance.Data;

namespace dFinance;

/// <summary>未分類商家的分類建議：可勾選套用、改分類或取消。</summary>
public partial class SuggestDialog : Window
{
    public static string NoCategory => Loc.T("Suggest_NoCategory");        // 不是 const：會跟著語系變，判斷一律用這個 property

    readonly LedgerStore _store;
    readonly Dictionary<string, long> _categoryIds;
    readonly long? _accountId;

    public sealed class SuggestRow
    {
        public required string Merchant { get; init; }
        public required int Count { get; init; }
        public bool Apply { get; set; }
        public string Category { get; set; } = NoCategory;
    }

    public List<SuggestRow> Rows { get; }
    public List<string> CategoryNames { get; }
    public int Applied { get; private set; }

    public SuggestDialog(LedgerStore store, string? from = null, string? to = null, long? accountId = null)
    {
        _store = store;
        _accountId = accountId;
        // entry 只能標最底層分類，所以下拉只列 leaf；又 leaf 顯示的是全路徑（「餐飲／午餐」），
        // 而 MerchantClassifier 給的是頂層名，所以只能拿路徑去對。ToDictionary 也不能用：
        // income/expense 可以同 name，同 key 會直接 Throw。
        var leaves = store.Categories("expense", leavesOnly: true).Concat(store.Categories("income", leavesOnly: true)).ToList();
        _categoryIds = new Dictionary<string, long>();
        foreach (var c in leaves) _categoryIds[c.Name] = c.Id;
        CategoryNames = new List<string> { NoCategory };
        CategoryNames.AddRange(leaves.Select(c => c.Name));

        Rows = store.UncategorizedMerchants(from, to, accountId)
            .Select(m =>
            {
                var suggested = MerchantClassifier.Suggest(m.Merchant);
                // 建議只到頂層：該頂層已经拆子類時不敢猜哪一個，只有一個子類才預勾，其它要人自己選。
                var hit = suggested is null
                    ? new List<LedgerStore.Option>()
                    : leaves.Where(c => c.Name == suggested || c.Name.StartsWith(suggested + "／", StringComparison.Ordinal)).ToList();
                var known = hit.Count == 1;
                return new SuggestRow
                {
                    Merchant = m.Merchant,
                    Count = m.Count,
                    Apply = known,
                    Category = known ? hit[0].Name : NoCategory,
                };
            })
            .ToList();

        DataContext = this;
        InitializeComponent();
        Title = from is null && to is null ? Loc.T("Suggest_Title") : Loc.F("Suggest_TitleRange", from ?? Loc.T("Suggest_Earliest"), to ?? Loc.T("Suggest_Latest"));
        ApplyButton.Content = Loc.T("Suggest_Apply");
        CancelButton.Content = Loc.T("Common_Cancel");
        HintText.Text = Loc.F("Suggest_Hint", Rows.Sum(r => r.Count), Rows.Count);
        ApplyCol.Header = Loc.T("Suggest_Apply");
        MerchantCol.Header = Loc.T("Common_Merchant");
        CountCol.Header = Loc.T("Suggest_Count");
        CategoryCol.Header = Loc.T("Common_Category");
    }

    void Apply_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in Rows)
        {
            if (!r.Apply || !_categoryIds.TryGetValue(r.Category, out var categoryId)) continue;
            Applied += _store.SetCategoryForMerchant(r.Merchant, categoryId, _accountId);
        }
        DialogResult = true;
    }
}
