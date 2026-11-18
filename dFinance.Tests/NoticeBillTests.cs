using dFinance.Parsing;
using Xunit;

namespace dFinance.Tests;

/// <summary>
/// 非銀行繳費單（電/水/瓦斯/電信/eTag），樣本在 D:\dennys\statement，
/// 是個資不進 version control，檔不在就跳過。
/// </summary>
public class NoticeBillTests
{
    private static ParsedStatement? Parse(string file) => Samples.Parse(file);

    public static IEnumerable<object[]> Bills()
    {
        foreach (var f in new[]
        {
            "062213305711151205C.pdf",
            "31131317405(11510).72F78ECF-6DAA-47F8-85C1-1777136DE3EB.pdf",
            "P180105206_202610_1_EI.pdf",
            "35-0920236528_11511_notification.pdf",
            "2df96bbf-06cc-4d1f-ab0f-14f667f46f95.PDF",
        })
        {
            var s = Parse(f);
            if (s is not null) yield return new object[] { f, s };
        }
    }

    [Theory]
    [MemberData(nameof(Bills))]
    public void Every_bill_has_one_payee_and_a_positive_or_flagged_amount(string file, ParsedStatement s)
    {
        Assert.False(string.IsNullOrWhiteSpace(s.Issuer), $"{file} 沒標出繳費對象");
        // 這些不是銀行，不能被判成單面上印的代收銀行
        Assert.DoesNotContain("商業銀行", s.Issuer);
        Assert.NotEmpty(s.Transactions);
        Assert.All(s.Transactions, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Merchant));
            Assert.False(t.Date == default);
            Assert.Equal("TWD", t.Currency);
        });
        if (s.Transactions.Sum(t => t.Amount) == 0)
            Assert.Contains(s.Warnings, w => w.Contains("0 元"));
    }

    [Fact]
    public void Electricity_amount_and_due_date()
    {
        var s = Parse("062213305711151205C.pdf"); if (s is null) return;
        Assert.Equal("台灣電力公司", s.Issuer);
        Assert.Equal(3229m, s.TotalDue);
        Assert.Equal(new DateOnly(2026, 12, 5), s.DueDate);
    }

    [Fact]
    public void Gas_amount_and_usage_period()
    {
        var s = Parse("P180105206_202610_1_EI.pdf"); if (s is null) return;
        Assert.Equal("台灣中油（天然氣）", s.Issuer);
        Assert.Equal(440m, s.TotalDue);
        Assert.Equal(new DateOnly(2026, 8, 16), s.StatementStartDate);
        Assert.Equal(new DateOnly(2026, 10, 16), s.StatementEndDate);
    }

    [Fact]
    public void Etag_keeps_only_paid_passes_and_matches_the_stated_total()
    {
        var s = Parse("2df96bbf-06cc-4d1f-ab0f-14f667f46f95.PDF"); if (s is null) return;
        Assert.Equal("遠通電收（eTag）", s.Issuer);
        Assert.Equal(3, s.Transactions.Count);
        Assert.Equal(155m, s.Transactions.Sum(t => t.Amount));
        Assert.Equal(155m, s.TotalDue);
        Assert.Contains(s.Warnings, w => w.Contains("金額 0"));
    }

    [Fact]
    public void Water_bill_zero_amount_is_flagged_not_silent()
    {
        var s = Parse("31131317405(11510).72F78ECF-6DAA-47F8-85C1-1777136DE3EB.pdf"); if (s is null) return;
        Assert.Equal("台灣自來水公司", s.Issuer);
        Assert.Equal(0m, s.TotalDue);
        Assert.Contains(s.Warnings, w => w.Contains("0 元"));
    }

    [Fact]
    public void Telecom_bills_are_not_confused_with_the_bank_that_debits_them()
    {
        var s = Parse("35-0920236528_11511_notification.pdf"); if (s is null) return;
        Assert.Equal("中華電信", s.Issuer);
        Assert.Equal(99m, s.TotalDue);
        Assert.Contains("2026-10-01~2026-10-31", s.Transactions[0].Merchant.Replace('/', '-'));
    }
}
