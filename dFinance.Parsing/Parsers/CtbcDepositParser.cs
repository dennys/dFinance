namespace dFinance.Parsing;

/// <summary>
/// 中國信託 存款對帳單（CTBC_Bank_Estatement_*.pdf）。
///
/// 交易明細那段（「一般帳戶資產交易明細」→「台幣活存帳戶交易明細」）整段中文都是圖片，
/// 要靠 OCR；金額/日期仍只取文字層。表頭是 帳號/日期/摘要/支出/存入/帳戶餘額/註記，
/// 所以用 StatementTable 那套欄位座標，不寫死數字。
///
/// 單子自己印了「上期結餘」與每列的「帳戶餘額」→ 順手拿它校驗：上期結餘 + Σ(存入-支出)
/// 要等於最後一列的餘額，對不上就警告（OCR 讀錯數字時會被抓到）。
/// </summary>
public sealed class CtbcDepositParser : IStatementParser
{
    // 段落標題 → 幣別。只认得這些；認不得的段落一律不解析（錢不能猜幣別）。
    static readonly (string Title, string Currency)[] Sections =
    [
        ("台幣", "TWD"), ("美元", "USD"), ("人民幣", "CNY"), ("歐元", "EUR"), ("日圓", "JPY"), ("日元", "JPY"),
        ("港幣", "HKD"), ("英鎊", "GBP"), ("澳元", "AUD"), ("加幣", "CAD"), ("紐元", "NZD"), ("韓元", "KRW"),
        ("泰銖", "THB"), ("南非幣", "ZAR"), ("越南盾", "VND"), ("瑞士法郎", "CHF"), ("新臺幣", "TWD"),
    ];

    // 結餘列不是交易。
    private static readonly string[] SkipWords = ["上期結餘", "本期結餘", "期末結餘", "小計", "合計", "本金及利息"];

    public bool CanParse(PageData data) => IssuerNames.IsPrimary(data, "中國信託商業銀行")
                                        && data.Rows.Any(r => r.Words.Any(w => w.Text.Contains("帳戶餘額"))
                                                          && r.Words.Any(w => w.Text.Contains("存入")));

    public ParsedStatement Parse(PageData data)
    {
        var s = new ParsedStatement { Issuer = "中國信託商業銀行", RawText = data.RawText };

        // 「對帳單期間：2025-12-01-2025-12-31」（標籤是圖片，兩個日期在文字層）
        var period = data.Rows.FirstOrDefault(r => r.Words.Any(w => w.Text.Contains("對帳單期間")));
        if (period is not null)
        {
            var dates = period.Words.Select(w => PdfText.ToDate(w.Text)).Where(d => d is not null).Cast<DateOnly>().ToList();
            if (dates.Count > 0) s.StatementStartDate = dates.Min();
            if (dates.Count > 1) s.StatementEndDate = dates.Max();
        }

        var accounts = new Dictionary<string, int>();
        foreach (var header in data.Rows.Where(IsTableHeader))
        {
            var currency = SectionCurrency(data, header, s);
            if (currency is null) continue;                       // 已加警告
            ParseSection(data, header, currency, s, accounts);
        }

        if (s.Transactions.Count == 0) { s.Warnings.Add("沒有解析到任何存款交易明細"); return s; }

        s.AccountMask = accounts.OrderByDescending(kv => kv.Value).First().Key;
        s.StatementStartDate ??= s.Transactions.Min(t => t.Date);
        s.StatementEndDate ??= s.Transactions.Max(t => t.Date);
        if (accounts.Count > 1)
            s.Warnings.Add($"這份含 {accounts.Count} 個帳號（{string.Join("、", accounts.Keys)}），已照列級帳號分別歸戶。");
        return s;
    }

    static bool IsTableHeader(Row row) => row.Words.Any(w => w.Text.Contains("日期"))
                                       && row.Words.Any(w => w.Text.Contains("存入"))
                                       && row.Words.Any(w => w.Text.Contains("帳戶餘額"));

    /// <summary>表頭上面那列的段落標題（「台幣活存帳戶交易明細」）決定這段是什麼幣別。</summary>
    static string? SectionCurrency(PageData data, Row header, ParsedStatement s)
    {
        var above = data.Rows.Where(r => r.Page == header.Page && r.Top - header.Top > 1 && r.Top - header.Top <= 40)
                             .OrderBy(r => r.Top).FirstOrDefault(r => r.Words.Any(w => w.Text.Contains("交易明細")));
        var title = above is null ? "" : string.Join(" ", above.Words.Select(w => w.Text));
        var hit = Sections.FirstOrDefault(x => title.Contains(x.Title));
        if (hit.Currency is null)
        {
            s.Warnings.Add($"有一段交易明細不知是什麼幣別（標題：「{(title.Length > 0 ? title : "讀不到")}」），沒解析它 —— 錢不能猜幣別。");
            return null;
        }
        return hit.Currency;
    }

