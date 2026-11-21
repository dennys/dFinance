using System.Text.RegularExpressions;

namespace dFinance.Parsing;

/// <summary>
/// 國泰世華商業銀行 信用卡帳單（信用卡電子帳單消費明細_*.pdf）。
/// 這家的表頭跨兩列（「入帳」/「起息日」、「卡號」/「後四碼」），StatementTable 會把同一欄的字合併。
/// 明細只寫 MM/DD，年份用帳單年月回推。
/// </summary>
public sealed class CathayCardParser : IStatementParser
{
    static readonly Regex BillYearMonth = new(@"(\d{3})\s*年\s*(\d{1,2})\s*月", RegexOptions.Compiled);

    public bool CanParse(PageData data) => IssuerNames.IsPrimary(data, "國泰世華商業銀行") && data.RawText.Contains("交易說明");

    public ParsedStatement Parse(PageData data)
    {
        var s = new ParsedStatement { Issuer = "國泰世華商業銀行", RawText = data.RawText };

        s.TotalDue = data.MoneyFor("本期應繳總額");
        s.MinimumDue = data.MoneyFor("最低應繳金額NT$") ?? data.MoneyFor("本期最低應繳金額");
        s.DueDate = data.DateFor("繳款截止日");

        // 帳單年月（例：112年9月）只在標題那行可靠：內文也會出現「113年8月31日前到期」那種日期。
        var ym = data.Rows.Select(r => BillYearMonth.Match(Row.Join(r.Words)))
                         .Concat([BillYearMonth.Match(data.RawText)])
                         .FirstOrDefault(m => m.Success);
        DateOnly? reference = null;
        if (ym.Success)
        {
            int y = int.Parse(ym.Groups[1].Value) + 1911, m = int.Parse(ym.Groups[2].Value);
            reference = new DateOnly(y, m, DateTime.DaysInMonth(y, m));
            s.StatementEndDate = reference;
        }

        var table = StatementTable.Find(data, ["新臺幣金額"], ["消費日"]);
        if (table is null)
        {
            s.Warnings.Add("找不到消費明細表頭（消費日/新臺幣金額）");
            return s;
        }

        foreach (var row in StatementTable.DetailRows(data, table.HeaderRow))
        {
            var date = table.Date(row, reference: reference);
            var amount = table.Amount(row);
            if (date is null || amount is null) continue;

            s.Transactions.Add(new ParsedTransaction
            {
                Page = row.Page,
                Date = date.Value,
                PostingDate = table.Date(row, posting: true, reference: reference),
                Merchant = table.Merchant(row),
                Amount = amount.Value,
                Kind = amount.Value < 0 ? "credit" : "charge",
                Currency = table.Currency(row),
                Card = table.Card(row),
                RawLine = Row.Join(row.Words),
            });
        }

        if (s.Transactions.Count == 0) { s.Warnings.Add("沒有解析到任何消費明細"); return s; }

        s.AccountMask = string.Join("/", s.Transactions.Select(t => t.Card).Where(c => c.Length > 0).Distinct());
        s.StatementStartDate = s.Transactions.Min(t => t.Date);
        if (s.StatementEndDate is null) s.StatementEndDate = s.Transactions.Max(t => t.Date);

        if (s.TotalDue == 0) s.Warnings.Add("本期應繳總額 0（已全額繳清），明細通常只剩繳款紀錄。");
        else if (s.MinimumDue is null) s.Warnings.Add("找不到最低應繳金額（這份樣本的那個欄位是圖片，文字層沒有）。");

        return s;
    }
}
