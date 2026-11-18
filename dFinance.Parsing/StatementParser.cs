namespace dFinance.Parsing;

/// <summary>
/// 對外唯一的入口：PDF 路徑 + 密碼 → ParsedStatement。
/// 讀文字層、找解析器、補共通警告；各銀行的規則在 Parsers/ 底下。
/// </summary>
public static class StatementParser
{
    /// <summary>改動任何 parser 的解析規則就 +1。落庫時寫進 document.parser_ver，重匯與「哪些列是舊版解析的」都靠它。</summary>
    public const int Version = 1;

    public static ParsedStatement Parse(string path, string? password)
    {
        var data = PdfRows.Read(path, password);
        var parser = ParserFactory.Create(data);
        var s = parser?.Parse(data) ?? new ParsedStatement();
        s.RawText = data.RawText;
        s.Parser = parser?.GetType().Name ?? "";
        if (string.IsNullOrEmpty(s.Issuer)) s.Issuer = IssuerNames.Detected(data);

        if (parser is null)
            s.Warnings.Add(s.Issuer == "" ? "辨識不出銀行，只顯示原始文字。"
                                        : $"{s.Issuer}：沒有適用的解析器（格式跟已知的樣本不同）。");
        else if (s.Transactions.Count == 0)
            s.Warnings.Add($"{s.Issuer}：讀不到交易明細（格式跟已知的樣本不同，或是這份沒有明細）。");

        // 圖片多不代表讀不出來（富邦、永豐都是圖片多但文字層完整），要看中文字數量。
        if (data.RawText.Count(c => c >= 0x4E00 && c <= 0x9FFF) < 50)
            s.Warnings.Add("文字層幾乎沒有中文（頁面是圖片），需要 OCR，目前未實作。");

        return s;
    }
}