    void ParseSection(PageData data, Row header, string currency, ParsedStatement s, Dictionary<string, int> accounts)
    {
        var dateCol = header.Words.First(w => w.Text.Contains("日期"));
        var outCol = header.Words.First(w => w.Text.Contains("支出"));
        var inCol = header.Words.First(w => w.Text.Contains("存入"));
        var balanceCol = header.Words.First(w => w.Text.Contains("帳戶餘額"));
        var acctCol = header.Words.Where(w => w.Left < dateCol.Left).OrderBy(w => w.Left).First();

        decimal? opening = null, lastBalance = null;
        decimal sectionSum = 0;
        string account = "";

        foreach (var row in data.Rows.Where(r => r.Page == header.Page && r.Top < header.Top).OrderByDescending(r => r.Top))
        {
            var text = row.Words.Where(w => !w.FromOcr).ToList();
            var ocr = row.Words.Where(w => w.FromOcr).ToList();
            var rowText = string.Join(" ", row.Words.Select(w => w.Text));

            // 帳號只印在段落第一列（下期結餘那列），下面留白 → 往下延用。
            var acctWord = text.FirstOrDefault(w => w.Overlaps(acctCol, 6)) ?? ocr.FirstOrDefault(w => w.Overlaps(acctCol, 6));
            if (acctWord is not null && acctWord.Text.Length >= 6) account = acctWord.Text;

            if (SkipWords.Any(t => rowText.Contains(t)))
            {
                opening ??= Money(text, ocr, balanceCol);      // 「上期結餘 X」：那是期初，不是交易
                continue;
            }

            var dateWord = First(text, w => w.Overlaps(dateCol) && PdfText.IsDateLike(w.Text))
                        ?? First(ocr, w => w.Overlaps(dateCol) && PdfText.IsDateLike(w.Text));
            var inWord = First(text, w => w.Overlaps(inCol) && PdfText.IsMoney(w.Text))
                      ?? First(ocr, w => w.Overlaps(inCol) && PdfText.IsMoney(w.Text));
            var outWord = First(text, w => w.Overlaps(outCol) && PdfText.IsMoney(w.Text))
                       ?? First(ocr, w => w.Overlaps(outCol) && PdfText.IsMoney(w.Text));
            if (dateWord is null || (inWord is null && outWord is null)) continue;

            var date = PdfText.ToDate(dateWord.Text, s.StatementEndDate);
            var amount = inWord is not null ? PdfText.ToDecimal(inWord.Text) : -PdfText.ToDecimal(outWord!.Text);
            if (date is null || amount is null) continue;

            var balance = Money(text, ocr, balanceCol);
            if (balance is not null) lastBalance = balance;
            sectionSum += amount.Value;

            if (account.Length > 0) accounts[account] = accounts.GetValueOrDefault(account) + 1;

            s.Transactions.Add(new ParsedTransaction
            {
                Page = row.Page,
                Date = date.Value,
                Merchant = Row.JoinDedup(row.Words.Where(w => w.Left >= dateCol.Right && w.Left <= outCol.Left)) is { Length: > 0 } m
                           ? m : "（未載明摘要）",
                Amount = amount.Value,
                Currency = currency,
                Kind = inWord is null ? "credit" : "charge",
                Account = account,
                RawLine = Row.Join(row.Words),
            });
        }

        // 餘額鏈校驗：這是 OCR 讀錯數字時唯一的自動偵錯機會。
        if (opening is not null && lastBalance is not null && opening + sectionSum != lastBalance)
            s.Warnings.Add($"{currency} 帳戶 {account}：上期結餘 {opening:N0} + 本期交易 {sectionSum:N0} 不等於單子上的期末餘額 {lastBalance:N0}（OCR 可能讀錯金額，請比對原始單據）。");
    }

    static WordBox? First(List<WordBox> words, Func<WordBox, bool> match) => words.FirstOrDefault(match);

    static decimal? Money(List<WordBox> text, List<WordBox> ocr, WordBox col)
        => PdfText.ToDecimal((First(text, w => w.Overlaps(col, 12) && PdfText.IsMoney(w.Text))
                           ?? First(ocr, w => w.Overlaps(col, 12) && PdfText.IsMoney(w.Text)))?.Text);
}
