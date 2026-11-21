namespace dFinance.Parsing;

/// <summary>
/// 中國信託 信用卡帳單（CTBC_card_Estatement_*.pdf）。
///
/// 文字層只有數字/日期/英文（中文是被畫成小圖），表頭與商家要靠 OCR；
/// 金額、日期仍以文字層為準。跟 HsbcCardParser 同一套路。
///
/// 手上的樣本是「本期應繳 0（已全額繳清）」的月份：明細表裡只有繳款一筆，
/// 所以消費列能不能解析還沒驗過（見 TODO）。
/// </summary>
public sealed class CtbcCardParser : IStatementParser
{
    // 帳單結帳日那一列（上期帳單金額 + 本期帳款 - 已繳款 = 本期應繳總金額）數字會落在同一批欄位裡，不是交易。
    private static readonly string[] SkipWords = ["帳單結帳日", "上期帳單", "本期帳款", "已繳款", "本期應繳總金額", "小計"];

    public bool CanParse(PageData data) => IssuerNames.IsPrimary(data, "中國信託商業銀行")
                                        && data.Rows.SelectMany(r => r.Words).Any(w => w.Text.Contains("信用卡帳單"));

    public ParsedStatement Parse(PageData data)
    {
        var s = new ParsedStatement { Issuer = "中國信託商業銀行", RawText = data.RawText };

        s.TotalDue = data.MoneyFor("本期應繳總金額");
        s.MinimumDue = data.MoneyFor("最低應繳總金額");
        s.StatementEndDate = data.DateFor("帳單結帳日");
        // 全額繳清的月份這裡印「請不用繳款」，不會有日期。
        s.DueDate = data.DateFor("繳款截止日");

        ParseTransactions(data, s);

        if (s.TotalDue is null) s.Warnings.Add("找不到本期應繳總金額（OCR 沒讀到那個標籤？）");
        else StatementParser.WarnIfDueMismatch(s, "本期應繳總金額");

        return s;
    }

    private void ParseTransactions(PageData data, ParsedStatement s)
    {
        // 表頭（OCR）：消費日 / 入帳起息日 / 消費暨收費摘要表 / 台幣金額 / 卡號末四碼 / 消費地 / 外幣折算日 / 幣別 / 消費地金額
        var header = data.Rows.FirstOrDefault(r => r.Words.Any(w => w.Text.Contains("消費日"))
                                               && r.Words.Any(w => w.Text.Contains("台幣金額")));
        if (header is null) { s.Warnings.Add("找不到消費明細表頭（消費日/台幣金額）—— OCR 沒跑到或版面不同。"); return; }

        // 表頭關鍵字是 OCR 讀的，比對要寬（.First() 找不到就丢例外 → 整份匯不進去）。
        var dateCol = header.LabelLike("消費日");
        var postCol = header.LabelLike("入帳");
        var amountCol = header.LabelLike("台幣金額");
        if (dateCol is null || postCol is null || amountCol is null)
        {
            s.Warnings.Add($"消費明細表頭少了欄位（缺 {header.MissingLabelsLike("消費日", "入帳", "台幣金額")}）—— OCR 沒跑到或版面不同。");
            return;
        }
        var cardCol = header.LabelLike("卡號");
        double merchantLeft = postCol.Right, merchantRight = amountCol.Left;

        var seen = new HashSet<(DateOnly, decimal)>();       // 同一筆常被文字層列和 OCR 列各算一次
        foreach (var row in StatementTable.DetailRows(data, header))
        {
            var text = row.Words.Where(w => !w.FromOcr).ToList();
            var ocr = row.Words.Where(w => w.FromOcr).ToList();
            if (ocr.Any(w => SkipWords.Any(t => w.Text.Contains(t)))) continue;

            var amountWord = text.FirstOrDefault(w => w.Overlaps(amountCol, 12) && PdfText.IsMoney(w.Text))
                          ?? ocr.FirstOrDefault(w => w.Overlaps(amountCol, 12) && PdfText.IsMoney(w.Text));
            // 繳款那一列的消費日是空白，日期落在「入帳起息日」欄。
            var dateWord = text.FirstOrDefault(w => w.Overlaps(dateCol) && PdfText.IsDateLike(w.Text))
                        ?? ocr.FirstOrDefault(w => w.Overlaps(dateCol) && PdfText.IsDateLike(w.Text))
                        ?? text.FirstOrDefault(w => w.Overlaps(postCol) && PdfText.IsDateLike(w.Text))
                        ?? ocr.FirstOrDefault(w => w.Overlaps(postCol) && PdfText.IsDateLike(w.Text));
            if (amountWord is null || dateWord is null) continue;

            var date = PdfText.ToDate(dateWord.Text, s.DueDate ?? s.StatementEndDate);
            var amount = PdfText.ToDecimal(amountWord.Text);
            if (date is null || amount is null || !seen.Add((date.Value, amount.Value))) continue;

            var merchant = Row.JoinDedup(row.Words.Where(w => w.Left >= merchantLeft - 12 && w.Left <= merchantRight
                                                          && !SkipWords.Any(t => w.Text.Contains(t))));

            s.Transactions.Add(new ParsedTransaction
            {
                Page = row.Page,
                Date = date.Value,
                PostingDate = dateWord.Overlaps(postCol) ? date :
                    PdfText.ToDate((text.FirstOrDefault(w => w.Overlaps(postCol) && PdfText.IsDateLike(w.Text))
                                 ?? ocr.FirstOrDefault(w => w.Overlaps(postCol) && PdfText.IsDateLike(w.Text)))?.Text,
                                 s.DueDate ?? s.StatementEndDate),
                Merchant = merchant,
                Amount = amount.Value,
                Currency = "TWD",               // 這張表就是「台幣消費明細」
                Card = cardCol is null ? "" :
                    row.Words.Where(w => w.Overlaps(cardCol)).Select(w => w.Text).FirstOrDefault(t => t.Length >= 4 && t.All(char.IsDigit)) ?? "",
                RawLine = Row.Join(row.Words),
            });
        }

        if (s.Transactions.Count == 0) { s.Warnings.Add("沒有解析到任何消費明細"); return; }

        s.Transactions.Sort((a, b) => a.Date.CompareTo(b.Date));
        s.StatementStartDate = s.Transactions.Min(t => t.Date);
        s.AccountMask = string.Join("/", s.Transactions.Select(t => t.Card).Where(c => c.Length > 0).Distinct());
        if (s.Transactions.Any(t => t.Merchant.Length == 0))
            s.Warnings.Add("有些列的商家是圖片但 OCR 沒讀出來，商家留空。");
    }
}
