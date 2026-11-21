namespace dFinance.Parsing;

public class ParsedStatement
{
    public string Issuer { get; set; } = "";

    public DateOnly? StatementStartDate { get; set; }

    public DateOnly? StatementEndDate { get; set; }

    public DateOnly? DueDate { get; set; }

    public decimal? TotalDue { get; set; }

    public decimal? MinimumDue { get; set; }

    public string AccountMask { get; set; } = "";

    /// <summary>解析這份的 parser 類名（&lt;銀行&gt;&lt;Card|Deposit|Bill&gt;Parser）。落庫時記進 document.parser，也用它判斷單據種類。</summary>
    public string Parser { get; set; } = "";

    public List<ParsedTransaction> Transactions { get; set; } = new();

    /// <summary>
    /// 單子上印的餘額（存款對帳單）。匯入端寫進 <c>account_balance</c>（source='statement'），
    /// 「交易累加 ＝ 單上餘額」的自動校驗就是吃這個 —— 漏解析會從這裡被照出來，比「解析到 0 筆才警告」強。
    /// </summary>
    public List<ParsedBalance> Balances { get; set; } = new();

    public List<string> Warnings { get; set; } = new();

    /// <summary>抽出的原始文字層，給人在 UI 上核對用。</summary>
    public string RawText { get; set; } = "";
}

/// <summary>
/// 單子上印的一個餘額。重點是 <see cref="AsOf"/>：單子上的數字是「那天結算出來的數」，
/// 不是現在的餘額（兆豐 10 月的單子印的是 115/09 月底餘額）。校驗端要照 <see cref="AsOf"/> 把
/// 之後的交易排除，不然期初餘額會天天報對不上。
/// </summary>
public class ParsedBalance
{
    /// <summary>這個餘額是「哪一天結算的」。單子上沒寫就拿那份單子的帳期末日。</summary>
    public DateOnly AsOf { get; set; }

    public decimal Amount { get; set; }

    /// <summary><see cref="Amount"/> 的幣別，照單子原文交（「美金」）；空白 = 用該帳戶的幣別。</summary>
    public string Currency { get; set; } = "";

    /// <summary>列級帳號（一份單子含多帳號時用）；空白 = 用整份的 <see cref="ParsedStatement.AccountMask"/>。</summary>
    public string Account { get; set; } = "";
}

public class ParsedTransaction
{
    public DateOnly Date { get; set; }

    public DateOnly? PostingDate { get; set; }

    public string Merchant { get; set; } = "";

    public decimal Amount { get; set; }

    /// <summary>
    /// 只給人看的參考：匯入一律照金額正負號決定 entry.kind，不讀這個欄位。
    /// 兩組用法：信用卡帳單 charge=消費 / credit=退款；存款對帳單 charge=存入 / credit=支出（銀行的借貸記法）。
    /// shortcut: 真要拿它決定入帳之前，先把兩組語意收成一個（TODO 2b 拆 transfer 時一起做）。
    /// </summary>
    public string Kind { get; set; } = "charge";

    /// <summary>
    /// 這列是「繳卡費」而不是消費（由 <see cref="StatementParser.LooksLikePayment"/> 判）。
    /// 繳款不是支出：它是從另一個帳戶打錢進來把卡帳抵掉，匯入端記成 transfer；
    /// 認不出扣款帳戶時才退回原本的 refund。跟 <see cref="Kind"/> 的借貸記法無關，不要混用。
    /// </summary>
    public bool IsPayment { get; set; }

    /// <summary>
    /// <b> Amount 那個金額的幣別</b>（不是「這筆原來的幣別」）。存款單就是列上的幣別；
    /// 台幣卡帳的消費要填 TWD（金額取「台幣金額」欄），海外消費的原幣屬於
    /// <c>entry.orig_currency</c>/orig_amount_minor（尚未實作，見 TODO：富邦/匯豐在這裡會警告）。
    /// 照單子上的原文交（「美金」），對成代碼是匯入端靠 <c>currency.aliases</c> 做的事。
    /// </summary>
    public string Currency { get; set; } = "";

    public string Card { get; set; } = "";

    /// <summary>列級帳號。一份單子含多個帳號時（存款對帳單）由 parser 填，匯入端逐列歸戶；空白 = 用整份的 AccountMask。</summary>
    public string Account { get; set; } = "";

    public int Page { get; set; }

    public string RawLine { get; set; } = "";
}
