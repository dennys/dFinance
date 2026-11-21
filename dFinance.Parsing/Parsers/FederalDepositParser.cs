namespace dFinance.Parsing;

/// <summary>
/// 聯邦商業銀行 數位存款電子對帳單（帳戶明細那一段）。
/// 收入列當正數、支出列當負數；單上的餘額進 <c>ParsedStatement.Balances</c>（「帳戶可用餘額」那張表）。
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
        decimal? lastBalance = null;                                  // 明細最後一列的餘額：那張餘額表找不到時的保底
        DateOnly? lastBalanceDate = null;

        foreach (var row in StatementTable.DetailRows(data, header))
        {
            var date = row.Words.Where(w => w.Overlaps(dateCol)).Select(w => PdfText.ToDate(w.Text)).FirstOrDefault(d => d is not null);
            var income = row.Words.Where(w => w.Overlaps(inCol)).Select(w => PdfText.ToDecimal(w.Text)).FirstOrDefault(a => a is not null);
            var expense = row.Words.Where(w => w.Overlaps(outCol)).Select(w => PdfText.ToDecimal(w.Text)).FirstOrDefault(a => a is not null);
            if (date is null || (income is null && expense is null)) continue;
            if (row.Words.Any(w => w.Overlaps(balanceCol) && PdfText.IsMoney(w.Text)) == false) continue;   // 沒餘額就不是明細列

            var printed = row.Words.Where(w => w.Overlaps(balanceCol)).Select(w => PdfText.ToDecimal(w.Text)).FirstOrDefault(a => a is not null);
            if (printed is not null) { lastBalance = printed; lastBalanceDate = date; }

            decimal amount = income ?? -expense!.Value;
            var summary = Row.Join(row.Words.Where(w => w.Overlaps(summaryCol)));
            var counterparty = Row.Join(row.Words.Where(w => w.Left > summaryCol.Right && w.Left < outCol.Left - 8 && !PdfText.IsMoney(w.Text)));

            s.Transactions.Add(new ParsedTransaction
            {
                Page = row.Page,
                Date = date.Value,
                Merchant = counterparty.Length > 0 ? $"{summary} {counterparty}" : summary,
                Amount = amount,
                Currency = "TWD",
                RawLine = Row.Join(row.Words),
            });
        }

        if (s.Transactions.Count == 0) { s.Warnings.Add("沒有解析到任何交易明細"); return s; }

        s.StatementStartDate = s.Transactions.Min(t => t.Date);
        s.StatementEndDate = s.Transactions.Max(t => t.Date);

        ReadBalances(data, s, header.Top);
        if (s.Balances.Count == 0 && lastBalance is not null && lastBalanceDate is not null)
            s.Balances.Add(new ParsedBalance
            {
                AsOf = lastBalanceDate.Value, Amount = lastBalance.Value, Currency = "TWD", Account = s.AccountMask,
            });
        if (s.Balances.Count == 0)
            s.Warnings.Add("單上的餘額沒讀到（找不到「臺幣存款帳戶餘額」那張表，明細列也沒帶餘額），這個帳戶不會進餘額校驗。");
        return s;
    }

    /// <summary>
    /// 「臺幣存款帳戶餘額　餘額基準日：115/9/30」那一張表（帳號 / 帳戶類別 / 帳戶可用餘額）。
    /// 它是單子上唯一寫了「哪天結算」的餘額，所以優先用它；低於 <paramref name="belowTop"/>（明細表頭）就不再往下收，
    /// 免得把明細列當成一張一帳戶一餘額的表。
    /// </summary>
    static void ReadBalances(PageData data, ParsedStatement s, double belowTop)
    {
        var table = data.Rows.FirstOrDefault(r => r.Words.Any(w => w.Text.Contains("帳戶可用餘額")));
        var asOf = data.DateInRow("餘額基準日");
        if (table is null || asOf is null) return;

        var acctCol = table.LabelLike("帳號");
        var balCol = table.LabelLike("帳戶可用餘額")!;
        foreach (var row in data.Rows.Where(r => r.Page == table.Page && r.Top < table.Top && r.Top > belowTop))
        {
            var amount = row.Words.Where(w => w.Overlaps(balCol)).Select(w => PdfText.ToDecimal(w.Text))
                                  .FirstOrDefault(a => a is not null);
            if (amount is null) continue;
            var acct = Row.Join(row.Words.Where(w => acctCol is not null && w.Overlaps(acctCol) && !PdfText.IsMoney(w.Text)));
            s.Balances.Add(new ParsedBalance
            {
                AsOf = asOf.Value, Amount = amount.Value, Currency = "TWD",
                Account = acct.Length > 0 ? acct : s.AccountMask,
            });
        }
    }
}
