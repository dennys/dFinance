namespace dFinance.Parsing;

/// <summary>
/// 誰來解析這份單子：照清單順序問，第一個說「我認得這個版面」的贏。
/// 新增銀行 = 寫一個 class 實作 IStatementParser，再掛到這個清單，不用改別人。
/// 順序有意義：繳費單要排最前面（它們上面印了代收銀行），同一家銀行的信用卡版和存款版也要能分辨。
/// </summary>
public static class ParserFactory
{
    static readonly IStatementParser[] Parsers =
    [
        new UtilityBillParser(),
        new FederalCardParser(),
        new FederalDepositParser(),
        new FubonCardParser(),
        new TaishinCardParser(),
        new WangdaoDepositParser(),
        new MegaCardParser(),
        new MegaDepositParser(),
        new SinopacCardParser(),
        new SinopacDepositParser(),
        new CathayCardParser(),
        new HsbcCardParser(),      // 要 OCR，放最後（沒模型就不認這份）
        new CtbcCardParser(),
        new CtbcDepositParser(),
    ];

    public static IStatementParser? Create(PageData data) => Parsers.FirstOrDefault(p => p.CanParse(data));
}
