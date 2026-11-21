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
    // 「115/09月底餘額」：這是上月底的結算餘額（10 月的單子印 09 月底），不是本期期末，as_of 要照它講的那天。
    private static readonly Regex MonthEnd = new(@"(1\d{2})/(\d{1,2})月底餘額", RegexOptions.Compiled);
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

        // 表頭少一個欄位名就丢例外（.First()）會讓整份檔案一筆都進不去：記警告就好。
        string[] need = ["幣別", "帳號", "交易日期", "摘要", "支出", "存入", "餘額", "交易時間"];
        var missing = header.MissingLabels(need);
        if (missing.Length > 0)
        {
            s.Warnings.Add($"存款交易明細表頭少了欄位（缺 {missing}），這份不拆明細。");
            return s;
        }
        WordBox Col(string text) => header.Label(text)!;                        // 上面查過了
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

        var rows = StatementTable.DetailRows(data, header).ToList();
        var accountLines = new Dictionary<string, int>();
        var oddCurrency = new SortedSet<string>();

        foreach (var row in rows)
        {
            var joined = Row.Join(row.Words);
            var tight = joined.Replace(" ", "");
            if (tight.Contains("月底餘額"))
            {
                // 「新台幣 02010660350 16,254.00 115/09月底餘額」= 這個帳戶上月底的結算餘額，是漏解析的校驗輸入。
                var me = MonthEnd.Match(tight);
                var meAccount = row.Words.Where(w => w.Overlaps(accountCol)).Select(w => Account.Match(w.Text))
                                         .FirstOrDefault(m => m.Success)?.Value;
                var meAmount = row.Words.Where(w => w.Overlaps(balanceCol)).Select(w => PdfText.ToDecimal(w.Text))
                                        .FirstOrDefault(a => a is not null)
                                    ?? row.Words.Where(w => PdfText.IsMoney(w.Text)).Select(w => PdfText.ToDecimal(w.Text))
                                                .LastOrDefault(a => a is not null);
                var meCurrency = Row.Join(row.Words.Where(w => w.Overlaps(currencyCol)));
                if (me.Success && meAccount is not null && meAmount is not null && meCurrency.Length > 0)
                    s.Balances.Add(new ParsedBalance
                    {
                        AsOf = MonthEndDate(int.Parse(me.Groups[1].Value) + 1911, int.Parse(me.Groups[2].Value)),
                        Amount = meAmount.Value, Currency = meCurrency, Account = meAccount,
                    });
                continue;
            }
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

        // 「帳號 幣別 存款種類 存款餘額」那张帳戶總覽（截至 115/10/31 為止）。跟明細裡的上月底餘額一起存：
        // 兩個 as_of 不同，「期初 ＋ Σ交易 ＝ 期末」兩頭都能校驗（account_balance 的主鍵容得下同帳戶不同日期）。
        s.Balances.AddRange(ReadOverview(data));
        if (s.Balances.Count == 0)
            s.Warnings.Add("單上的餘額沒讀到（帳戶總覽與月底餘額列都沒對到），這個帳戶不會進餘額校驗。");
        return s;
    }

    /// <summary>月底：單子只寫「115/09」，校驗要看的是那天結算的數，所以補成該月最後一天。</summary>
    static DateOnly MonthEndDate(int year, int month) => new(year, month, DateTime.DaysInMonth(year, month));

    /// <summary>
    /// 頁首的帳戶總覽（帳號 / 幣別 / 存款種類 / 存款餘額 / 往來分行），帳期取自「截至115-10-31為止」。
    /// 只收活期：定期存單的「存款餘額」是面額，進 account_balance 會跟存摺交易對不上。
    /// </summary>
    static IEnumerable<ParsedBalance> ReadOverview(PageData data)
    {
        var table = data.Rows.FirstOrDefault(r => r.Words.Any(w => w.Text == "存款餘額")
                                               && r.Words.Any(w => w.Text == "存款種類"));
        if (table is null) yield break;
        var asOf = data.DateInRow("截至");
        if (asOf is null) yield break;

        var accountCol = table.Label("帳號");
        var currencyCol = table.Label("幣別");
        var balanceCol = table.Label("存款餘額")!;
        if (accountCol is null || currencyCol is null) yield break;

        // 往下收到遇到下一個表頭（明細的「交易日期」）為止。
        var stop = data.Rows.Where(r => r.Page == table.Page && r.Top < table.Top
                                     && r.Words.Any(w => w.Text == "交易日期"))
                            .Select(r => r.Top).DefaultIfEmpty(double.MinValue).Min();
        foreach (var row in data.Rows.Where(r => r.Page == table.Page && r.Top < table.Top && r.Top > stop))
        {
            var tight = Row.Join(row.Words).Replace(" ", "");
            if (!tight.Contains("活期")) continue;
            var account = row.Words.Where(w => w.Overlaps(accountCol)).Select(w => Account.Match(w.Text))
                                   .FirstOrDefault(m => m.Success)?.Value;
            var amount = row.Words.Where(w => w.Overlaps(balanceCol)).Select(w => PdfText.ToDecimal(w.Text))
                                  .FirstOrDefault(a => a is not null);
            var currency = Row.Join(row.Words.Where(w => w.Overlaps(currencyCol)));
            if (account is null || amount is null || currency.Length == 0) continue;
            yield return new ParsedBalance { AsOf = asOf.Value, Amount = amount.Value, Currency = currency, Account = account };
        }
    }
}
