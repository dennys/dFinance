using dFinance.Parsing;
using Xunit;

namespace dFinance.Tests;

/// <summary>
/// 中國信託存款對帳單（CTBC_Bank_Estatement_*.pdf）：交易明細那段整段是圖片，要靠 OCR。
/// 單子上有「上期結餘」與每列「帳戶餘額」→ 順手拿它校驗 OCR 有沒有讀錯金額。
/// </summary>
public class CtbcDepositTests
{
    const string Statement = "CTBC_Bank_Estatement_11501.pdf";

    [Fact]
    public void 期間與帳號讀得到()
    {
        Samples.RequireOcr();
        var s = Samples.Parse(Statement);

        Assert.Equal("中國信託商業銀行", s.Issuer);
        Assert.Equal("CtbcDepositParser", s.Parser);
        Assert.Equal(new DateOnly(2025, 12, 1), s.StatementStartDate);
        Assert.Equal(new DateOnly(2025, 12, 31), s.StatementEndDate);
        Assert.Equal("29953**3590*", s.AccountMask);
    }

    [Fact]
    public void 存入與支出分得對_幣別是台幣()
    {
        Samples.RequireOcr();
        var s = Samples.Parse(Statement);

        Assert.Equal(3, s.Transactions.Count);
        Assert.Equal(new[] { 500m, 500m, -1000m }, s.Transactions.Select(t => t.Amount).ToArray());
        Assert.All(s.Transactions, t => Assert.Equal("TWD", t.Currency));
        Assert.Equal(new[] { "charge", "charge", "credit" }, s.Transactions.Select(t => t.Kind).ToArray());
        Assert.Contains("慈善款", s.Transactions.Single(t => t.Amount < 0).Merchant);
    }

    [Fact]
    public void 上期結餘加本期交易要等於單子上的期末餘額()
    {
        Samples.RequireOcr();
        var s = Samples.Parse(Statement);

        // 上期結餘 0 + 500 + 500 - 1,000 = 0 = 單子上的餘額；對不上就會出現這條警告。
        Assert.DoesNotContain(s.Warnings, w => w.Contains("期末餘額"));
    }
}
