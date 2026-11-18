using System.Text.RegularExpressions;
using System.Globalization;

namespace dFinance.Parsing;

/// <summary>
/// 王道商業銀行（obank.tw）存款對帳單。
/// 存款單沒有應繳金額，只有交易明細（金額自帶正負號）。
/// </summary>
public sealed class WangdaoDepositParser : IStatementParser
{
    // 日期分隔號在不同的王道 PDF 可能是 '-'、'/' 或 U+2010 那一類連字號；金額負號也可能是 U+2212。
    static readonly Regex DateRe = new(@"^(\d{4})[/\.\u2010-\u2015](\d{1,2})[/\.\u2010-\u2015](\d{1,2})$", RegexOptions.Compiled);
    static readonly Regex MoneyRe = new(@"^([+\-\u2212])\$?([\d,]+(?:[./]\d+)?)$", RegexOptions.Compiled);
    static readonly Regex AccountRe = new(@"帳號[:：]\s*([0-9*]+)", RegexOptions.Compiled);
    static readonly Regex ForeignTotalRe = new(@"外幣活期存款總餘額[^0-9]*([\d,]+)\s*元", RegexOptions.Compiled);

    static readonly (string Name, string Code)[] Currencies =
    {
        ("美元", "USD"), ("日元", "JPY"), ("人民幣", "CNY"), ("歐元", "EUR"), ("英鎊", "GBP"),
        ("澳幣", "AUD"), ("紐元", "NZD"), ("加拿大幣", "CAD"), ("瑞士法郎", "CHF"), ("南非幣", "ZAR"),
        ("臺幣", "TWD"), ("台幣", "TWD"),
    };

    public bool CanParse(PageData data) => IssuerNames.IsPrimary(data, "王道商業銀行");

    public ParsedStatement Parse(PageData data)
    {
        var s = ParseRows(data.Rows) ?? new ParsedStatement();
        s.Issuer = "王道商業銀行";
        s.RawText = data.RawText;
        return s;
    }

    static ParsedStatement? ParseRows(IReadOnlyList<Row> rows)
    {
        var s = new ParsedStatement();
        string account = "", currency = "TWD";
        int fixedDepositLines = 0;
        int tableHeaders = 0;                                    // 有幾個「交易日期 摘要 支出/收入」段落
        string? foreignTotal = null;                              // 外幣活期存款總餘額（約等值臺幣）

        foreach (var row in rows)
        {
            var text = Row.Join(row.Words);

            var acct = AccountRe.Match(text);
            if (acct.Success)
            {
                account = acct.Groups[1].Value;
                continue;
            }

            var cur = Currencies.FirstOrDefault(c => text.StartsWith(c.Name + "：") || text.StartsWith(c.Name + ":"));
            if (cur != default)
            {
                currency = cur.Code;
                continue;
            }

            if (text.StartsWith("交易日期") && text.Contains("摘要")) { tableHeaders++; continue; }

            var f = ForeignTotalRe.Match(text);
            if (f.Success) { foreignTotal = f.Groups[1].Value; continue; }

            if (text.Contains("到期領息") || text.Contains("到期領回"))
            {
                fixedDepositLines++;
                continue;
            }

            if (row.Words.Count == 0 || !DateRe.IsMatch(row.Words[0].Text)) continue;
            var money = row.Words.FirstOrDefault(w => MoneyRe.IsMatch(w.Text));
            if (money is null) continue;

            var d = DateRe.Match(row.Words[0].Text);
            var m = MoneyRe.Match(money.Text);
            var merchant = Row.Join(row.Words.Skip(1).Where(w => w != money));
            decimal amount = decimal.Parse(m.Groups[2].Value.Replace('/', '.'), NumberStyles.Number, CultureInfo.InvariantCulture)
                             * (m.Groups[1].Value.StartsWith('-') || m.Groups[1].Value == "\u2212" ? -1m : 1m);
            if (merchant.Length == 0) merchant = "不明交易";

            s.Transactions.Add(new ParsedTransaction
            {
                Page = row.Page,
                Date = new DateOnly(int.Parse(d.Groups[1].Value), int.Parse(d.Groups[2].Value), int.Parse(d.Groups[3].Value)),
                Merchant = merchant,
                Amount = amount,
                Currency = currency,
                Kind = amount < 0 ? "debit" : "credit",      // 支出 = debit，收入/退款 = credit
                RawLine = text,
            });
        }

        if (s.Transactions.Count == 0) return null;

        s.AccountMask = account;
        // 對帳單上沒有清楚的帳期欄位，用明細的首末日期當帳期。
        s.StatementStartDate = s.Transactions.Min(t => t.Date);
        s.StatementEndDate = s.Transactions.Max(t => t.Date);
        if (fixedDepositLines > 0)
            s.Warnings.Add($"另有 {fixedDepositLines} 筆定期存款（到期日/利率）未列入明細，v1 不處理。");
        if (foreignTotal is not null)
            s.Warnings.Add($"外幣活期存款總餘額（約等值臺幣）{foreignTotal} 元沒進模型，只記了外幣交易明細。");
        if (tableHeaders > s.Transactions.Count)
            s.Warnings.Add($"有 {tableHeaders} 個明細段落但只解析到 {s.Transactions.Count} 筆：其餘可能是本期沒交易，也可能是版面對不上。");

        return s;
    }
}
