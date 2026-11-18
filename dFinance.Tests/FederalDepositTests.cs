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
        var s = Samples.Parse(Sample); if (s is null) return;

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
        var s = Samples.Parse(Sample); if (s is null) return;

        // 信用卡 parser 的警告不該出現；餘額未建模要講明
        Assert.DoesNotContain(s.Warnings, w => w.Contains("本期應繳") || w.Contains("消費明細"));
        Assert.Contains(s.Warnings, w => w.Contains("餘額"));
    }
}
