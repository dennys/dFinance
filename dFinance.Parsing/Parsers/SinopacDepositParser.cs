using System.Text.RegularExpressions;

namespace dFinance.Parsing;

/// <summary>
/// 永豐商業銀行 電子綜合對帳單（存款帳戶交易明細那一段）。
/// 備註/資金用途那欄常常跨好幾列，會散在明細列的上方跟下方，所以要先把週邊的續行撿回來當商家說明。
/// 這份單子也會列別家銀行的信用卡應繳金額，銀行辨識靠 IssuerNames.IsPrimary（數出現次數最多的那家）。
/// </summary>
public sealed class SinopacDepositParser : IStatementParser
{
    static readonly Regex AccountBlob = new(@"^[0-9Ａ-Ｚａ-ｚ*\-]{8,}$", RegexOptions.Compiled);

    public bool CanParse(PageData data) => IssuerNames.IsPrimary(data, "永豐商業銀行") && data.RawText.Contains("帳戶交易明細")
                                        && !data.RawText.Contains("帳單說明");

    public ParsedStatement Parse(PageData data)
    {
        var s = new ParsedStatement { Issuer = "永豐商業銀行", RawText = data.RawText };

        var header = data.Rows.FirstOrDefault(r => r.Words.Any(w => w.Text == "交易日")
                                               && r.Words.Any(w => w.Text == "存入")
                                               && r.Words.Any(w => w.Text == "支出"));
        if (header is null)
        {
            s.Warnings.Add("找不到存款交易明細表頭（交易日/存入/支出）");
            return s;
        }

        // 表頭少一個欄位名就丢例外（.First()）會讓整份檔案一筆都進不去：記警告就好。
        string[] need = ["交易日", "摘要", "支出", "存入", "餘額"];
        var missing = header.MissingLabels(need);
        if (missing.Length > 0)
        {
            s.Warnings.Add($"存款交易明細表頭少了欄位（缺 {missing}），這份不拆明細。");
            return s;
        }
        WordBox Col(string text) => header.Label(text)!;                        // 上面查過了
        var dateCol = Col("交易日");
        var summaryCol = Col("摘要");
        var outCol = Col("支出");
        var inCol = Col("存入");
        var balanceCol = Col("餘額");
        var account = Regex.Match(data.RawText, @"帳號[:：]\s*([0-9*\-]+)\(([^)]+)\)").Groups;
        s.AccountMask = account[1].Success ? account[1].Value : "";
        // 段落標題「帳號:…(新臺幣)」就是這段明細的幣別原文；沒抓到就留空白（= 用帳戶自己的幣別），不猜 TWD。
        var sectionCurrency = account[2].Success ? account[2].Value : "";

        // 備註欄只是拿来切「接在上一筆下面的續行」，沒有也能記帳，所以只警告不擋。
        var noteCol = header.LabelLike("備註");
        if (noteCol is null) s.Warnings.Add("找不到備註欄：商家名的多行續行不會被接回來。");
        var noteLeft = noteCol is null ? double.MaxValue : noteCol.Left - 40;      // 沒有備註欄就乾脆不收續行，免得把整列吃進來
        var all = StatementTable.DetailRows(data, header).ToList();
        decimal? lastBalance = null;
        DateOnly? lastBalanceDate = null;

        decimal? Income(Row r) => r.Words.Where(w => w.BelongsTo(inCol, outCol, balanceCol)).Select(w => PdfText.ToDecimal(w.Text)).FirstOrDefault(a => a is not null);
        decimal? Expense(Row r) => r.Words.Where(w => w.BelongsTo(outCol, inCol, balanceCol)).Select(w => PdfText.ToDecimal(w.Text)).FirstOrDefault(a => a is not null);
        decimal? PrintedBalance(Row r) => r.Words.Where(w => w.BelongsTo(balanceCol, inCol, outCol)).Select(w => PdfText.ToDecimal(w.Text)).FirstOrDefault(a => a is not null);
        DateOnly? Date(Row r) => r.Words.Where(w => w.BelongsTo(dateCol, summaryCol)).Select(w => PdfText.ToDate(w.Text)).FirstOrDefault(d => d is not null);

        // 備註/資金用途常常跨好幾列，會被排到明細列的上方或下方，先全部收起來再照距離分配。
        var notes = all.Where(r => Date(r) is null || (Income(r) is null && Expense(r) is null))
                       .Select(r => (r.Page, r.Top, Text: Row.Join(r.Words.Where(w => w.Left >= noteLeft && !AccountBlob.IsMatch(w.Text))).Trim()))
                       .Where(n => n.Text.Length > 0)
                       .ToList();

        foreach (var row in all)
        {
            var date = Date(row);
            var income = Income(row);
            var expense = Expense(row);
            if (date is null || (income is null && expense is null)) continue;

            // 明細自帶 running 餘額：拿最後一列的那個當單上餘額（匯入端的「期初＋交易＝餘額」校驗吃它）。
            var printed = PrintedBalance(row);
            if (printed is not null) { lastBalance = printed; lastBalanceDate = date; }

            decimal amount = income ?? -expense!.Value;
            var extra = string.Join("", notes.Where(n => n.Page == row.Page && Math.Abs(n.Top - row.Top) <= 12).Select(n => n.Text));
            var summary = Row.Join(row.Words.Where(w => w.BelongsTo(summaryCol, dateCol) && !PdfText.IsMoney(w.Text)));

            s.Transactions.Add(new ParsedTransaction
            {
                Page = row.Page,
                Date = date.Value,
                Merchant = (summary + " " + extra).Trim(),
                Amount = amount,
                Currency = sectionCurrency,
                RawLine = Row.Join(row.Words),
            });
        }

        if (s.Transactions.Count == 0) { s.Warnings.Add("沒有解析到任何存款交易明細"); return s; }

        s.StatementStartDate = s.Transactions.Min(t => t.Date);
        s.StatementEndDate = s.Transactions.Max(t => t.Date);
        if (lastBalance is not null && lastBalanceDate is not null)
            s.Balances.Add(new ParsedBalance
            {
                AsOf = lastBalanceDate.Value, Amount = lastBalance.Value, Currency = sectionCurrency, Account = s.AccountMask,
            });
        else
            s.Warnings.Add("明細列沒帶餘額，這個帳戶不會進餘額校驗。");
        s.Warnings.Add("這份綜合對帳單只解析存款交易明細；幣別/帳號那張資產表與別家銀行信用卡應繳列尚未建模。");
        return s;
    }
}
