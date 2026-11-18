using dFinance.Parsing;
using Xunit;

namespace dFinance.Tests;

/// <summary>台北富邦信用卡帳單（115年MM月.pdf），樣本/密碼缺就跳過（見 Samples）。</summary>
public class FubonCardTests
{
    [Theory]
    [InlineData("115年08月.pdf", 7225, 800, 2026, 8, 26, 3)]
    [InlineData("115年09月.pdf", 2404, 800, 2026, 9, 26, 3)]
    public void Header_and_charges_match_the_bill(string file, int total, int min, int y, int m, int d, int charges)
    {
        var s = Samples.Parse(file); if (s is null) return;

        Assert.Equal("台北富邦商業銀行", s.Issuer);
        Assert.Equal(total, s.TotalDue);
        Assert.Equal(min, s.MinimumDue);
        Assert.Equal(new DateOnly(y, m, d), s.DueDate);

        Assert.Equal(charges, s.Transactions.Count(t => t.Kind == "charge"));
        Assert.Equal(total, s.Transactions.Where(t => t.Kind == "charge").Sum(t => t.Amount));
        Assert.Contains(s.Transactions, t => t.Kind == "credit" && t.Merchant.Contains("ACH自動扣繳"));
        Assert.All(s.Transactions, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Merchant), $"商家是空的：{t.RawLine}");
            Assert.DoesNotMatch(@"\d{3}[-/.]\d{1,2}[-/.]\d{1,2}", t.Merchant);   // 入帳日不應該混進商家名
            Assert.NotNull(t.PostingDate);
        });
    }

    [Fact]
    public void Two_cards_are_kept_apart()
    {
        var s = Samples.Parse("115年09月.pdf"); if (s is null) return;
        Assert.Equal(new[] { "5362", "8043" }, s.Transactions.Select(t => t.Card).Where(c => c != "").Distinct().OrderBy(x => x));
    }
}
