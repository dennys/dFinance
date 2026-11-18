using System.Windows;
using dFinance.Data;

namespace dFinance;

/// <summary>未分類商家的分類建議：可勾選套用、改分類或取消。</summary>
public partial class SuggestDialog : Window
{
    public const string NoCategory = "（不分類）";

    readonly LedgerStore _store;
    readonly Dictionary<string, long> _categoryIds;

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
        var categories = store.Categories("expense").Concat(store.Categories("income")).ToList();
        _categoryIds = categories.ToDictionary(c => c.Name, c => c.Id);
        CategoryNames = new List<string> { NoCategory };
        CategoryNames.AddRange(categories.Select(c => c.Name));

        Rows = store.UncategorizedMerchants(from, to, accountId)
            .Select(m =>
            {
                var suggested = MerchantClassifier.Suggest(m.Merchant);
                var known = suggested is not null && _categoryIds.ContainsKey(suggested);
                return new SuggestRow
                {
                    Merchant = m.Merchant,
                    Count = m.Count,
                    Apply = known,
                    Category = known ? suggested! : NoCategory,
                };
            })
            .ToList();

        DataContext = this;
        InitializeComponent();
        Title = from is null && to is null ? "建議分類" : $"建議分類（{from ?? "最早"} ～ {to ?? "最近"}）";
        ApplyButton.Content = "套用";
        CancelButton.Content = "取消";
        HintText.Text = $"共 {Rows.Sum(r => r.Count)} 筆未分類、{Rows.Count} 個商家（同一商家的筆數會合併成一列）。勾選要套用的商家；可點「分類」欄改成其他分類。取消勾選或選「（不分類）」就不套用。";
        ApplyCol.Header = "套用";
        MerchantCol.Header = "商家";
        CountCol.Header = "筆數";
        CategoryCol.Header = "分類";
    }

    void Apply_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in Rows)
        {
            if (!r.Apply || !_categoryIds.TryGetValue(r.Category, out var categoryId)) continue;
            Applied += _store.SetCategoryForMerchant(r.Merchant, categoryId);
        }
        DialogResult = true;
    }
}
