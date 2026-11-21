using dFinance.Parsing;
using Xunit;

namespace dFinance.Tests;

/// <summary>永豐電子綜合對帳單（只解析存款帳戶交易明細那一段）。</summary>
public class SinopacDepositTests
{
    const string Sample = "永豐銀行2026年07月份-電子綜合對帳單.pdf";

    [Fact]
    public void Deposit_rows_balance_to_zero_and_keep_the_note_column()
    {
        var s = Samples.Parse(Sample);

        Assert.Equal("永豐商業銀行", s.Issuer);
        Assert.Equal("198-01*-**53769-*", s.AccountMask);
        Assert.Equal(9, s.Transactions.Count);
        Assert.Equal(0m, s.Transactions.Sum(t => t.Amount));   // 存入 39,610 − 支出 39,610，結餘 0
        Assert.Contains(s.Transactions, t => t.Merchant.Contains("聯邦信用卡"));
        Assert.Contains(s.Transactions, t => t.Merchant.Contains("中國信託商業銀行"));
    }

    [Fact]
    public void Other_banks_named_on_the_bill_do_not_take_over()
    {
        var s = Samples.Parse(Sample);

        // 單內也列了聯邦/兆豐/中信的信用卡應繳，銀行不能被它們搶走
        Assert.Equal("永豐商業銀行", s.Issuer);
        Assert.DoesNotContain(s.Warnings, w => w.Contains("辨識不出銀行"));
        Assert.Contains(s.Warnings, w => w.Contains("只解析存款交易明細"));
    }

    [Fact]
    public void Long_counterparty_account_numbers_are_not_the_merchant()
    {
        var s = Samples.Parse(Sample);
        Assert.All(s.Transactions, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Merchant), "商家是空的：" + t.RawLine);
            Assert.DoesNotMatch(@"^\d{12,}", t.Merchant);   // 開頭不該是一串帳號
        });
    }
}
