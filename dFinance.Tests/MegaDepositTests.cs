using dFinance.Parsing;
using Xunit;

namespace dFinance.Tests;

/// <summary>
/// 兆豐存款對帳單（MegaAttachmentFile *.pdf：資產配置總覽 + 存摺存款明細）。
/// 樣本/密碼缺就跳過（見 Samples）。三份樣本各自對得回單子上印的餘額。
/// </summary>
public class MegaDepositTests
{
    const string Main = "MegaAttachmentFile 202610 I.pdf";
    const string Foreign = "MegaAttachmentFile 202608 I.pdf";

    [Fact]
    public void 交易列解析出日期_金額_符號與摘要()
    {
        var s = Samples.Parse(Main);

        Assert.Equal("兆豐國際商業銀行", s.Issuer);
        Assert.Equal("MegaDepositParser", s.Parser);
        Assert.Equal(23, s.Transactions.Count);
        Assert.Equal(new DateOnly(2026, 10, 1), s.StatementStartDate);
        Assert.Equal(new DateOnly(2026, 10, 30), s.StatementEndDate);

        Assert.Equal(104181m, s.Transactions.Single(t => t.Merchant.Contains("薪")).Amount);
        Assert.Equal(4, s.Transactions.Count(t => t.Merchant.StartsWith("現金") && t.Amount == -20000m));   // 台新銀行 ATM
        Assert.Contains(s.Transactions, t => t.Merchant == "現金 中國信託 FEE0" && t.Amount == -5000m);
        Assert.Equal("新台幣", s.Transactions.First().Currency);      // parser 交原文，代碼由匯入端靠別名對
        Assert.All(s.Transactions, t => Assert.Equal(2026, t.Date.Year));
    }

    [Fact]
    public void 一份單子有多個帳號時逐列帶帳號()
    {
        var s = Samples.Parse(Main);

        // 交易最多的那個帳號當整份單子的帳號（帳戶總覽裡另外兩個帳號本月無交易）
        Assert.Equal("02010660350", s.AccountMask);
        Assert.Equal(["02010660350", "02064003171"], s.Transactions.Select(t => t.Account).Distinct().OrderBy(x => x).ToArray());
        Assert.Contains(s.Warnings, w => w.Contains("2 個帳號"));

        // 同一筆網際轉：提款帳戶是負的、收款帳戶是正的，不會被算成兩筆支出
        var transfer = s.Transactions.Where(t => t.Account == "02064003171").ToArray();
        Assert.Equal(new decimal[] { 99m, -99m }, transfer.Select(t => t.Amount).ToArray());
    }

    [Fact]
    public void 交易累加對得上單子印的餘額()
    {
        var s = Samples.Parse(Main);

        // 02010660350：115/09 月底餘額 16,254 → 10/30 最後一列餘額 10,924（餘額欄還沒建模，先用它驗解析有沒有漏列）
        Assert.Equal(10924m, 16254m + s.Transactions.Where(t => t.Account == "02010660350").Sum(t => t.Amount));
    }

    [Fact]
    public void 說明列與餘額列不當成交易()
    {
        var s = Samples.Parse(Main);

        Assert.DoesNotContain(s.Transactions, t => t.Merchant.Contains("月底餘額") || t.Merchant.Contains("無交易明細"));
        Assert.Contains(s.Warnings, w => w.Contains("餘額欄"));
    }

    [Fact]
    public void 外幣帳戶列帶對幣別且不混入台幣對照額()
    {
        var s = Samples.Parse(Foreign);

        Assert.Equal(4, s.Transactions.Count(t => t.Currency == "美金"));
        Assert.Equal(3, s.Transactions.Count(t => t.Currency == "日圓"));
        Assert.Contains(s.Warnings, w => w.Contains("非台幣"));

        // 日圓那幾列的備註是台幣對照額（159.090000），不該跑進商家名
        var jpy = s.Transactions.Where(t => t.Currency == "日圓").ToArray();
        Assert.All(jpy, t => Assert.Equal("網際轉", t.Merchant));
        Assert.Equal([250000m, 500000m, 500000m], jpy.Select(t => t.Amount).OrderBy(a => a).ToArray());
    }
}
