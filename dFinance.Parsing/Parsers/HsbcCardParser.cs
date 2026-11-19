namespace dFinance.Parsing;

/// <summary>
/// 匯豐（台灣）信用卡帳單（HCBC_eStatement_*.pdf）。
///
/// 這份的中文全部是圖片（文字層只有數字、日期與英文），所以要靠 OCR 讀表頭與商家；
/// 金額、日期仍以文字層為準（OCR 對數字約 97% 準，錢不能賭在那裡）：
/// OCR 那一列讀到日期/商家，同一欄位附近若有文字層的數字就用它。
/// 找不到 OCR（模型沒放好）就不認這份，讓入口去提示要放模型。
/// </summary>
public sealed class HsbcCardParser : IStatementParser
{
    private static readonly string[] SkipWords = ["小計", "總額", "上期", "前期", "應繳", "手續費合計"];

    public bool CanParse(PageData data) => IssuerNames.IsPrimary(data, "匯豐銀行") && data.OcrApplied;

    public ParsedStatement Parse(PageData data)
    {
        var s = new ParsedStatement { Issuer = "匯豐銀行", RawText = data.RawText };

        // 標籤是 OCR 讀出來的，數值是文字層的（ValueFor 先找同一列標籤右邊，再往正下方找）。
        s.DueDate = data.DateFor("繳款截止日");
        s.StatementEndDate = data.DateFor("帳單結帳日");
        s.TotalDue = data.MoneyFor("本期應繳總額");
        s.MinimumDue = data.MoneyFor("最低應繳金額");
        s.AccountMask = data.Rows.SelectMany(r => r.Words)
                                 .Select(w => System.Text.RegularExpressions.Regex.Match(w.Text, @"卡號[:：]?(\d+)\*+(\d{4})"))
                                 .FirstOrDefault(m => m.Success)?.Groups[2].Value ?? "";

        ParseTransactions(data, s);

        if (s.TotalDue is null) s.Warnings.Add("找不到本期應繳總額（OCR 沒讀到那個標籤？）");
        else if (s.Transactions.Count > 0 && s.Transactions.Sum(t => t.Amount) != s.TotalDue)
            s.Warnings.Add($"明細合計 {s.Transactions.Sum(t => t.Amount):N0} 不等於本期應繳總額 {s.TotalDue:N0}（漏列或 OCR 讀錯，請比對原始單據）。");

        return s;
    }

    private void ParseTransactions(PageData data, ParsedStatement s)
    {
        // 表頭是 OCR 讀的：「簽帳日期 / 入帳日期/起息日 / 交易說明 / 消費地及外幣金額 / 折算新台幣交易日期 / 新台幣金額」。
        // OCR 會把簡繁混用（新台幣金額常被讀成「金額」的簡體），所以關鍵字要比對過才能用。
        var header = data.Rows.FirstOrDefault(r => r.Words.Count(w => w.Text.Contains("日期")) >= 2
                                               && r.Words.Any(w => w.Text.Contains("新台幣")));
        if (header is null) { s.Warnings.Add("找不到消費明細表頭（簽帳日期/新台幣金額）—— OCR 沒跑到或版面不同。"); return; }

        var dateCol = header.Words.First(w => w.Text.Contains("簽帳"));
        var postCol = header.Words.First(w => w.Text.Contains("入帳"));
        var amountCol = header.Words.Where(w => w.Text.Contains("新台幣")).OrderByDescending(w => w.Left).First();
        // 商家欄右界：表頭的「消費地」常常比實際商家格右邊一點，取雨者中間比較不會把外幣金額欄併進來。
        var rightCol = header.Words.Where(w => w.Text.Contains("消費地")).OrderBy(w => w.Left).FirstOrDefault();
        double merchantRight = Math.Min(rightCol?.Left ?? amountCol.Left, (postCol.Right + amountCol.Left) / 2);

        double merchantLeft = postCol.Right;
        var seen = new HashSet<(DateOnly, decimal)>();       // 同一筆常被文字層列和 OCR 列各算一次
        foreach (var row in data.Rows.Where(r => r.Page == header.Page && r.Top < header.Top).OrderByDescending(r => r.Top))
        {
            var text = row.Words.Where(w => !w.FromOcr).ToList();       // 文字層：錢與日期的權威來源
            var ocr = row.Words.Where(w => w.FromOcr).ToList();

            var amountWord = text.FirstOrDefault(w => w.Overlaps(amountCol, 12) && PdfText.IsMoney(w.Text))
                          ?? ocr.FirstOrDefault(w => w.Overlaps(amountCol, 12) && PdfText.IsMoney(w.Text));
            var dateWord = text.FirstOrDefault(w => w.Overlaps(dateCol) && PdfText.IsDateLike(w.Text))
                        ?? ocr.FirstOrDefault(w => w.Overlaps(dateCol) && PdfText.IsDateLike(w.Text));
            if (amountWord is null || dateWord is null) continue;

            var date = PdfText.ToDate(dateWord.Text, s.DueDate);
            var amount = PdfText.ToDecimal(amountWord.Text);
            if (date is null || amount is null) continue;
            if (!seen.Add((date.Value, amount.Value))) continue;

            var merchant = Row.Join(ocr.Where(w => w.Left >= merchantLeft - 12 && w.Left <= merchantRight
                                                && Math.Abs(w.Top - row.Top) <= 12
                                                && !SkipWords.Any(t => w.Text.Contains(t))));

            s.Transactions.Add(new ParsedTransaction
            {
                Page = row.Page,
                Date = date.Value,
                PostingDate = PdfText.ToDate((text.FirstOrDefault(w => w.Overlaps(postCol) && PdfText.IsDateLike(w.Text))
                                           ?? ocr.FirstOrDefault(w => w.Overlaps(postCol) && PdfText.IsDateLike(w.Text)))?.Text, s.DueDate),
                Merchant = merchant,
                Amount = amount.Value,
                Kind = amount.Value < 0 ? "credit" : "charge",
                // 幣別：消費地/外幣金額那段裡的最後一個三字母碼（SGP TWD 1,363.00 → TWD）。
                Currency = System.Text.RegularExpressions.Regex.Matches(
                               Row.Join(row.Words.Where(w => w.Left >= merchantRight - 12 && w.Left < amountCol.Left)),
                               @"\b[A-Z]{3}\b") is { Count: > 0 } codes ? codes[^1].Value : "",
                Card = s.AccountMask,
                RawLine = Row.Join(row.Words),
            });
        }

        if (s.Transactions.Count == 0) { s.Warnings.Add("沒有解析到任何消費明細"); return; }

        s.Transactions.Sort((a, b) => a.Date.CompareTo(b.Date));
        s.StatementStartDate = s.Transactions.Min(t => t.Date);
        if (s.Transactions.Any(t => t.Merchant.Length == 0))
            s.Warnings.Add("有些列的商家是圖片但 OCR 沒讀出來，商家留空。");
    }
}
