using dFinance.Parsing;
using Xunit;

namespace dFinance.Tests;

/// <summary>聯邦數位存款電子對帳單（不是信用卡帳單）。</summary>
public class FederalDepositTests
{
    const string Sample = "數位存款電子對帳單.pdf";

    [Fact]
    public void Deposit_rows_are_parsed_with_sign_and_account_mask()
    {
        var s = Samples.Parse(Sample);

        Assert.Equal("聯邦商業銀行", s.Issuer);
        Assert.Equal("888-50-*1*9*9*", s.AccountMask);
        Assert.Equal(3, s.Transactions.Count);
        Assert.Equal(5217m, s.Transactions.Single(t => t.Merchant.Contains("ＡＴＭＦ轉入")).Amount);
        Assert.Equal(-5218m, s.Transactions.Single(t => t.Merchant.Contains("信用卡款")).Amount);
        Assert.All(s.Transactions, t => Assert.Equal(2026, t.Date.Year));
    }

    [Fact]
    public void Deposit_statement_is_not_mistaken_for_the_credit_card_one()
    {
        var s = Samples.Parse(Sample);

        // 信用卡 parser 的警告不該出現
        Assert.DoesNotContain(s.Warnings, w => w.Contains("本期應繳") || w.Contains("消費明細"));
    }

    [Fact]
    public void 單上的餘額基準日表會進_balances()
    {
        var s = Samples.Parse(Sample);

        // 「臺幣存款帳戶餘額　餘額基準日：115/9/30」→ 帳號 888-50-*1*9*9* 可用餘額 1 元
        var b = Assert.Single(s.Balances);
        Assert.Equal("888-50-*1*9*9*", b.Account);
        Assert.Equal(1m, b.Amount);
        Assert.Equal(new DateOnly(2026, 9, 30), b.AsOf);              // 基準日是單子上寫的，不是最後一筆交易日
        Assert.DoesNotContain(s.Warnings, w => w.Contains("餘額沒讀到"));
    }
}
