using dFinance.Parsing;
using Xunit;

namespace dFinance.Tests;

/// <summary>
/// 中國信託信用卡帳單（CTBC_card_Estatement_*.pdf）。中文是圖片、要靠 OCR：沒放模型就整組跳過。
/// 手上的樣本是「已全額繳清」的月份（本期應繳 0），所以只保證繳款列；消費列還沒樣本可驗。
/// </summary>
public class CtbcCardTests
{
    const string PaidInFull = "CTBC_card_Estatement_11510.pdf";
    const string WithCharges = "CTBC_card_Estatement_11507.pdf";

    [Fact]
    public void 發卡行與表頭欄位讀得到()
    {
        Samples.RequireOcr();
        var s = Samples.Parse(PaidInFull);

        Assert.Equal("中國信託商業銀行", s.Issuer);
        Assert.Equal("CtbcCardParser", s.Parser);
        Assert.Equal(new DateOnly(2026, 10, 11), s.StatementEndDate);
        Assert.Equal(0m, s.TotalDue);                            // 這個月印「請不用繳款」
        Assert.Equal(0m, s.MinimumDue);
    }

    [Fact]
    public void 繳款列解析出來_商家來自_OCR()
    {
        Samples.RequireOcr();
        var s = Samples.Parse(PaidInFull);

        var txn = Assert.Single(s.Transactions);
        Assert.Equal(-1166m, txn.Amount);
        Assert.Equal("credit", txn.Kind);
        Assert.Equal(new DateOnly(2026, 9, 29), txn.Date);
        Assert.Contains("兆豐銀", txn.Merchant);
        Assert.DoesNotContain("1,166", txn.Merchant);
    }

    [Fact]
    public void 有消費的月份_合計等於應繳_卡號讀得到()   // 11507：兩筆消費 900 + 99 = 999
    {
        Samples.RequireOcr();
        var s = Samples.Parse(WithCharges);

        Assert.Equal(2, s.Transactions.Count);
        Assert.Equal(999m, s.TotalDue);
        Assert.Equal(999m, s.Transactions.Sum(t => t.Amount));
        Assert.Equal("1648", s.AccountMask);
        Assert.All(s.Transactions, t =>
        {
            Assert.Equal("charge", t.Kind);
            Assert.Equal("TWD", t.Currency);
            Assert.Contains("CHT", t.Merchant);          // 這張單子的商店名就是這樣編碼的
        });
    }

    [Fact]
    public void 同一筆不會被文字層與_OCR_各算一次()
    {
        Samples.RequireOcr();
        var s = Samples.Parse(PaidInFull);

        Assert.Equal(s.Transactions.Count, s.Transactions.Select(t => (t.Date, t.Amount)).Distinct().Count());
    }
}
