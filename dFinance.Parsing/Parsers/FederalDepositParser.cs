namespace dFinance.Parsing;

/// <summary>
/// 聯邦商業銀行 數位存款電子對帳單（帳戶明細那一段）。
/// 收入列當正數、支出列當負數；餘額欄還沒建模（見 TODO）。
/// </summary>
public sealed class FederalDepositParser : IStatementParser
{
    static readonly System.Text.RegularExpressions.Regex Account = new(@"帳號[：:]\s*([0-9*\-]+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    public bool CanParse(PageData data) => IssuerNames.IsPrimary(data, "聯邦商業銀行") && data.RawText.Contains("帳戶明細")
                                        && !data.RawText.Contains("最低應繳");

    public ParsedStatement Parse(PageData data)
    {
        var s = new ParsedStatement { Issuer = "聯邦商業銀行", RawText = data.RawText };
        s.AccountMask = Account.Match(data.RawText).Groups[1].Value;

        var header = data.Rows.FirstOrDefault(r => r.Words.Any(w => w.Text.Contains("交易日期"))
                                               && r.Words.Any(w => w.Text.Contains("收入金額")));
        if (header is null)
        {
            s.Warnings.Add("找不到交易明細表頭（交易日期/收入金額）");
            return s;
        }

        // 表頭少一個欄位名就丢例外（.First()）會讓整份檔案一筆都進不去：記警告就好。
        string[] need = ["交易日期", "摘要", "支出金額", "收入金額", "餘額"];
        var missing = header.MissingLabelsLike(need);
        if (missing.Length > 0)
        {
            s.Warnings.Add($"交易明細表頭少了欄位（缺 {missing}），這份不拆明細。");
            return s;
        }
        WordBox Col(string key) => header.LabelLike(key)!;                      // 上面查過了
        var dateCol = Col("交易日期");
        var summaryCol = Col("摘要");
        var outCol = Col("支出金額");
        var inCol = Col("收入金額");
        var balanceCol = Col("餘額");

        foreach (var row in StatementTable.DetailRows(data, header))
        {
            var date = row.Words.Where(w => w.Overlaps(dateCol)).Select(w => PdfText.ToDate(w.Text)).FirstOrDefault(d => d is not null);
            var income = row.Words.Where(w => w.Overlaps(inCol)).Select(w => PdfText.ToDecimal(w.Text)).FirstOrDefault(a => a is not null);
            var expense = row.Words.Where(w => w.Overlaps(outCol)).Select(w => PdfText.ToDecimal(w.Text)).FirstOrDefault(a => a is not null);
            if (date is null || (income is null && expense is null)) continue;
            if (row.Words.Any(w => w.Overlaps(balanceCol) && PdfText.IsMoney(w.Text)) == false) continue;   // 沒餘額就不是明細列

            decimal amount = income ?? -expense!.Value;
            var summary = Row.Join(row.Words.Where(w => w.Overlaps(summaryCol)));
            var counterparty = Row.Join(row.Words.Where(w => w.Left > summaryCol.Right && w.Left < outCol.Left - 8 && !PdfText.IsMoney(w.Text)));

            s.Transactions.Add(new ParsedTransaction
            {
                Page = row.Page,
                Date = date.Value,
                Merchant = counterparty.Length > 0 ? $"{summary} {counterparty}" : summary,
                Amount = amount,
                Kind = amount < 0 ? "credit" : "charge",
                Currency = "TWD",
                RawLine = Row.Join(row.Words),
            });
        }

        if (s.Transactions.Count == 0) { s.Warnings.Add("沒有解析到任何交易明細"); return s; }

        s.StatementStartDate = s.Transactions.Min(t => t.Date);
        s.StatementEndDate = s.Transactions.Max(t => t.Date);
        s.Warnings.Add("存款單的餘額欄沒有建模（總資產/帳戶餘額在 TODO 上）。");
        return s;
    }
}
