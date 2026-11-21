using dFinance.Parsing;
using Xunit;

namespace dFinance.Tests;

/// <summary>
/// 匯豐信用卡月結單（HCBC_eStatement_*.pdf；檔名 HCBC 是原本就打錯）。
/// 中文是圖片、要靠 OCR：OCR 模型沒放進 data\ocr_models\ 就整組跳過（見 Samples.OcrReady）。
/// </summary>
public class HsbcCardTests
{
    const string PaidInFull = "HCBC_eStatement_202611.pdf";
    const string Monthly = "HCBC_eStatement_202609.pdf";

    [Fact]
    public void 表頭欄位與卡號讀得到()
    {
        Samples.RequireOcr();
        var s = Samples.Parse(PaidInFull);

        Assert.Equal("匯豐銀行", s.Issuer);
        Assert.Equal("HsbcCardParser", s.Parser);
        Assert.Equal(new DateOnly(2026, 10, 21), s.StatementStartDate);
        Assert.Equal(new DateOnly(2026, 11, 12), s.StatementEndDate);
        Assert.Equal(new DateOnly(2026, 11, 30), s.DueDate);
        Assert.Equal(2769m, s.TotalDue);
        Assert.Equal(1036m, s.MinimumDue);
        Assert.Equal("5722", s.AccountMask);
    }

    [Fact]
    public void 明細合計等於本期應繳總金額()
    {
        Samples.RequireOcr();
        var s = Samples.Parse(PaidInFull);

        Assert.Equal(4, s.Transactions.Count);
        Assert.Equal(2769m, s.Transactions.Sum(t => t.Amount));
        Assert.DoesNotContain("沒有解析到任何消費明細", s.Warnings);
    }

    [Fact]
    public void 商家是圖片但被_OCR_讀出來()
    {
        Samples.RequireOcr();
        var s = Samples.Parse(PaidInFull);

        var paypal = s.Transactions.First(t => t.Amount == 1363m);
        Assert.Contains("PAYPAL", paypal.Merchant);
        Assert.DoesNotContain("1,363", paypal.Merchant);          // 金額那列不能混進商家
    }

    [Fact]
    public void 有消費有繳款的月份也解析出繳款列()
    {
        Samples.RequireOcr();
        var s = Samples.Parse(Monthly);

        Assert.Contains(s.Transactions, t => t.Amount < 0);       // 繳款/退款列
    }
}
