using dFinance.Parsing;
using Xunit;

namespace dFinance.Tests;

/// <summary>
/// 拿真實帳單當 golden 樣本。樣本檔是個資，不放進 version control（見 Samples），
/// 所以檔不存在時直接跳過（return，不是 fail）。
/// 兩個月（11510 / 11511）都驗，銀行改版時比較抓得到。
/// </summary>
public class FederalCardTests
{
    private const string Sample = "UBOT_Estatement_11511.pdf";
    private const string LastMonth = "UBOT_Estatement_11510.pdf";

    private static ParsedStatement? Parse() => Samples.Parse(Sample);

    [Fact]
    public void Header_fields_match_the_sample()
    {
        var s = Parse(); if (s is null) return;
        Assert.Equal("聯邦商業銀行", s.Issuer);
        Assert.Equal(new DateOnly(2026, 11, 21), s.DueDate);
        Assert.Equal(7439m, s.TotalDue);
        Assert.Equal(1000m, s.MinimumDue);
        Assert.DoesNotContain("尚不支援", string.Join("", s.Warnings));
    }

    [Fact]
    public void Transaction_amounts_add_up_to_total_due()
    {
        var s = Parse(); if (s is null) return;
        Assert.True(s.Transactions.Count > 40, $"只解析到 {s.Transactions.Count} 筆");
        Assert.Equal(s.TotalDue, s.Transactions.Sum(t => t.Amount));
    }

    [Fact]
    public void Every_transaction_has_date_merchant_and_amount()
    {
        var s = Parse(); if (s is null) return;
        Assert.All(s.Transactions, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Merchant));
            Assert.NotEqual(default, t.Date);
            Assert.False(t.Merchant.Contains("上期"), $"彙總表頭被當成明細：{t.Merchant}");
        });
    }

    [Fact]
    public void Refund_rows_are_negative_and_marked_credit()
    {
        var s = Parse(); if (s is null) return;
        Assert.All(s.Transactions.Where(t => t.Kind == "credit"), t => Assert.True(t.Amount < 0));
    }

    [Fact]
    public void Both_cards_are_kept_and_chinese_merchants_survive()
    {
        var s = Parse(); if (s is null) return;
        Assert.Equal(new[] { "1107", "7906" }, s.Transactions.Select(t => t.Card).Distinct().OrderBy(x => x));
        Assert.Contains(s.Transactions, t => t.Merchant.Contains("全家便利商店"));
        Assert.Contains(s.Transactions, t => t.Merchant.Contains("高鐵台北站"));
    }

    [Fact]
    public void Previous_month_bill_parses_by_the_same_rules()
    {
        var s = Samples.Parse(LastMonth); if (s is null) return;

        Assert.Equal("聯邦商業銀行", s.Issuer);
        Assert.Equal(5209m, s.TotalDue);
        Assert.Equal(s.TotalDue, s.Transactions.Sum(t => t.Amount));   // 這份沒有繳款列，合計應該要等於應繳
        Assert.All(s.Transactions, t => Assert.False(string.IsNullOrWhiteSpace(t.Merchant), t.RawLine));
        Assert.Equal(new[] { "1107", "7906" }, s.Transactions.Select(t => t.Card).Distinct().OrderBy(x => x));
        Assert.Contains(s.Transactions, t => t.Amount < 0 && t.Merchant.Contains("現金回饋"));
    }

    [Fact]
    public void Image_only_pdf_is_reported_not_silently_empty()
    {
        foreach (var pdf in new[] { "2022ESC0105.PDF" })   // 中信卡現在用 OCR 讀得過了（見 CtbcCardTests）
        {
            var s = Samples.Parse(pdf);
            if (s is null) continue;
            Assert.Empty(s.Transactions);
            Assert.Contains(s.Warnings, w => w.Contains("OCR"));
        }
    }
}
