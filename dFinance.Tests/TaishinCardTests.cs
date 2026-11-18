using dFinance.Parsing;
using Xunit;

namespace dFinance.Tests;

/// <summary>
/// 台新信用卡帳單（TSB_Creditcard_Estatement_*.pdf），樣本/密碼缺就跳過（見 Samples）。
/// </summary>
public class TaishinCardTests
{

    private static ParsedStatement? Parse() => Samples.Parse("TSB_Creditcard_Estatement_202610.pdf");

    [Fact]
    public void Header_fields_match_the_sample()
    {
        var s = Parse(); if (s is null) return;
        Assert.Equal("台新國際商業銀行", s.Issuer);
        Assert.Equal(new DateOnly(2026, 10, 27), s.DueDate);
        Assert.Equal(3000m, s.TotalDue);
        Assert.Equal(1000m, s.MinimumDue);
        Assert.Equal("4605", s.AccountMask);
    }

    [Fact]
    public void Merchant_wrapped_over_two_lines_is_joined()
    {
        var s = Parse(); if (s is null) return;
        Assert.Equal(2, s.Transactions.Count);
        var pay = s.Transactions.Single(t => t.Amount < 0);
        Assert.Equal(-1166m, pay.Amount);
        Assert.Contains("兆豐銀行帳戶自動轉帳", pay.Merchant);
        Assert.Contains("卡款", pay.Merchant);   // 同一列接在日期後面的續文字
    }

    [Fact]
    public void Location_line_below_does_not_leak_into_the_next_merchant()
    {
        var s = Parse(); if (s is null) return;
        var buy = s.Transactions.Single(t => t.Amount == 3000m);
        Assert.Equal("街口電支－繳費-新竹振道有線電", buy.Merchant);
        Assert.DoesNotContain("TAIPEI", buy.Merchant);
        Assert.Equal(new DateOnly(2026, 9, 23), buy.Date);
        Assert.Equal(new DateOnly(2026, 9, 24), buy.PostingDate);
        Assert.Equal("TWD", buy.Currency);
        Assert.Equal("4605", buy.Card);
    }

    [Fact]
    public void Payment_rows_are_explained_not_left_looking_like_a_parse_error()
    {
        var s = Parse(); if (s is null) return;
        Assert.NotEqual(s.TotalDue, s.Transactions.Sum(t => t.Amount));
        Assert.Contains(s.Warnings, w => w.Contains("繳款/退款"));
        Assert.DoesNotContain(s.Warnings, w => w.Contains("讀不到交易明細"));
    }

    /// <summary>
    /// 上個月那份（202611）拿來驗改版：不一樣的消費內容、不一樣的繳款金額，
    /// 規則要還是一樣：消費合计 = 應繳，繳款列另外講。
    /// </summary>
    [Fact]
    public void Previous_month_bill_parses_by_the_same_rules()
    {
        var s = Samples.Parse("TSB_Creditcard_Estatement_202611.pdf"); if (s is null) return;

        Assert.Equal(new DateOnly(2026, 11, 27), s.DueDate);
        Assert.Equal(1006m, s.TotalDue);
        Assert.Equal(1000m, s.MinimumDue);
        Assert.Equal("4605", s.AccountMask);
        Assert.Equal(1006m, s.Transactions.Where(t => t.Amount > 0).Sum(t => t.Amount));
        Assert.Equal(-3000m, Assert.Single(s.Transactions.Where(t => t.Amount < 0)).Amount);
        // 消費地這月份被 PDF 直接拼在商家名字後面（同一個 text run，不是 parser 撿錯欄位）
        Assert.Contains(s.Transactions, t => t.Merchant.StartsWith("街口電支－中華電信"));
    }
}
