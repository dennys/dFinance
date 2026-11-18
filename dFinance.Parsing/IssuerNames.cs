namespace dFinance.Parsing;

/// <summary>銀行關鍵字表。同一家可能有全名/縮寫/網域等不同線索。</summary>
public static class IssuerNames
{
    public static readonly (string Key, string Issuer)[] All =
    [
        ("聯邦", "聯邦商業銀行"),
        ("中國信託", "中國信託商業銀行"),
        ("ctbcbank", "中國信託商業銀行"),
        ("玉山", "玉山商業銀行"),
        ("國泰世華", "國泰世華商業銀行"),
        ("cathaybk", "國泰世華商業銀行"),
        ("台北富邦", "台北富邦商業銀行"),
        ("fubon", "台北富邦商業銀行"),
        ("台新國際商業", "台新國際商業銀行"),
        ("台新銀行", "台新國際商業銀行"),
        ("taishinbank", "台新國際商業銀行"),
        ("永豐", "永豐商業銀行"),
        ("sinopac", "永豐商業銀行"),
        ("王道", "王道商業銀行"),
        ("obank", "王道商業銀行"),
        ("渣打", "渣打國際商業銀行"),
        ("sc.com", "渣打國際商業銀行"),
        ("兆豐", "兆豐國際商業銀行"),
        ("HSBC", "匯豐銀行"),
        ("遠通", "遠通電收（eTag）"),
    ];

    public static int Mentions(PageData data, string bank)
        => All.Where(b => b.Issuer == bank).Sum(b => Count(data, b.Key));

    /// <summary>
    /// 這份單子是不是屬於這家銀行。單子上常出現別家銀行（代收/自動扣繳銀行、
    /// 綜合對帳單裡列的別家信用卡），所以要數出現次數、最多的才算單子的主人。
    /// </summary>
    public static bool IsPrimary(PageData data, string bank)
    {
        int mine = Mentions(data, bank);
        if (mine == 0) return false;
        return All.Select(b => Mentions(data, b.Issuer)).Max() <= mine;
    }

    /// <summary>最容易誤判的情況：繳費單上的「繳費銀行」被當成單子的主人。</summary>
    public static string Detected(PageData data)
        => All.Select(b => (b.Issuer, N: Count(data, b.Key)))
              .Where(x => x.N > 0)
              .OrderByDescending(x => x.N)
              .Select(x => x.Issuer)
              .FirstOrDefault() ?? "";

    static int Count(PageData data, string key)
    {
        int n = 0, i = 0;
        while ((i = data.RawText.IndexOf(key, i, StringComparison.OrdinalIgnoreCase)) >= 0) { n++; i += key.Length; }
        return n;
    }
}
