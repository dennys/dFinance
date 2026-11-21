using System.Text.RegularExpressions;

namespace dFinance.Parsing;

/// <summary>
/// 兆豐國際商業銀行 信用卡帳單（MEGA_ebill_*.pdf）。
/// 一列一筆，金額取「台幣金額」欄；欄位位置取自表頭那一列的「台幣金額」等字，
/// 所以字級變動會自己跟著走。
/// </summary>
public sealed class MegaCardParser : IStatementParser
{
    private static readonly Regex CardNumber = new(@"^\d{4}-[0-9X]{4}-[0-9X]{4}-(\d{4})$", RegexOptions.Compiled);
    private static readonly Regex LeadingDate = new(@"^\d{3,4}[-/.\u2010-\u2015]\d{1,2}[-/.\u2010-\u2015]\d{1,2}", RegexOptions.Compiled);
    private static readonly string[] SkipWords = ["小計", "應付帳款", "上期", "本期總應繳款", "紅利積點"];

    public bool CanParse(PageData data) => IssuerNames.IsPrimary(data, "兆豐國際商業銀行") && data.RawText.Contains("本期待繳總額");

    public ParsedStatement Parse(PageData data)
    {
        var s = new ParsedStatement { Issuer = "兆豐國際商業銀行", RawText = data.RawText };

        s.TotalDue = data.MoneyFor("本期總應繳款") ?? data.MoneyFor("本期待繳總額");
        s.MinimumDue = data.MoneyFor("最低應繳金額");
        s.DueDate = data.DateFor("繳款截止日");
        s.StatementEndDate = data.DateFor("結帳日期");

        if (s.TotalDue is null) s.Warnings.Add("找不到本期總應繳款（第一頁版面與預期不同）");

        ParseTransactions(data, s);
        return s;
    }

    private void ParseTransactions(PageData data, ParsedStatement s)
    {
        var header = data.Rows.FirstOrDefault(r => r.Words.Any(w => w.Text == "台幣金額")
                                               && r.Words.Any(w => w.Text == "消費日"));
        if (header is null)
        {
            s.Warnings.Add("找不到消費明細表頭（消費日/台幣金額）");
            return;
        }

        // 表頭少一個欄位名就整份丢例外（.First()）會害整個檔案一筆都進不去；改成記警告、這份不拆明細。
        string[] need = ["消費日", "入帳起息日", "地區", "兌換日", "台幣金額"];
        var missing = header.MissingLabels(need);
        if (missing.Length > 0)
        {
            s.Warnings.Add($"消費明細表頭少了欄位（缺 {missing}），這份不拆明細。");
            return;
        }
        WordBox Lbl(string text) => header.Label(text)!;                       // 上面查過了
        var dateCol = Lbl("消費日");
        var postCol = Lbl("入帳起息日");
        double merchantLeft = postCol.Right, merchantRight = Lbl("地區").Left;
        double currencyLeft = Lbl("兌換日").Left;
        var amountCol = Lbl("台幣金額");

        string card = "";
        foreach (var row in StatementTable.DetailRows(data, header))
        {
            var joined = Row.Join(row.Words);
            var tight = joined.Replace(" ", "");
            if (SkipWords.Any(tight.Contains)) continue;

            var num = row.Words.Select(w => CardNumber.Match(w.Text)).FirstOrDefault(m => m.Success);
            if (num is not null) { card = num.Groups[1].Value; continue; }

            // 台幣金額欄右邊還有小計欄；繳款那一列的金額就在小計欄的位置，所以要收 Left 大於台幣金額欄起點的金額字。
            var amount = row.Words.FirstOrDefault(w => PdfText.IsMoney(w.Text) && w.Left >= amountCol.Left - 8);
            if (amount is null) continue;

            var dateWord = row.Words.FirstOrDefault(w => w.Overlaps(dateCol) && PdfText.ToDate(w.Text) is not null)
                        ?? row.Words.FirstOrDefault(w => w.Left >= merchantLeft && w.Left < merchantRight && PdfText.FirstDate(w.Text) is not null);
            var date = PdfText.ToDate(dateWord?.Text) ?? PdfText.FirstDate(dateWord?.Text);
            if (date is null) continue;   // 小計/說明列沒有日期

            var merchant = Row.Join(row.Words.Where(w => w.Left >= merchantLeft && w.Left < merchantRight));
            if (dateWord!.Left >= merchantLeft) merchant = LeadingDate.Replace(merchant, "").Trim();

            s.Transactions.Add(new ParsedTransaction
            {
                Page = row.Page,
                Date = date.Value,
                PostingDate = row.Words.Where(w => w.Overlaps(postCol))
                                       .Select(w => PdfText.ToDate(w.Text)).FirstOrDefault(d => d is not null),
                Merchant = merchant.Length == 0 ? "（未載明說明）" : merchant,
                Currency = Row.Join(row.Words.Where(w => w.Left >= currencyLeft && w.Left < amountCol.Left && !PdfText.IsMoney(w.Text))),
                Amount = PdfText.ToDecimal(amount.Text) ?? 0m,
                Kind = (PdfText.ToDecimal(amount.Text) ?? 0m) < 0 ? "credit" : "charge",
                Card = card,
                RawLine = joined,
            });
        }

        if (s.Transactions.Count == 0) { s.Warnings.Add("沒有解析到任何消費明細"); return; }

        s.AccountMask = string.Join("/", s.Transactions.Select(t => t.Card).Where(c => c.Length > 0).Distinct());

        s.StatementStartDate = s.Transactions.Min(t => t.Date);
        s.StatementEndDate ??= s.Transactions.Max(t => t.Date);

        // 繳款列不計入消費合計（它是轉帳）；排掉之後還對不上才会警告。
        StatementParser.WarnIfDueMismatch(s, "本期總應繳款");
    }
}
