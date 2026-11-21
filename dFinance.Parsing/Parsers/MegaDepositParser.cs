using System.Text.RegularExpressions;

namespace dFinance.Parsing;

/// <summary>
/// 兆豐國際商業銀行 存款對帳單（MegaAttachmentFile *.pdf：資產配置總覽 → 帳戶總覽 → 存摺存款明細）。
/// 一份單子常常含多個帳號（甚至外幣帳號），所以每列自己帶帳號與幣別，由匯入端逐列歸戶。
/// 金額看字落在「支出」還是「存入」欄：支出當負數、存入當正數（跟聯邦/永豐存款單同一套符號）。
/// </summary>
public sealed class MegaDepositParser : IStatementParser
{
    // 帳號欄：純數字（各分行位數不同，9 碼起）。
    private static readonly Regex Account = new(@"^\d{9,}$", RegexOptions.Compiled);
    private static readonly string[] SkipWords = ["月底餘額", "本月無交易明細", "存摺存款", "交易期間", "帳戶總覽", "存款種類", "存單面額"];

    // 發動行/備註那帶會出現純數字（例：外幣列的台幣對照額 159.090000），那種不該混進商家名；
    // 但帳號也是一串數字，所以要帶小數點才算金額。
    static bool LooksLikeAmount(string text) => text.Contains('.') && decimal.TryParse(text.Replace(",", ""), out _);

    // 不用 IssuerNames.IsPrimary：這份單子的自動扣繳列寫了台新/永豐/中信好幾次，“台新銀行”出現次數比“兆豐”還多。
    public bool CanParse(PageData data) => IssuerNames.Mentions(data, "兆豐國際商業銀行") > 0
                                        && data.RawText.Contains("存摺存款")
                                        && !data.RawText.Contains("本期待繳總額");

    public ParsedStatement Parse(PageData data)
    {
        var s = new ParsedStatement { Issuer = "兆豐國際商業銀行", RawText = data.RawText };

        var header = data.Rows.FirstOrDefault(r => r.Words.Any(w => w.Text == "交易日期")
                                               && r.Words.Any(w => w.Text == "摘要")
                                               && r.Words.Any(w => w.Text == "存入")
                                               && r.Words.Any(w => w.Text == "餘額"));
        if (header is null)
        {
            s.Warnings.Add("找不到存款交易明細表頭（交易日期/摘要/存入/餘額）");
            return s;
        }

        WordBox Col(string text) => header.Words.First(w => w.Text == text);
        var currencyCol = Col("幣別");
        var accountCol = Col("帳號");
        var dateCol = Col("交易日期");
        var summaryCol = Col("摘要");
        var outCol = Col("支出");
        var inCol = Col("存入");
        var balanceCol = Col("餘額");
        var timeCol = Col("交易時間");

        // 金額欄用欄位範圍對，不用 BelongsTo：帳號那欄的長串數字形状也像金額，會被nearest 判進支出欄。
        decimal? Income(Row r) => r.Words.Where(w => w.Overlaps(inCol)).Select(w => PdfText.ToDecimal(w.Text)).FirstOrDefault(a => a is not null);
        decimal? Expense(Row r) => r.Words.Where(w => w.Overlaps(outCol)).Select(w => PdfText.ToDecimal(w.Text)).FirstOrDefault(a => a is not null);

        var rows = data.Rows.Where(r => r.Page > header.Page || (r.Page == header.Page && r.Top < header.Top))
                            .OrderBy(r => r.Page).ThenByDescending(r => r.Top).ToList();
        var accountLines = new Dictionary<string, int>();
        var oddCurrency = new SortedSet<string>();

        foreach (var row in rows)
        {
            var joined = Row.Join(row.Words);
            var tight = joined.Replace(" ", "");
            if (SkipWords.Any(tight.Contains)) continue;

            var account = row.Words.Where(w => w.Overlaps(accountCol)).Select(w => Account.Match(w.Text))
                                   .FirstOrDefault(m => m.Success)?.Value;
            var date = row.Words.Where(w => w.Overlaps(dateCol)).Select(w => PdfText.ToDate(w.Text)).FirstOrDefault(d => d is not null);
            var income = Income(row);
            var expense = Expense(row);
            if (account is null || date is null || (income is null && expense is null)) continue;   // 說明列/分隔線/月底餘額列

            // 摘要常常被拆成一格一格（薪 資、現 金）；這欄只有中文，接起來時把空格去掉。
            var summary = Row.Join(row.Words.Where(w => w.Overlaps(summaryCol))).Replace(" ", "");
            var counterparty = Row.Join(row.Words.Where(w => w.Left > balanceCol.Right && !w.Overlaps(timeCol) && !LooksLikeAmount(w.Text)));
            // 幣別交原文（「美金」「日圓」），由匯入端靠 currency.aliases 對成代碼 —— parser 不查資料庫。
            var currencyWord = Row.Join(row.Words.Where(w => w.Overlaps(currencyCol)));
            if (!currencyWord.Contains("台幣") && !currencyWord.Contains("臺幣")) oddCurrency.Add(currencyWord);
            accountLines[account] = accountLines.GetValueOrDefault(account) + 1;

            s.Transactions.Add(new ParsedTransaction
            {
                Page = row.Page,
                Date = date.Value,
                Merchant = (summary + " " + counterparty).Trim() is { Length: > 0 } m ? m : "（未載明摘要）",
                Amount = income ?? -expense!.Value,
                Kind = income is null ? "credit" : "charge",
                Currency = currencyWord,
                Account = account,
                RawLine = joined,
            });
        }

        if (s.Transactions.Count == 0) { s.Warnings.Add("沒有解析到任何存款交易明細"); return s; }

        s.AccountMask = accountLines.OrderByDescending(kv => kv.Value).First().Key;
        s.StatementStartDate = s.Transactions.Min(t => t.Date);
        s.StatementEndDate = s.Transactions.Max(t => t.Date);

        if (accountLines.Count > 1)
            s.Warnings.Add($"這份含 {accountLines.Count} 個帳號（{string.Join("、", accountLines.Keys)}），已照列級帳號分別歸戶。");
        if (oddCurrency.Count > 0)
            s.Warnings.Add($"有非台幣列（{string.Join("、", oddCurrency)}）；帳戶會按幣別分開，但沒匯率就不計入淨值，請到「工具→幣別…」確認匯率。");
        s.Warnings.Add("存款單的餘額欄（月底餘額/餘額列）沒有建模（總資產/餘額校驗在 TODO 上）。");
        return s;
    }
}
