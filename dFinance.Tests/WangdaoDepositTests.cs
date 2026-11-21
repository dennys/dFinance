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
    public void 台幣與外幣帳戶的餘額都讀得到()
    {
        var s = Parse();

        // 「帳號：010000***41988 餘額1元」與「美元：餘額 0.71元」各是一個帳戶的餘額（一個帳戶一種幣別）
        Assert.Equal(1m, s.Balances.Single(b => b.Currency == "TWD").Amount);
        Assert.Equal(0.71m, s.Balances.Single(b => b.Currency == "USD").Amount);
        Assert.All(s.Balances, b => Assert.Equal(new DateOnly(2026, 1, 26), b.AsOf));   // 單子沒印基準日：拿帳期末日
        // 「外幣活期存款總餘額（約等值臺幣）」是跨帳戶換匯的合計，對不上單一帳戶的外幣餘額 → 故意不存（會警告）
        Assert.Contains(s.Balances, b => b.Currency == "USD" && b.Amount == 0.71m);
    }

    [Fact]
    public void Header_fields_match_the_sample()
    {
        var s = Parse();
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
        var s = Parse();
        Assert.Equal(8, s.Transactions.Count);
        Assert.All(s.Transactions, t => Assert.Equal("USD", t.Currency));
        Assert.Equal(0.50m, s.Transactions.Sum(t => t.Amount));
        Assert.Contains(s.Transactions, t => t.Amount > 0);      // 存入與支出都要在（方向就是正負號）
        Assert.Contains(s.Transactions, t => t.Amount < 0);
        Assert.Contains(s.Transactions, t => t.Merchant == "帳戶存入");
    }

    [Fact]
    public void Fixed_deposits_are_flagged_not_dropped_silently()
    {
        var s = Parse();
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
        var s = Samples.Parse(QuietMonth);

        var interest = Assert.Single(s.Transactions);
        Assert.Equal("利息", interest.Merchant);
        Assert.Equal(9.44m, interest.Amount);
        Assert.Equal("USD", interest.Currency);
        Assert.True(interest.Amount > 0, "利息是收入，金額要是正的（落庫只看正負號）");
        Assert.Contains(s.Warnings, w => w.Contains("沒進模型"));
        Assert.Contains(s.Warnings, w => w.Contains("個明細段落"));
    }
}
