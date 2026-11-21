using dFinance.Parsing;
using Xunit;

namespace dFinance.Tests;

/// <summary>永豐信用卡帳單。樣本/密碼缺就跳過（見 Samples）。</summary>
public class SinopacCardTests
{
    const string Sample = "永豐銀行信用卡帳單.pdf";

    [Fact]
    public void Header_fields_match_the_bill()
    {
        var s = Samples.Parse(Sample);

        Assert.Equal("永豐商業銀行", s.Issuer);
        Assert.Equal(13735m, s.TotalDue);
        Assert.Equal(7985m, s.MinimumDue);
        Assert.Equal(new DateOnly(2026, 11, 30), s.DueDate);
        Assert.Equal("9209", s.AccountMask);
    }

    [Fact]
    public void Charges_add_up_to_total_due_and_the_autopay_row_is_kept()
    {
        var s = Samples.Parse(Sample);

        Assert.Equal(13735m, s.Transactions.Where(t => t.Kind == "charge").Sum(t => t.Amount));
        var pay = Assert.Single(s.Transactions.Where(t => t.Amount == -16951m));
        Assert.Contains("自扣", pay.Merchant);
        Assert.Contains(s.Warnings, w => w.Contains("繳款/退款"));
    }

    [Fact]
    public void Every_row_has_merchant_and_card()
    {
        var s = Samples.Parse(Sample);

        Assert.NotEmpty(s.Transactions);
        Assert.All(s.Transactions.Where(t => t.Kind == "charge"), t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Merchant), "商家是空的：" + t.RawLine);
            Assert.Equal("9209", t.Card);                       // 消費明細都掛在同一張卡（繳款列沒有卡號）
        });
    }

    [Fact]
    public void Month_only_dates_are_placed_in_the_right_year()
    {
        var s = Samples.Parse(Sample);

        // 明細只寫 MM/DD；12 月的分期是上一年（繳款截止日 2026-11-30 之前）
        Assert.Contains(s.Transactions, t => t.Date == new DateOnly(2025, 12, 23));
        Assert.Contains(s.Transactions, t => t.Date == new DateOnly(2026, 11, 10));
        Assert.All(s.Transactions, t => Assert.True(t.Date <= s.DueDate, $"消費日比繳款截止日還晚：{t.RawLine}"));
    }
}
