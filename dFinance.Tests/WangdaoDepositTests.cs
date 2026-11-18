using dFinance.Parsing;
using Xunit;

namespace dFinance.Tests;

/// <summary>王道商業銀行（obank.tw）存款對帳單樣本。跟玉山（E.SUN）不同家。</summary>
public class WangdaoDepositTests
{
    private const string Sample = "PDF對帳單.pdf";
    private const string QuietMonth = "obank-202610-PDF對帳單.pdf";

    private static ParsedStatement? Parse() => Samples.Parse(Sample);

    [Fact]
    public void Header_fields_match_the_sample()
    {
        var s = Parse(); if (s is null) return;
        Assert.Equal("王道商業銀行", s.Issuer);
        Assert.Equal("010000***41988", s.AccountMask);
        Assert.Equal(new DateOnly(2026, 1, 21), s.StatementStartDate);
        Assert.Equal(new DateOnly(2026, 1, 26), s.StatementEndDate);
        // 存款單沒有應繳金額這種欄位
        Assert.Null(s.TotalDue);
        Assert.Null(s.MinimumDue);
    }

    [Fact]
    public void Deposit_transactions_are_parsed_with_sign_and_currency()
    {
        var s = Parse(); if (s is null) return;
        Assert.Equal(8, s.Transactions.Count);
        Assert.All(s.Transactions, t => Assert.Equal("USD", t.Currency));
        Assert.Equal(0.50m, s.Transactions.Sum(t => t.Amount));
        Assert.All(s.Transactions.Where(t => t.Kind == "credit"), t => Assert.True(t.Amount > 0));
        Assert.All(s.Transactions.Where(t => t.Kind == "debit"), t => Assert.True(t.Amount < 0));
        Assert.Contains(s.Transactions, t => t.Merchant == "帳戶存入");
    }

    [Fact]
    public void Fixed_deposits_are_flagged_not_dropped_silently()
    {
        var s = Parse(); if (s is null) return;
        Assert.Contains(s.Warnings, w => w.Contains("定期存款"));
        Assert.Contains(s.Warnings, w => w.Contains("外幣活期存款總餘額"));
    }

    /// <summary>
    /// 這月份很薄（臺幣沒交易、外幣只有一筆利息），拿來驗「薄」的時候會不會老實講，
    /// 而不是讓人家以為解析壞掉。
    /// </summary>
    [Fact]
    public void Quiet_month_says_why_it_is_thin()
    {
        var s = Samples.Parse(QuietMonth); if (s is null) return;

        var interest = Assert.Single(s.Transactions);
        Assert.Equal("利息", interest.Merchant);
        Assert.Equal(9.44m, interest.Amount);
        Assert.Equal("USD", interest.Currency);
        Assert.Equal("credit", interest.Kind);                 // 收入不能標成支出
        Assert.Contains(s.Warnings, w => w.Contains("沒進模型"));
        Assert.Contains(s.Warnings, w => w.Contains("個明細段落"));
    }
}
