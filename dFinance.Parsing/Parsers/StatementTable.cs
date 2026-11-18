namespace dFinance.Parsing;

/// <summary>
/// 消費明細表（消費日 / 入帳日 / 卡號 / 說明 / 金額）的欄位定義。
/// 表頭常常跨兩三列（「入帳」在上面、「起息日」在下面），所以每個欄位是把關鍵字對到的字範圍合併起來，
/// 再判斷資料列的字中心點落在哪個欄位裡。
/// </summary>
public sealed class StatementTable
{
    public required WordBox DateCol { get; init; }
    public WordBox? PostCol { get; init; }
    public WordBox? CardCol { get; init; }
    public WordBox? CurrencyCol { get; init; }
    public required WordBox AmountCol { get; init; }
    public required double MerchantLeft { get; init; }
    public required double MerchantRight { get; init; }
    public required Row HeaderRow { get; init; }

    /// <summary>
    /// 找消費明細的表頭：某一列（或它上下 20pt 內）同時有金額欄關鍵字和「消費日」之類的日期欄。
    /// 找不到回 null，讓 parser 自己決定要警告什麼。
    /// </summary>
    public static StatementTable? Find(PageData data, string[] amountKeywords, string[] dateKeywords)
    {
        foreach (var row in data.Rows)
        {
            var amount = row.Words.FirstOrDefault(w => amountKeywords.Any(k => w.Text.Contains(k)));
            if (amount is null) continue;

            // 表頭常跨兩三列，但同一帶裡也可能混到資料列（例：商家名寫了「自扣入帳」就會被當成「入帳」欄標題），
            // 所以有日期的列不當作表頭。
            var band = data.Rows.Where(r => r.Page == row.Page && Math.Abs(r.Top - row.Top) <= 20
                                         && !r.Words.Any(w => PdfText.IsDateLike(w.Text))).ToList();
            var date = Union(band, dateKeywords);
            if (date is null) continue;

            var card = Union(band, ["卡號", "末四碼", "末４碼"]);
            var post = Union(band, ["入帳", "起息"]);
            return new StatementTable
            {
                HeaderRow = row,
                AmountCol = amount,
                DateCol = date,
                PostCol = post,
                CardCol = card,
                CurrencyCol = Union(band, ["幣別"]),
                MerchantLeft = (post ?? date).Right + 4,
                MerchantRight = amount.Left - 12,
            };
        }
        return null;
    }

    static WordBox? Union(List<Row> band, string[] keywords)
    {
        var hits = band.SelectMany(r => r.Words).Where(w => keywords.Any(k => w.Text.Contains(k))).ToList();
        if (hits.Count == 0) return null;
        return new WordBox("", hits.Min(w => w.Left), hits[0].Top, hits.Max(w => w.Right));
    }

    /// <summary>金額欄的字有時會被拆成一格一格（- 1 6 9 5 1），所以要把欄位內的字接起來再解析。</summary>
    public decimal? Amount(Row row)
    {
        var inCol = row.Words.Where(w => w.Overlaps(AmountCol, 12)).ToList();
        if (inCol.Count == 0) return null;

        var joined = string.Concat(inCol.Select(w => w.Text)).Replace(",", "").Replace(" ", "");
        if (decimal.TryParse(joined, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var whole))
            return whole;

        var single = inCol.FirstOrDefault(w => PdfText.IsMoney(w.Text));
        return PdfText.ToDecimal(single?.Text);
    }

    /// <summary>商家名/消費說明：日期欄右邊到金額欄左邊之間，但卡號、幣別、金額欄的字不算。</summary>
    public string Merchant(Row row) => Row.Join(row.Words.Where(
        w => w.Left >= MerchantLeft - 12 && w.Left <= MerchantRight
             && !w.Overlaps(AmountCol, 12)
             && (CardCol is null || !w.Overlaps(CardCol))
             && (CurrencyCol is null || !w.Overlaps(CurrencyCol))));

    public DateOnly? Date(Row row, bool posting = false, DateOnly? reference = null)
    {
        var col = posting ? PostCol ?? DateCol : DateCol;
        return row.Words.Where(w => w.Overlaps(col)).Select(w => PdfText.ToDate(w.Text, reference)).FirstOrDefault(d => d is not null);
    }

    /// <summary>卡號欄通常是末四碼；整個欄位沒數字就回空字串。</summary>
    public string Card(Row row) => CardCol is null ? "" :
        row.Words.Where(w => w.Overlaps(CardCol)).Select(w => w.Text).FirstOrDefault(t => t.Length >= 4 && t.All(char.IsDigit)) ?? "";

    public string Currency(Row row) => CurrencyCol is null ? "" : Row.Join(row.Words.Where(w => w.Overlaps(CurrencyCol) && !PdfText.IsMoney(w.Text)));
}
