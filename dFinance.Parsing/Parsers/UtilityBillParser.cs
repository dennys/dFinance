using System.Text.RegularExpressions;

namespace dFinance.Parsing;

/// <summary>
/// 非銀行的繳費單/帳單（電、水、瓦斯、電信、eTag 通行費）。
/// 目的是記帳：每份產生「付款對象 + 日期 + 金額」，能拿到帳單期間就一起放進商家名。
/// 這些單子上面也會印代收銀行，所以要在銀行辨識之前先跑，否則會被誤判成該銀行。
/// </summary>
public sealed class UtilityBillParser : IStatementParser
{
    private static readonly Regex Money = new(@"^[-+]?\$?(\d{1,3}(,\d{3})*|\d+)(\.\d{1,2})?元?$", RegexOptions.Compiled);
    private static readonly Regex MoneyLoose = new(@"^\$(\d{1,3}(,\d{3})*|\d+)", RegexOptions.Compiled);
    private static readonly Regex DateRx = new(@"(\d{3,4})[-/.](\d{1,2})[-/.](\d{1,2})", RegexOptions.Compiled);
    private static readonly Regex RocDateWords = new(@"(\d{3})\s*年\s*(\d{1,2})\s*月\s*(\d{1,2})\s*日", RegexOptions.Compiled);

    enum Bill { Electric, Water, Telecom, Gas, Etag }

    static Bill? Match(PageData data)
    {
        var text = data.RawText;
        return text switch
        {
            _ when text.Contains("電費通知") => Bill.Electric,
            _ when text.Contains("水費電子通知") || text.Contains("水費通知") => Bill.Water,
            _ when text.Contains("中華電信") && text.Contains("繳費期限") => Bill.Telecom,
            _ when text.Contains("天然氣費") => Bill.Gas,
            _ when text.Contains("eTag") && text.Contains("通行費") => Bill.Etag,
            _ => null,
        };
    }

    public bool CanParse(PageData data) => Match(data) is not null;

    public ParsedStatement Parse(PageData data) => Match(data) switch
    {
        Bill.Electric => Single("台灣電力公司", "電費", data, "應繳總金額", null),
        Bill.Water => Single("台灣自來水公司", "水費", data, "水費項目小計", "扣繳日期"),
        Bill.Telecom => Single("中華電信", "電信費", data, "應繳總金額", "繳費期限"),
        Bill.Gas => Single("台灣中油（天然氣）", "天然氣費", data, "扣繳總金額", "代繳帳號"),
        Bill.Etag => Etag(data),
        _ => new ParsedStatement { Issuer = "", RawText = data.RawText, Warnings = { "這份單子沒有適用的解析器。" } },
    };

    /// <summary>整份單子只有一筆應繳金額：金額/日期都用「標籤後面第一個數字/日期」找。</summary>
    static ParsedStatement Single(string issuer, string item, PageData data, string amountLabel, string? dateLabel)
    {
        var amount = AmountAfter(data, amountLabel);
        var due = dateLabel is null ? RowDate(data) : DateAfter(data, dateLabel);
        if (amount is null) return new ParsedStatement { Issuer = issuer, RawText = data.RawText, Warnings = { $"讀不到{issuer}的應繳金額（{amountLabel}）" } };

        var period = Period(data.RawText);
        var merchant = item + (period is null ? "" : $" {period.Value.Start:yyyy/MM/dd}~{period.Value.End:yyyy/MM/dd}");

        var s = new ParsedStatement
        {
            Issuer = issuer,
            RawText = data.RawText,
            TotalDue = amount,
            DueDate = due,
            Transactions =
            {
                new ParsedTransaction
                {
                    Date = due ?? period?.End ?? default,
                    Merchant = merchant,
                    Amount = amount.Value,
                    Kind = amount.Value < 0 ? "credit" : "charge",
                    Currency = "TWD",
                    Page = 1,
                    RawLine = data.Rows.FirstOrDefault(r => r.Words.Any(w => w.Text.Contains(amountLabel))) is { } r
                        ? Row.Join(r.Words) : amountLabel,
                },
            },
            StatementStartDate = period?.Start,
            StatementEndDate = period?.End,
        };
        if (amount == 0) s.Warnings.Add($"{issuer}本期應繳 0 元（退費/折抵抵掉了），記帳上這筆可以忽略。");
        return s;
    }

