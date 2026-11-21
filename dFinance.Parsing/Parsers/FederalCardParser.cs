using System.Text.RegularExpressions;

namespace dFinance.Parsing;

/// <summary>聯邦商業銀行 信用卡電子帳單（UBOT_Estatement_*.pdf）。</summary>
public sealed class FederalCardParser : IStatementParser
{
    private static readonly Regex RocDate = new(@"^(\d{3})/(\d{1,2})/(\d{1,2})$", RegexOptions.Compiled);
    private static readonly Regex MmDd = new(@"^(\d{1,2})/(\d{1,2})$", RegexOptions.Compiled);
    private static readonly Regex Masked = new(@"/([0-9X]{6,}X)", RegexOptions.Compiled);

    // shortcut: 聯邦帳單的欄位標籤（本期應繳金額…）是圖片，沒有文字層，所以欄位只能靠 X/Y 位置對位。
    // 聯邦換帳單模板時要重新對這些數字。
    private const double DateColRight = 60;      // 入帳日欄右界
    private const double PostColRight = 95;      // 消費日欄右界
    private const double CurrColLeft = 360;      // 幣別欄左界
    private const double AmountColLeft = 450;    // 金額欄左界
    private const double CardNumLeft = 230;
    private const double CardNumRight = 262;
    private const double HeaderBandLow = 590;
    private const double HeaderBandHigh = 700;

    // 這個版面的指紋：有「最低應繳」才是信用卡帳單（存款對帳單不會有）。
    public bool CanParse(PageData data) => IssuerNames.IsPrimary(data, "聯邦商業銀行") && data.RawText.Contains("最低應繳");

    public ParsedStatement Parse(PageData data)
    {
        var s = new ParsedStatement { Issuer = "聯邦商業銀行", RawText = data.RawText };

        var header = data.Rows.Where(r => r.Page == 1 && r.Top >= HeaderBandLow && r.Top <= HeaderBandHigh).ToList();
        s.DueDate = header.OrderByDescending(r => r.Top)
                         .Select(r => r.Words.FirstOrDefault(w => RocDate.IsMatch(w.Text)))
                         .Select(w => w is null ? null : ToDate(w.Text))
                         .FirstOrDefault(d => d is not null);

        var amountsRow = header.Select(r => new { Row = r, Amounts = r.Words.Where(IsMoney).OrderBy(w => w.Left).ToList() })
                               .Where(x => x.Amounts.Count >= 2)
                               .OrderByDescending(x => x.Amounts.Count)
                               .FirstOrDefault();
        if (amountsRow is not null)
        {
            s.TotalDue = ToDecimal(amountsRow.Amounts[0].Text);
            s.MinimumDue = ToDecimal(amountsRow.Amounts[1].Text);
        }
        else s.Warnings.Add("找不到本期應繳/最低應繳金額（第一頁版面與預期不同）");

        s.AccountMask = Masked.Match(data.RawText).Groups[1].Value;

        ParseTransactions(data, s);

        if (s.Transactions.Count > 0)
        {
            s.StatementStartDate = s.Transactions.Min(t => t.Date);
            s.StatementEndDate = s.Transactions.Max(t => t.Date);
        }
        else s.Warnings.Add("沒有解析到任何消費明細");

        return s;
    }

    private static void ParseTransactions(PageData data, ParsedStatement s)
    {
        string card = "";
        foreach (var row in data.Rows.Where(r => r.Page >= 2).OrderBy(r => r.Page).ThenByDescending(r => r.Top))
        {
            var post = row.Words.FirstOrDefault(w => MmDd.IsMatch(w.Text) && w.Left < DateColRight);
            if (post is null)
            {
                // 卡片區塊標題列（聯邦悠遊吉鶴卡 －正卡 1107）
                var num = row.Words.FirstOrDefault(w => w.Text.Length == 4 && char.IsDigit(w.Text[0])
                                                       && w.Left >= CardNumLeft && w.Left < CardNumRight);
                if (num is not null) card = num.Text;
                continue;
            }

            var consum = row.InRange(DateColRight, PostColRight).FirstOrDefault(w => MmDd.IsMatch(w.Text));
            var merchant = Row.Join(row.InRange(PostColRight, CurrColLeft));
            var currency = row.InRange(CurrColLeft, AmountColLeft).Select(w => w.Text).FirstOrDefault() ?? "";
            var amounts = row.InRange(AmountColLeft, double.MaxValue).Where(IsMoney).OrderBy(w => w.Left).ToList();
            if (amounts.Count == 0 || merchant.Length == 0) continue;

            var year = s.DueDate?.Year;
            var date = ToMmDd(consum?.Text ?? post.Text, year, s.DueDate);
            if (date is null)
            {
                s.Warnings.Add($"第 {row.Page} 頁日期無法換算西元年：{post.Text}");
                continue;
            }

            var amount = ToDecimal(amounts[^1].Text);
            s.Transactions.Add(new ParsedTransaction
            {
                Date = date.Value,
                PostingDate = ToMmDd(post.Text, year, s.DueDate),
                Merchant = merchant,
                Amount = amount,
                Kind = amount < 0 ? "credit" : "charge",
                Currency = currency,
                Card = card,
                Page = row.Page,
                RawLine = Row.Join(row.Words),
            });
        }
    }

    // 金額/日期規則一律走 PdfText（之前這裡自己寫一份只收 ASCII 減號，負額列會靜靜漏掉）。
    private static bool IsMoney(WordBox w) => PdfText.IsMoney(w.Text);

    private static decimal ToDecimal(string text) => PdfText.ToDecimal(text) ?? 0m;

    private static DateOnly? ToDate(string roc)
    {
        var m = RocDate.Match(roc);
        return m.Success ? new DateOnly(int.Parse(m.Groups[1].Value) + 1911, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
    }

    /// <summary>明細只寫 MM/DD，用繳款截止日的年份回推（跨年的消費月會落到前一年）。</summary>
    private static DateOnly? ToMmDd(string text, int? year, DateOnly? due)
    {
        var m = MmDd.Match(text);
        if (!m.Success || year is null) return null;
        int mm = int.Parse(m.Groups[1].Value), dd = int.Parse(m.Groups[2].Value);
        int y = year.Value;
        if (due is not null && mm > due.Value.Month) y -= 1;
        try { return new DateOnly(y, mm, dd); } catch { return null; }
    }
}
