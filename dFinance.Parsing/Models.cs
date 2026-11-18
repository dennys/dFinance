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

    public List<string> Warnings { get; set; } = new();

    /// <summary>抽出的原始文字層，給人在 UI 上核對用。</summary>
    public string RawText { get; set; } = "";
}

public class ParsedTransaction
{
    public DateOnly Date { get; set; }

    public DateOnly? PostingDate { get; set; }

    public string Merchant { get; set; } = "";

    public decimal Amount { get; set; }

    /// <summary>charge = 消費, credit = 退款/回饋（金額為負）。</summary>
    public string Kind { get; set; } = "charge";

    public string Currency { get; set; } = "";

    public string Card { get; set; } = "";

    public int Page { get; set; }

    public string RawLine { get; set; } = "";
}
