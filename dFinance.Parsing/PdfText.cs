using System.Globalization;
using System.Text.RegularExpressions;

namespace dFinance.Parsing;

/// <summary>帳單文字層裡常見的金額/日期樣式，與「標籤列 + 下面一列數值」的取法。</summary>
public static class PdfText
{
    public static readonly Regex MoneyRx = new(@"^[-+]?\$?(\d{1,3}(,\d{3})*|\d+)([./]\d{1,2})?%?元?$", RegexOptions.Compiled);
    // 西元/民國都收，分隔號可能是 - / . 或 U+2010–U+2015 那幾種橫線。
    public static readonly Regex DateRx = new(@"^(\d{3,4})[-/.\u2010-\u2015](\d{1,2})[-/.\u2010-\u2015](\d{1,2})$", RegexOptions.Compiled);
    // 有些銀行把日期和文字接在同一個字（例：115-10-28已繳款金額），這種要從字串裡抓。
    private static readonly Regex LooseDateRx = new(@"(\d{3,4})[-/.\u2010-\u2015](\d{1,2})[-/.\u2010-\u2015](\d{1,2})", RegexOptions.Compiled);
    // 有的銀行把日期壓成 8 碼（兆豐存款單的 1151001）；當成日期的單號會被月/日的 TryParseExact 擋掉。
    private static readonly Regex CompactDateRx = new(@"^(1\d{2})(\d{2})(\d{2})$", RegexOptions.Compiled);

    /// <summary>從任意字串裡取第一個日期（不要求整個字就是日期）。</summary>
    public static DateOnly? FirstDate(string? text)
    {
        if (text is null) return null;
        var m = LooseDateRx.Match(text);
        return m.Success ? ToDate(m.Value) : null;
    }

    public static readonly Regex MonthDayRx = new(@"^(\d{1,2})[-/](\d{1,2})$", RegexOptions.Compiled);

    /// <summary>
    /// 這個字比較屬於哪個欄位：比對中心點距離，最近的才算。
    /// 銀行帳單的數字常常靠右對齊（數字區塊整排比欄位標題右移幾十 pt），用重疊會全部對不上。
    /// </summary>
    public static bool BelongsTo(this WordBox w, WordBox col, params WordBox[] others)
    {
        double Center(WordBox b) => (b.Left + b.Right) / 2;
        double d = Math.Abs(Center(w) - Center(col));
        return others.All(o => d <= Math.Abs(Center(w) - Center(o)) + 0.01);
    }

    /// <summary>只看形状像日期的字（不需要年份也能判断），用來分辨表頭列和資料列。</summary>
    public static bool IsDateLike(string? text) => text is not null && (DateRx.IsMatch(text) || MonthDayRx.IsMatch(text));

    public static bool IsMoney(string text) => MoneyRx.IsMatch(text);

    public static decimal? ToDecimal(string? text)
    {
        if (text is null || !IsMoney(text)) return null;
        var s = text.Replace("$", "").Replace(",", "").Replace("%", "").Replace("元", "").Replace('/', '.');
        return decimal.Parse(s, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 115-08-26 → 2026-08-26；2026-11-15 也收。日期不合法就回 null（不會把單號當成日期）。
    /// 有些帳單明細只寫 MM/DD（例：10/30），這時要給 reference（繳款截止日那種）才能回推年份。
    /// </summary>
    public static DateOnly? ToDate(string? text, DateOnly? reference = null)
    {
        if (text is null) return null;

        var compact = CompactDateRx.Match(text);                       // 1151001 → 115-10-01
        if (compact.Success) text = $"{compact.Groups[1].Value}-{compact.Groups[2].Value}-{compact.Groups[3].Value}";

        if (DateRx.IsMatch(text))
        {
            var g = DateRx.Match(text).Groups;
            int year = int.Parse(g[1].Value, CultureInfo.InvariantCulture);
            if (year < 1000) year += 1911;   // 民國年
            return DateOnly.TryParseExact($"{year}-{g[2].Value}-{g[3].Value}", "yyyy-M-d", CultureInfo.InvariantCulture,
                                          DateTimeStyles.None, out var d) ? d : null;
        }

        var mmdd = MonthDayRx.Match(text);
        if (mmdd.Success && reference is not null)
        {
            int mm = int.Parse(mmdd.Groups[1].Value, CultureInfo.InvariantCulture), dd = int.Parse(mmdd.Groups[2].Value, CultureInfo.InvariantCulture);
            // 帳單期間常跨年：比繳款截止日還「晚」的月份代表是去年的消費。
            int y = reference.Value.Year - (mm > reference.Value.Month ? 1 : 0);
            return DateOnly.TryParseExact($"{y}-{mm}-{dd}", "yyyy-M-d", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        }

        return null;
    }

    /// <summary>
    /// 文字是否屬於這個欄位（表頭那個字的 x 範圍）。判準是字的中心點落在欄位內（容許一點出界），
    /// 因為相鄰欄位常常只差 1pt，用「有碰到」會把左欄的字算進右欄。
    /// </summary>
    public static bool Overlaps(this WordBox w, WordBox column, double tolerance = 8)
    {
        double center = (w.Left + w.Right) / 2;
        return center >= column.Left - tolerance && center <= column.Right + tolerance;
    }

    public static IEnumerable<DateOnly> Dates(this PageData data)
        => data.Rows.SelectMany(r => r.Words).Select(w => ToDate(w.Text)).Where(d => d is not null).Select(d => d!.Value);

    /// <summary>
    /// 找寫著 label 的那一列，取同一列裡 label 右邊的第一個符合的字；
    /// 同一列沒有就往正下方（重疊 x、不超過 maxGap）找，這是銀行帳單最常見的版面。
    /// </summary>
    public static WordBox? ValueFor(this PageData data, string label, Func<WordBox, bool> matches, double maxGap = 45)
    {
        foreach (var row in data.Rows)
        {
            int k = row.Words.FindIndex(w => w.Text.Contains(label));
            if (k < 0) continue;

            // 同一列可能有文字層與 OCR 兩種來源：優先取文字層的（OCR 對數字不是 100% 準，錢不能賭在上面）。
            var right = row.Words.Skip(k + 1).Where(matches).ToList();
            var hit = right.FirstOrDefault(w => !w.FromOcr) ?? right.FirstOrDefault();
            if (hit is not null) return hit;

            var labelWord = row.Words[k];
            var below = data.Rows.Where(r => r.Page == row.Page && row.Top - r.Top > 1 && row.Top - r.Top <= maxGap)
                                 .SelectMany(r => r.Words)
                                 .Where(w => w.Left < labelWord.Right && w.Right > labelWord.Left - 5 && matches(w))
                                 .OrderBy(w => w.FromOcr)
                                 .ThenBy(w => Math.Abs(w.Left - labelWord.Left))
                                 .FirstOrDefault();
            if (below is not null) return below;
        }
        return null;
    }

    public static decimal? MoneyFor(this PageData data, string label) => PdfText.ToDecimal(data.ValueFor(label, w => IsMoney(w.Text))?.Text);
    public static DateOnly? DateFor(this PageData data, string label) => PdfText.ToDate(data.ValueFor(label, w => DateRx.IsMatch(w.Text))?.Text);
}
