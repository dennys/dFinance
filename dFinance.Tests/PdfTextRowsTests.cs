using dFinance.Parsing;
using Xunit;

namespace dFinance.Tests;

/// <summary>
/// 不用 PDF 的純邏輯測試，鎖住兩種「靜靜漏列」的坑（真實樣本裡剛好都沒有，靠樣本測不到）：
/// 1) 明細溢出到第 2 頁、而表頭沒有重複 —— 拿第一頁表頭的 Top 當天花板會整頁丢光；
/// 2) 金額的負號被字型輸出成 U+2212 —— 只收 ASCII 連字號的話，繳款/退款列會無聲消失。
/// </summary>
public class PdfTextRowsTests
{
    static WordBox W(string text, double left, double top) => new(text, left, top, left + 40);

    static Row R(int page, double top, params (string text, double left)[] words)
        => new(page, top, words.Select(w => W(w.text, w.left, top)));

    [Fact]
    public void 表頭沒重複時下一頁靠上的明細列也要進候選()
    {
        var header = R(1, 600, ("消費日期", 40), ("台幣金額", 400));
        var data = new PageData
        {
            Rows =
            [
                R(1, 700, ("單據標題", 40)),          // 表頭上面的那帶不是明細
                header,
                R(1, 500, ("115-10-01", 40)),         // 第 1 頁明細
                R(2, 780, ("115-10-05", 40)),         // 第 2 頁第一筆：Top 比第 1 頁表頭還高，以前會被整頁剔掉
                R(3, 100, ("115-10-09", 40)),
            ],
        };

        var rows = StatementTable.DetailRows(data, header).ToList();

        Assert.DoesNotContain(rows, r => r.Page == 1 && r.Top == 700);
        Assert.Equal(new[] { 1, 2, 3 }, rows.Select(r => r.Page));        // 照頁、頁內由上往下
        Assert.Contains(rows, r => r.Page == 2 && r.Top == 780);
    }

    [Theory]
    [InlineData("−8,420", -8420)]     // U+2212：字型常輸出這個（王道踩過）
    [InlineData("－8,420", -8420)]     // 全形減號
    [InlineData("-8,420", -8420)]
    [InlineData("+1,200", 1200)]
    [InlineData("1,200", 1200)]
    [InlineData("$1,200", 1200)]
    public void 金額要收各種負號(string text, decimal expected) => Assert.Equal(expected, PdfText.ToDecimal(text));

    [Theory]
    [InlineData("8 420")]             // 中間空格：不是金額
    [InlineData("1,20")]              // 千分位不對
    [InlineData("12.3456")]           // 小數位超過 2 位
    public void 不像金額的不算金額(string text) => Assert.False(PdfText.IsMoney(text));
}
