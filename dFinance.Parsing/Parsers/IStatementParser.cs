namespace dFinance.Parsing;

/// <summary>
/// 一份單據一種 parser：自己認自己的單（CanParse = 這家銀行 + 這個版面的指紋），自己解析（Parse）。
///
/// 命名規則：&lt;銀行&gt;&lt;單據種類&gt;Parser，單據種類只用這三個詞——
///   Card    = 信用卡帳單（有應繳金額／最低應繳金額）
///   Deposit = 存款對帳單（有帳戶交易明細，含綜合對帳單裡的存款那段）
///   Bill    = 非銀行的繳費單（電/水/瓦斯/電信/eTag）
/// 外面的入口只有一個 StatementParser.Parse(path, password)，class 名字裡不要再出現 Statement，
/// 免得跟那個入口搞混。新增銀行就新增一個 class 並掛進 ParserFactory。
/// </summary>
public interface IStatementParser
{
    bool CanParse(PageData data);

    /// <summary>至少要填 Issuer 與 Transactions；讀不到的地方放 Warnings，不要靜靜回空。</summary>
    ParsedStatement Parse(PageData data);
}
