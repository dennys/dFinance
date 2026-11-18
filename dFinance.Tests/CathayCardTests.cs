using dFinance.Parsing;
using Xunit;

namespace dFinance.Tests;

/// <summary>
/// 國泰世華信用卡帳單。手上這份樣本是已全額繳清的月份，所以明細只有繳款那一筆，
/// 能驗的範圍比較小（金額、日期、應繳 0 的警告）。
/// </summary>
public class CathayCardTests
{
    const string Sample = "信用卡電子帳單消費明細_11209.pdf";

    [Fact]
    public void Paid_off_bill_reports_zero_due_instead_of_looking_broken()
    {
        var s = Samples.Parse(Sample); if (s is null) return;

        Assert.Equal("國泰世華商業銀行", s.Issuer);
        Assert.Equal(0m, s.TotalDue);
        Assert.Contains(s.Warnings, w => w.Contains("已全額繳清"));
        Assert.DoesNotContain(s.Warnings, w => w.Contains("讀不到交易明細"));
    }

    [Fact]
    public void Payment_row_date_uses_the_bill_year_not_the_footer_year()
    {
        var s = Samples.Parse(Sample); if (s is null) return;

        var pay = Assert.Single(s.Transactions);
        Assert.Equal(-2247m, pay.Amount);
        Assert.Equal(new DateOnly(2023, 8, 29), pay.Date);   // 帳單年月 112年9月，不是內文提到的 113年8月
        Assert.Contains("自動扣繳", pay.Merchant);
        Assert.Equal(new DateOnly(2023, 9, 30), s.StatementEndDate);
    }

    [Fact]
    public void Summary_rows_are_not_transactions()
    {
        var s = Samples.Parse(Sample); if (s is null) return;
        Assert.DoesNotContain(s.Transactions, t => t.Merchant.Contains("小計") || t.Merchant.Contains("上期"));
    }
}
