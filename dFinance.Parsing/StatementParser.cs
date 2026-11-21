namespace dFinance.Parsing;

/// <summary>
/// 對外唯一的入口：PDF 路徑 + 密碼 → ParsedStatement。
/// 讀文字層、找解析器、補共通警告；各銀行的規則在 Parsers/ 底下。
/// </summary>
public static class StatementParser
{
    /// <summary>改動任何 parser 的解析規則就 +1。落庫時寫進 document.parser_ver，重匯與「哪些列是舊版解析的」都靠它。</summary>
    public const int Version = 4;   // v4：Kind 刪掉（它只是 Amount 正負號的複製）、卡帳/存款單的幣別照單上原文交、中信/永豐存款單補上單上餘額

    // 公開面只有這一層（要拆出去發 dll 時，這就是 API 審查看板）：
    //   主入口：Parse(...) → ParsedStatement（唯一承諾的外部契約，欄位口徑見 Models.cs 與 ParserContractTests）。
    //   公開但不是契約（只是 devcheck/測試要拿来除錯，改了得先問他們）：
    //     PdfRows.Read、OcrReader.Read/ModelsDirectory、StatementTable.Find/DetailRows、PdfText.Is*/ToDecimal、
    //     Row.Join/JoinDedup、StatementParser.LooksLikePayment/WarnIfDueMismatch、ParserFactory、IssuerNames。
    // 那些东西现在是 public 只是因为同 repo 要拿；真要 pack 成 nupkg 前先想清楚要不承诺。

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

        // 卡帳上的繳款列不是消費（那是「從別個帳戶打錢進來把卡帳抵掉」）。在這裡認一次，
        // 八支卡帳 parser 就不必各寫一份，匯入端也能把它記成 transfer 而不是 refund。
        if (s.Parser.EndsWith("CardParser", StringComparison.Ordinal))
            foreach (var t in s.Transactions) t.IsPayment = LooksLikePayment(t);

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

    // 繳款列的判斷：金額是负的（銀行把它記成抵掉應繳）而且商家寫的是「打錢進來還卡」那類字。
    // 詞表是樣本上各家自己印的寫法（已驗證）：「已繳款金額-本行帳戶扣款」「兆豐商銀自扣入帳」
    // 「...自動轉帳扣繳台新信用卡款」「兆豐銀扣」。現金回饋（會直接抵應繳）不是繳款，
    // 要留在消費合計裡，所以故意不收「退款」；宁願漏判（只是多一筆 refund）也不要誤判（誤判會憑空變轉帳）。
    static readonly string[] PaymentWords = ["繳款", "還款", "扣款", "扣繳", "自扣", "銀扣", "儲值"];

    /// <summary>這列是不是「繳卡費」而不是消費。</summary>
    public static bool LooksLikePayment(ParsedTransaction t) =>
        t.Amount < 0 && PaymentWords.Any(w => t.Merchant.Contains(w));

    /// <summary>
    /// 「明細合計 ＝ 本期應繳」的校驗（各卡帳 parser 共用，口徑只留一份）。
    /// 繳款列是轉帳不是消費，要先從合計裡扣掉；排掉之後還對不上才是真的漏列
    /// —— 之前繳款列混在合計裡，那個訊號會被掩蓋掉。
    /// </summary>
    public static void WarnIfDueMismatch(ParsedStatement s, string label)
    {
        if (s.TotalDue is null || s.Transactions.Count == 0) return;
        var spend = s.Transactions.Where(t => !LooksLikePayment(t)).Sum(t => t.Amount);
        if (spend == s.TotalDue) return;
        int payments = s.Transactions.Count(LooksLikePayment);
        s.Warnings.Add($"扣掉 {payments} 筆繳款後的明細合計 {spend:N0} 不等於{label} {s.TotalDue:N0}（可能漏列，請比對原始單據）。");
    }
}
