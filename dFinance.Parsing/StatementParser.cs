namespace dFinance.Parsing;

/// <summary>
/// 對外唯一的入口：PDF 路徑 + 密碼 → ParsedStatement。
/// 讀文字層、找解析器、補共通警告；各銀行的規則在 Parsers/ 底下。
/// </summary>
public static class StatementParser
{
    /// <summary>改動任何 parser 的解析規則就 +1。落庫時寫進 document.parser_ver，重匯與「哪些列是舊版解析的」都靠它。</summary>
    public const int Version = 2;   // v2：圖片式帳單走 OCR（ValueFor 改優先取文字層、中信/匯豐 parser 上線）

    public static ParsedStatement Parse(string path, string? password) => Parse(path, password, null, null);

    /// <summary>dataDirectory = data\ 的位置；OCR 結果快取在 data\ocr\（不給就每份重 OCR）。</summary>
    public static ParsedStatement Parse(string path, string? password, string? dataDirectory, IProgress<string>? progress = null)
    {
        var data = PdfRows.Read(path, password);

        // 圖片多不代表讀不出來（富邦、永豐都是圖片多但文字層完整），要看中文字數量。
        if (!data.HasChinese)
        {
            var ocr = OcrReader.Read(path, password, dataDirectory, progress);
            if (ocr is not null) data = data.WithOcr(ocr);
        }

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

        if (!data.HasChinese)
            s.Warnings.Add(data.OcrApplied
                ? "中文是圖片式，已用 OCR 補（金額仍以文字層為準）。"
                : "文字層幾乎沒有中文（頁面是圖片），需要 OCR：把 ch_PP-OCRv5_rec_mobile.onnx 跟 ppocrv5_dict.txt 放進 data\\ocr_models\\（或設 DFINANCE_OCR_MODELS）。");

        return s;
    }
}
