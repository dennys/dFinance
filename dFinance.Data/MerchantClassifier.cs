namespace dFinance.Data;

/// <summary>
/// 依商家名稱關鍵字建議分類（只給建議，不寫入資料庫）。
/// 規則順序有意義：先比到的先贏，所以越具體的關鍵字放越前面。
/// </summary>
public static class MerchantClassifier
{
    static readonly (string Category, string[] Keywords)[] Rules =
    [
        ("交通", ["悠遊卡", "一卡通", "高鐵", "台鐵", "臺鐵", "捷運", "Uber", "Taxi", "計程車", "停車", "中油", "台灣中油", "加油", "Foodpanda外送交通"]),
        ("水電瓦斯", ["台電", "臺灣電力", "台水", "臺灣自來水", "天然氣", "瓦斯"]),
        ("通訊網路", ["中華電信", "遠傳", "台灣大哥大", "亞太電信", "Hinet", "Netflix網路"]),
        ("訂閱軟體", ["Apple.com/bill", "APPLE.COM", "Google", "Microsoft", "OpenAI", "ChatGPT", "Spotify", "YouTube", "Adobe", "iCloud", "Dropbox", "Notion"]),
        ("醫療保健", ["診所", "醫院", "藥局", "康是美", "屈臣氏", "丁丁藥局", "藥妝"]),
        ("保險", ["保險", "人壽", "產險", "?險"]),
        ("稅金", ["稅", "罰鍰", "規費"]),
        ("居住", ["房租", "管理費", "物業", "瓦斯費"]),
        ("購物", ["momo", "蝦皮", "Shopee", "PChome", "博客來", "Costco", "好市多", "IKEA", "宜家", "Uniqlo", "UNIQLO", "全聯", "家樂福", "大潤發", "愛買", "Amazon", "AMAZON", "Yahoo購物"]),
        ("餐飲", ["麥當勞", "肯德基", "星巴克", "7-ELEVEN", "7-11", "全家", "萊爾富", "OK超商", "路易莎", "CAFE", "Cafe", "咖啡", "餐廳", "小吃", "便當", "麵", "飯", "火鍋", "Foodpanda", "UberEats", "Uber Eats"]),
    ];

    /// <summary>回傳建議的分類名稱（與種子資料的 name 相同）；比不到回傳 null。</summary>
    public static string? Suggest(string? merchant)
    {
        if (string.IsNullOrWhiteSpace(merchant)) return null;
        foreach (var (category, keywords) in Rules)
            if (keywords.Any(k => merchant.Contains(k, StringComparison.OrdinalIgnoreCase)))
                return category;
        return null;
    }
}