    /// <summary>eTag 通行費：一列一次通行，金額取最後一欄「實際扣(繳)款金額」。</summary>
    static ParsedStatement Etag(PageData data)
    {
        var s = new ParsedStatement { Issuer = "遠通電收（eTag）", RawText = data.RawText };
        int zero = 0;

        foreach (var row in data.Rows)
        {
            var money = row.Words.Where(w => Money.IsMatch(w.Text) && w.Text.StartsWith('$')).ToList();
            var date = row.Words.Select(w => DateRx.Match(w.Text)).Where(m => m.Success).Select(ToDate).FirstOrDefault();
            if (money.Count < 5 || date is null) continue;

            var amount = ToDecimal(money[^1].Text);
            if (amount == 0) { zero++; continue; }

            s.Transactions.Add(new ParsedTransaction
            {
                Date = date.Value,
                Merchant = "通行費",
                Amount = amount,
                Kind = "charge",
                Currency = "TWD",
                Page = row.Page,
                RawLine = Row.Join(row.Words),
            });
        }

        s.TotalDue = AmountAfter(data, "實際扣(繳)款金額合計");
        if (s.Transactions.Count > 0)
        {
            s.StatementStartDate = s.Transactions.Min(t => t.Date);
            s.StatementEndDate = s.Transactions.Max(t => t.Date);
        }
        if (zero > 0) s.Warnings.Add($"另有 {zero} 筆金額 0 的通行（優惠抵扣）未列入明細。");
        if (s.TotalDue is not null && s.Transactions.Sum(t => t.Amount) != s.TotalDue)
            s.Warnings.Add($"明細合計 {s.Transactions.Sum(t => t.Amount)} 與單據「實際扣(繳)款金額合計」{s.TotalDue} 不符。");
        return s;
    }

    /// <summary>標籤所在列之後（沒有就看下一列）的第一個金額。用字詞比對，不會把日期的一部分當成金額。</summary>
    static decimal? AmountAfter(PageData data, string label)
    {
        foreach (var w in WordsAfterLabel(data, label))
            if (Money.IsMatch(w) || MoneyLoose.IsMatch(w)) return ToDecimal(w);
        return null;
    }

    static DateOnly? DateAfter(PageData data, string label)
    {
        foreach (var w in WordsAfterLabel(data, label))
        {
            var m = DateRx.Match(w);
            if (m.Success && ToDate(m) is { } d) return d;
        }
        return null;
    }

    static IEnumerable<string> WordsAfterLabel(PageData data, string label)
    {
        for (int i = 0; i < data.Rows.Count; i++)
        {
            var row = data.Rows[i];
            int k = row.Words.ToList().FindIndex(w => w.Text.Contains(label));
            if (k < 0) continue;
            for (int j = k + 1; j < row.Words.Count; j++) yield return row.Words[j].Text;
            if (i + 1 < data.Rows.Count)
                foreach (var w in data.Rows[i + 1].Words) yield return w.Text;
        }
    }
    /// <summary>沒有標籤可找的（台電）：繳費截止日就跟在掩碼金額 ＊＊＊＊ 同一列。</summary>
    static DateOnly? RowDate(PageData data)
    {
        var row = data.Rows.FirstOrDefault(r => r.Words.Any(w => w.Text.Contains("＊＊＊＊")));
        if (row is null) return null;
        var m = row.Words.Select(w => DateRx.Match(w.Text)).FirstOrDefault(x => x.Success && ToDate(x) is not null);
        return m is null || !m.Success ? null : ToDate(m);
    }

    static (DateOnly Start, DateOnly End)? Period(string text)
    {
        var m = Regex.Match(text, @"(\d{3,4}[-+/.]\d{1,2}[-+/.]\d{1,2})\s*[–至-]\s*(\d{3,4}[-+/.]\d{1,2}[-+/.]\d{1,2})");
        if (m.Success)
        {
            var a = ToDate(DateRx.Match(m.Groups[1].Value));
            var b = ToDate(DateRx.Match(m.Groups[2].Value));
            if (a is not null && b is not null) return (a.Value, b.Value);
        }
        // 中油天然氣用「115年08月16日 起至 115年10月16日」
        var all = RocDateWords.Matches(text);
        if (all.Count >= 2)
        {
            var c = ToDate(all[0]);
            var e = ToDate(all[1]);
            if (c is not null && e is not null) return (c.Value, e.Value);
        }
        return null;
    }

    static decimal ToDecimal(string text) =>
        decimal.Parse(text.Replace("$", "").Replace(",", "").Replace("元", ""), System.Globalization.CultureInfo.InvariantCulture);

    static DateOnly? ToDate(Match m)
    {
        int year = int.Parse(m.Groups[1].Value);
        if (year < 1000) year += 1911;   // 民國年
        // 單子上的編號（例：06-22-1330-57-1）也會被日期樣式抓到，所以範圍不對就當不是日期。
        return DateOnly.TryParseExact($"{year}-{m.Groups[2].Value}-{m.Groups[3].Value}", "yyyy-M-d", out var d) ? d : null;
    }}
