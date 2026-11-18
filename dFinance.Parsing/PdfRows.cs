using System.Text;
using UglyToad.PdfPig;

namespace dFinance.Parsing;

public sealed record WordBox(string Text, double Left, double Top, double Right)
{
    public double X => Left;
}

public sealed class Row
{
    public Row(int page, double top, IEnumerable<WordBox> words)
    {
        Page = page;
        Top = top;
        Words = words.OrderBy(w => w.Left).ToList();
    }

    public int Page { get; }
    public double Top { get; }
    public List<WordBox> Words { get; }

    public IEnumerable<WordBox> InRange(double leftInclusive, double rightExclusive)
        => Words.Where(w => w.Left >= leftInclusive && w.Left < rightExclusive);

    /// <summary>把同一行（或跨行續行）的字接回原文。</summary>
    public static string Join(IEnumerable<WordBox> words)
    {
        string s = "";
        WordBox? prev = null;
        foreach (var w in words)
        {
            if (prev is not null && w.Left - prev.Right > 2) s += " ";
            s += w.Text;
            prev = w;
        }
        return s;
    }
}

public sealed class PageData
{
    public List<Row> Rows { get; init; } = new();
    public string RawText { get; init; } = "";
    public int PageCount { get; init; }

    /// <summary>圖片物件數量，用來判斷這份是不是圖片式帳單。</summary>
    public int ImageCount { get; init; }

    public bool HasChinese => RawText.Any(c => c >= 0x4E00 && c <= 0x9FFF);
}

public static class PdfRows
{
    // 聯邦帳單行距約 14.2pt，同一行內上下差 ≤3pt → 5pt 可分開相鄰兩行。
    private const double RowTolerance = 5.0;

    public static PageData Read(string path, string? password)
    {
        var options = new ParsingOptions { Password = password ?? "" };
        using var doc = PdfDocument.Open(path, options);

        var rows = new List<Row>();
        var text = new StringBuilder();
        int images = 0;
        foreach (var page in doc.GetPages())
        {
            text.Append(page.Text).Append('\n');
            images += page.GetImages().Count();
            rows.AddRange(Cluster(page.GetWords().Select(w =>
                new WordBox(w.Text, w.BoundingBox.Left, w.BoundingBox.Top, w.BoundingBox.Right)), page.Number));
        }

        return new PageData { Rows = rows, RawText = text.ToString(), PageCount = doc.NumberOfPages, ImageCount = images };
    }

    private static IEnumerable<Row> Cluster(IEnumerable<WordBox> words, int page)
    {
        var sorted = words.OrderByDescending(w => w.Top).ToList();
        var groups = new List<(double Top, List<WordBox> Words)>();
        List<WordBox>? current = null;
        double anchor = 0;

        foreach (var w in sorted)
        {
            if (current is null || anchor - w.Top > RowTolerance)
            {
                current = new List<WordBox>();
                groups.Add((w.Top, current));
                anchor = w.Top;
            }
            current.Add(w);
        }

        return groups.Select(g => new Row(page, g.Top, g.Words));
    }
}
