using dFinance.Parsing;
using Xunit;

namespace dFinance.Tests;

/// <summary>兆豐信用卡帳單（MEGA_ebill_*.pdf），樣本/密碼缺就跳過（見 Samples）。</summary>
public class MegaCardTests
{
    const string Sample = "MEGA_ebill_11511.pdf";

    [Fact]
    public void Header_fields_match_the_bill()
    {
        var s = Samples.Parse(Sample);

        Assert.Equal("兆豐國際商業銀行", s.Issuer);
        Assert.Equal(1265.00m, s.TotalDue);
        Assert.Equal(1265.00m, s.MinimumDue);
        Assert.Equal(new DateOnly(2026, 11, 27), s.DueDate);
        Assert.Equal(new DateOnly(2026, 11, 12), s.StatementEndDate);
        Assert.Equal("9306", s.AccountMask);
    }

    [Fact]
    public void Charges_add_up_to_the_amount_due_and_payment_is_flagged()
    {
        var s = Samples.Parse(Sample);

        Assert.Equal(3, s.Transactions.Count(t => t.Kind == "charge"));
        Assert.Equal(1265.00m, s.Transactions.Where(t => t.Kind == "charge").Sum(t => t.Amount)
                   + s.Transactions.Where(t => t.Kind == "credit" && !t.Merchant.Contains("繳款")).Sum(t => t.Amount));
        var payment = Assert.Single(s.Transactions.Where(t => t.Merchant.Contains("繳款")));
        Assert.Equal(-4534.00m, payment.Amount);
        Assert.Contains(s.Warnings, w => w.Contains("繳款"));
    }

    [Fact]
    public void Summary_rows_are_not_mistaken_for_transactions()
    {
        var s = Samples.Parse(Sample);
        Assert.DoesNotContain(s.Transactions, t => t.Merchant.Contains("小計") || t.Merchant.Contains("應付帳款"));
        Assert.All(s.Transactions.Where(t => t.Kind == "charge"), t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Merchant));
            Assert.NotNull(t.PostingDate);   // 消費明細該有消費日+入帳日（其他交易明細只列消費日）
        });
    }
}
