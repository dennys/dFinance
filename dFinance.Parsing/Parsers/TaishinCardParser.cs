using System.Text.RegularExpressions;
using System.Globalization;

namespace dFinance.Parsing;

/// <summary>
/// 台新國際商業銀行 信用卡電子帳單（TSB_Creditcard_Estatement_*.pdf）。
/// 帳單欄位是「標籤 + 數值」同一列，消費明細欄會跨列，所以商家名要從上一列撿回來。
/// </summary>
public sealed class TaishinCardParser : IStatementParser
{
    // 此帳單的日期在文字層裡出現 '115-10-12' 與 '115/10/27' 兩種分隔號，兩種都要收。
    private static readonly Regex RocDate = new(@"^(\d{3})[-/](\d{1,2})[-/](\d{1,2})$", RegexOptions.Compiled);
    private static readonly Regex Money = new(@"^[-\u2212]?(\d{1,3}(,\d{3})*|\d+)([./]\d{1,2})?$", RegexOptions.Compiled);
    private static readonly Regex CardTail = new(@"卡號末四碼[:：]\s*(\d{4})", RegexOptions.Compiled);

    // shortcut: 欄位位置取自表頭「新臺幣金額」那個字，找不到表頭就整頁跳過；台新換模板時要重新看。
    private const double AmountColTolerance = 45;   // 金額欄左右容許範圍
    private const double MerchantColLeft = 150;     // 消費明細欄左界
    private const double ContinuationMaxGap = 20;   // 商家名續行與明細列的最大行距

    public bool CanParse(PageData data) => IssuerNames.IsPrimary(data, "台新國際商業銀行") && data.RawText.Contains("本期累計應繳金額");

    public ParsedStatement Parse(PageData data)
    {
        var s = new ParsedStatement { Issuer = "台新國際商業銀行", RawText = data.RawText };
        foreach (var row in data.Rows)
        {
            var label = Row.Join(row.Words).TrimStart('-', '+', '=');
            if (label.Length == 0) continue;
            TryAssignHeader(row, label, s);
        }

        if (s.TotalDue is null) s.Warnings.Add("找不到本期累計應繳金額（第一頁版面與預期不同）");
        if (s.DueDate is null) s.Warnings.Add("找不到繳款截止日");

        s.AccountMask = string.Join("/", CardTail.Matches(data.RawText).Select(m => m.Groups[1].Value).Distinct());

        ParseTransactions(data, s);

        var payments = s.Transactions.Count(t => t.Amount < 0);
        if (s.Transactions.Count > 0 && payments > 0 && s.Transactions.Sum(t => t.Amount) != s.TotalDue)
            s.Warnings.Add($"本期累計應繳只含本期新增款項，帳單明細裡有 {payments} 筆繳款/退款金額為負，所以明細合計不會等於應繳金額。");

        return s;
    }

    private static bool TryAssignHeader(Row row, string label, ParsedStatement s)
    {
        var money = row.Words.LastOrDefault(w => Money.IsMatch(w.Text));
        var date = row.Words.FirstOrDefault(w => RocDate.IsMatch(w.Text));

        if (label.StartsWith("帳單結帳日")) s.StatementEndDate = ToDate(date?.Text);
        else if (label.StartsWith("繳款截止日")) s.DueDate = ToDate(date?.Text);
        else if (label.StartsWith("本期累計應繳金額")) s.TotalDue = ToDecimal(money?.Text);
        else if (label.StartsWith("本期最低應繳金額")) s.MinimumDue = ToDecimal(money?.Text);
        else if (label.StartsWith("本期新增款項")) { }
        else return false;

        return true;
    }

    private static void ParseTransactions(PageData data, ParsedStatement s)
    {
        var amountCol = data.Rows.GroupBy(r => r.Page).ToDictionary(
            g => g.Key,
            g => g.SelectMany(r => r.Words).FirstOrDefault(w => w.Text == "新臺幣金額")?.Left ?? double.NaN);

        string card = "";
        var pending = new List<(double Top, string Text)>();
        double lastAnchor = double.NaN;

        foreach (var pageGroup in data.Rows.GroupBy(r => r.Page))
        {
            double col = amountCol.TryGetValue(pageGroup.Key, out var c) ? c : double.NaN;
            if (double.IsNaN(col)) continue;   // 這一頁沒有消費明細表

            foreach (var row in pageGroup.OrderByDescending(r => r.Top))
            {
                var cardTail = CardTail.Match(Row.Join(row.Words));
                if (cardTail.Success)
                {
                    card = cardTail.Groups[1].Value;
                    pending.Clear();
                    continue;
                }

                var dates = row.Words.Where(w => RocDate.IsMatch(w.Text) && w.Left < MerchantColLeft).ToList();
                if (dates.Count < 2)
                {
                    // 上一筆明細下方的續列（消費地/商店名）不應該接到下一筆商家名
                    if (!double.IsNaN(lastAnchor) && lastAnchor - row.Top <= ContinuationMaxGap) continue;
                    var text = Row.Join(row.Words);
                    if (text.Length > 0 && row.Words[0].Left >= MerchantColLeft) pending.Add((row.Top, text));
                    continue;
                }

                var amount = row.Words.FirstOrDefault(w => Money.IsMatch(w.Text)
                                                          && w.Left >= col - AmountColTolerance && w.Left < col + AmountColTolerance);
                if (amount is null) continue;

                var above = pending.Where(p => p.Top - row.Top <= ContinuationMaxGap).OrderByDescending(p => p.Top);
                var inline = Row.Join(row.InRange(MerchantColLeft, col - AmountColTolerance));
                var merchant = string.Join("", above.Select(p => p.Text)) + inline;
                pending.Clear();
                lastAnchor = row.Top;

                var currency = row.Words.FirstOrDefault(w => w.Left >= col + AmountColTolerance && w.Text.Length == 2 && char.IsUpper(w.Text[0]))?.Text ?? "";

                s.Transactions.Add(new ParsedTransaction
                {
                    Page = row.Page,
                    Date = ToDate(dates[0].Text) ?? default,
                    PostingDate = ToDate(dates[1].Text),
                    Merchant = merchant.Length == 0 ? "（不明消費）" : merchant,
                    Amount = ToDecimal(amount.Text) ?? 0m,
                    Kind = (ToDecimal(amount.Text) ?? 0m) < 0 ? "credit" : "charge",
                    Currency = currency == "TW" ? "TWD" : currency,
                    Card = card,
                    RawLine = Row.Join(row.Words),
                });
            }
        }

        if (s.Transactions.Count == 0) s.Warnings.Add("沒有解析到任何消費明細");
        else
        {
            s.StatementStartDate = s.Transactions.Min(t => t.Date);
            s.StatementEndDate = s.Transactions.Max(t => t.Date);
        }
    }

    private static decimal? ToDecimal(string? text)
        => text is null || !Money.IsMatch(text) ? null : decimal.Parse(text.Replace(",", "").Replace('/', '.'), CultureInfo.InvariantCulture);

    private static DateOnly? ToDate(string? roc)
    {
        if (roc is null) return null;
        var m = RocDate.Match(roc);
        return m.Success ? new DateOnly(int.Parse(m.Groups[1].Value) + 1911, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
    }
}
