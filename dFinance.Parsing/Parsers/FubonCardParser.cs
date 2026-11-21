using System.Text.RegularExpressions;

namespace dFinance.Parsing;

/// <summary>
/// 台北富邦銀行 信用卡帳單（115年MM月.pdf 這種檔名）。
/// 欄位對位取自表頭那一列（消費日期 / 消費說明 / 入帳日期 / 台幣金額）各字的位置，
/// 所以銀行換字級時會自己跟著走；換的是版面結構才需要改這裡。
/// </summary>
public sealed class FubonCardParser : IStatementParser
{
    private static readonly Regex CardTail = new(@"末[４4]碼\s*(\d{4})", RegexOptions.Compiled);
    private static readonly string[] SkipWords = ["小計", "本期應繳金額", "前期應繳總額", "應付帳款"];

    public bool CanParse(PageData data) => IssuerNames.IsPrimary(data, "台北富邦商業銀行") && data.RawText.Contains("台幣金額");

    public ParsedStatement Parse(PageData data)
    {
        var s = new ParsedStatement { Issuer = "台北富邦商業銀行", RawText = data.RawText };

        s.TotalDue = data.MoneyFor("本期應繳總額");
        s.MinimumDue = data.MoneyFor("最低應繳金額");
        s.DueDate = data.DateFor("繳款截止日");
        s.StatementEndDate = data.DateFor("帳單結帳日");
        s.AccountMask = string.Join("/", CardTail.Matches(data.RawText).Select(m => m.Groups[1].Value).Distinct());

        if (s.TotalDue is null) s.Warnings.Add("找不到本期應繳總額（第一頁版面與預期不同）");
        if (s.MinimumDue is null) s.Warnings.Add("找不到最低應繳金額");

        ParseTransactions(data, s);
        return s;
    }

    private static void ParseTransactions(PageData data, ParsedStatement s)
    {
        var header = data.Rows.FirstOrDefault(r => r.Words.Any(w => w.Text == "台幣金額")
                                               && r.Words.Any(w => w.Text == "消費日期"));
        if (header is null)
        {
            s.Warnings.Add("找不到消費明細表頭（消費日期/台幣金額）");
            return;
        }

        var dateCol = header.Label("消費日期");
        var postCol = header.Label("入帳日期");
        var currCol = header.Label("外幣折算日/幣別");
        var amountCol = header.Label("台幣金額");
        if (dateCol is null || postCol is null || currCol is null || amountCol is null)
        {
            // 銀行改一個欄位名就丢例外（.First()）會讓整份檔案一筆都進不去；照其他 parser 的做法：記警告、這份不拆明細。
            s.Warnings.Add($"消費明細表頭少了欄位（缺 {header.MissingLabels("消費日期", "入帳日期", "外幣折算日/幣別", "台幣金額")}），這份不拆明細。");
            return;
        }

        string card = "";
        foreach (var row in StatementTable.DetailRows(data, header))
        {
            var joined = Row.Join(row.Words);
            if (SkipWords.Any(joined.Contains)) continue;

            var tail = CardTail.Match(joined);
            if (tail.Success) { card = tail.Groups[1].Value; continue; }

            var date = row.Words.FirstOrDefault(w => w.Overlaps(dateCol, 2) && PdfText.ToDate(w.Text) is not null);
            var amount = row.Words.FirstOrDefault(w => w.Overlaps(amountCol) && PdfText.IsMoney(w.Text));
            if (date is null || amount is null) continue;

            s.Transactions.Add(new ParsedTransaction
            {
                Page = row.Page,
                Date = PdfText.ToDate(date.Text)!.Value,
                PostingDate = row.Words.Where(w => w.Overlaps(postCol, 2))
                                       .Select(w => PdfText.ToDate(w.Text)).FirstOrDefault(d => d is not null),
                Merchant = Row.Join(row.Words.Where(w => w.Left > dateCol.Right + 2 && w.Left < postCol.Left - 2 && !w.Overlaps(postCol, 2))),
                Currency = Row.Join(row.Words.Where(w => w.Overlaps(currCol) && !PdfText.IsMoney(w.Text))),
                Amount = PdfText.ToDecimal(amount.Text) ?? 0m,
                Kind = (PdfText.ToDecimal(amount.Text) ?? 0m) < 0 ? "credit" : "charge",
                Card = card,
                RawLine = joined,
            });
        }

        if (s.Transactions.Count == 0) { s.Warnings.Add("沒有解析到任何消費明細"); return; }

        s.StatementStartDate = s.Transactions.Min(t => t.Date);
        s.StatementEndDate ??= s.Transactions.Max(t => t.Date);

        // 繳款/退款列會混在明細裡（負數），應繳金額只算消費，所以合計不同時說明原因。
        int payments = s.Transactions.Count(t => t.Amount < 0);
        if (payments > 0 && s.Transactions.Sum(t => t.Amount) != s.TotalDue)
            s.Warnings.Add($"明細含 {payments} 筆繳款/退款（負數），應繳金額只算本期消費，所以合計不會等於應繳。");
    }
}
