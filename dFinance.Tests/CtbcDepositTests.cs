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
        Assert.All(s.Transactions, t => Assert.Equal("TWD", t.Currency));   // 幣別来自段落標題的字典（那段是圖片，不是文字層原文）
        // Kind 拿掉了：方向就是上面那排金額的正負號。
        Assert.Contains("慈善款", s.Transactions.Single(t => t.Amount < 0).Merchant);

        // 單上的期末餘額要交給匯入端進 account_balance（那張帳戶總覽也印 0；51.00 是美元帳戶換過來的）。
        var bal = Assert.Single(s.Balances);
        Assert.Equal(new DateOnly(2025, 12, 15), bal.AsOf);
        Assert.Equal(0m, bal.Amount);
        Assert.Equal("29953**3590*", bal.Account);
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
