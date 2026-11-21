namespace dFinance.Parsing;

/// <summary>
/// 永豐商業銀行 信用卡帳單（永豐銀行信用卡帳單.pdf）。
/// 明細只寫 MM/DD，年份用繳款截止日回推；金額欄有時被拆成一格一格，靠 StatementTable 接回去。
/// </summary>
public sealed class SinopacCardParser : IStatementParser
{
    public bool CanParse(PageData data) => IssuerNames.IsPrimary(data, "永豐商業銀行") && data.RawText.Contains("帳單說明");

    public ParsedStatement Parse(PageData data)
    {
        var s = new ParsedStatement { Issuer = "永豐商業銀行", RawText = data.RawText };

        s.TotalDue = data.MoneyFor("本期應繳總金額");
        s.MinimumDue = data.MoneyFor("本期最低應繳金額");
        s.DueDate = data.DateFor("繳款截止日");
        s.StatementEndDate = data.DateFor("結帳日");
        if (s.TotalDue is null) s.Warnings.Add("找不到本期應繳總金額（第一頁版面與預期不同）");

        var table = StatementTable.Find(data, ["臺幣金額"], ["消費日"]);
        if (table is null)
        {
            s.Warnings.Add("找不到消費明細表頭（消費日/臺幣金額）");
            return s;
        }

        foreach (var row in StatementTable.DetailRows(data, table.HeaderRow))
        {
            var date = table.Date(row, reference: s.DueDate);
            var amount = table.Amount(row);
            if (date is null || amount is null) continue;

            s.Transactions.Add(new ParsedTransaction
            {
                Page = row.Page,
                Date = date.Value,
                PostingDate = table.Date(row, posting: true, reference: s.DueDate),
                Merchant = table.Merchant(row),
                Amount = amount.Value,
                Kind = amount.Value < 0 ? "credit" : "charge",
                Currency = "TWD",
                Card = table.Card(row),
                RawLine = Row.Join(row.Words),
            });
        }

        if (s.Transactions.Count == 0) { s.Warnings.Add("沒有解析到任何消費明細"); return s; }

        s.AccountMask = string.Join("/", s.Transactions.Select(t => t.Card).Where(c => c.Length > 0).Distinct());
        s.StatementStartDate = s.Transactions.Min(t => t.Date);
        s.StatementEndDate ??= s.Transactions.Max(t => t.Date);

        int payments = s.Transactions.Count(t => t.Amount < 0);
        if (payments > 0 && s.Transactions.Sum(t => t.Amount) != s.TotalDue)
            s.Warnings.Add($"明細含 {payments} 筆繳款/退款（負數），本期應繳總金額只算消費，所以合計不會等於應繳。");

        return s;
    }
}
