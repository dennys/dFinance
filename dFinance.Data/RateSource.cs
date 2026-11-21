using dFinance.Data.Localization;
using System.Net;
using System.Text.Json;

namespace dFinance.Data;

/// <summary>
/// 從 Yahoo 的 chart API 抓匯率。<c>{from}{to}=X</c> 的報價就是「1 from = ? to」，
/// 跟 <c>exchange_rate</c> 的語意一樣，所以不需要經台幣當樞紐、也不用建幣對路徑圖。
///
/// 兩個實測出來的坑（2026-11-19 這台機器）：
///   1. 不帶 User-Agent 直接 429「Edge: Too Many Requests」→ 一定要帶。
///   2. 公司網直連連不出去（curl 000），要能填 proxy（公司的 squid）才通 → ProxyUrl 可設定。
/// 抓不到一律回錯誤訊息、不動現有數字（人工填的照舊留著）。
/// </summary>
public sealed class RateSource
{
    public const string Endpoint = "https://query1.finance.yahoo.com/v8/finance/chart/";
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0 Safari/537.36";
    public const int TimeoutSeconds = 10;

    /// <summary>取 URL 的內容；連不出去或 HTTP 非 2xx 就丟例外。測試餵假實作，不打網路。</summary>
    public interface IFetch { string Get(string url); }

    public sealed record Quote(decimal Rate, string RateDate);

    public static string Symbol(string from, string to) => $"{Db.NormalizeCurrencyCode(from)}{Db.NormalizeCurrencyCode(to)}=X";

    /// <summary>例 http://10.191.190.5:3128；空白 = 直連（不用系統 proxy，免得一開就卡在環境設定上）。</summary>
    public string? ProxyUrl { get; set; }

    readonly IFetch? _fetch;
    public RateSource(IFetch? fetch = null) => _fetch = fetch;

    /// <summary>抓一對匯率。任何失敗都回 (null, 給人看的訊息)，不抛例外。</summary>
    public (Quote? Quote, string? Error) Fetch(string fromCcy, string toCcy)
    {
        var from = Db.NormalizeCurrencyCode(fromCcy);
        var to = Db.NormalizeCurrencyCode(toCcy);
        if (from == to) return (new Quote(1m, Db.Today()), null);
        var url = $"{Endpoint}{Uri.EscapeDataString(Symbol(from, to))}?range=1d&interval=1d";
        try
        {
            var body = _fetch?.Get(url) ?? new HttpFetch(ProxyUrl).Get(url);
            return Parse(body, from, to);
        }
        catch (Exception e)
        {
            return (null, Loc.F("Rate_NoConnect", e.Message.Split('\n')[0]));
        }
    }

    /// <summary>
    /// 解析 chart API 的回應。只接受 <c>meta.currency</c> 等於 to 的報價 ——
    /// 符號拼錯時 Yahoo 也可能回別種幣別的價，寧可拒絕寫入。
    /// </summary>
    public static (Quote? Quote, string? Error) Parse(string json, string fromCcy, string toCcy)
    {
        JsonElement chart;
        try
        {
            chart = JsonDocument.Parse(json).RootElement.GetProperty("chart");
        }
        catch (JsonException)
        {
            return (null, Loc.F("Rate_NotJson", json.Substring(0, Math.Min(60, json.Length))));
        }

        if (chart.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
        {
            var desc = err.TryGetProperty("description", out var d) ? d.GetString() : "";
            return (null, Loc.F("Rate_NotFoundDetail", fromCcy, toCcy, $"{err.GetProperty("code").GetString()} {desc}"));
        }
        if (!chart.TryGetProperty("result", out var result) || result.GetArrayLength() == 0)
            return (null, Loc.F("Rate_NoQuote", fromCcy, toCcy));

        var meta = result[0].GetProperty("meta");
        if (!meta.TryGetProperty("regularMarketPrice", out var price) || price.ValueKind != JsonValueKind.Number)
            return (null, Loc.F("Rate_NoLive", fromCcy, toCcy));

        var quoteCcy = meta.TryGetProperty("currency", out var c) ? c.GetString() ?? "" : "";
        if (!quoteCcy.Equals(Db.NormalizeCurrencyCode(toCcy), StringComparison.OrdinalIgnoreCase))
            return (null, Loc.F("Rate_WrongCcy", fromCcy, toCcy, quoteCcy));

        // 報價自己的日期（GMT 的 unix 時間）；少了它就不能判斷匯率新不新。
        var date = Db.Today();
        if (result[0].TryGetProperty("timestamp", out var ts) && ts.GetArrayLength() > 0 && ts[0].ValueKind == JsonValueKind.Number)
            date = DateTimeOffset.FromUnixTimeSeconds(ts[0].GetInt64()).UtcDateTime.ToString("yyyy-MM-dd");

        return (new Quote(price.GetDecimal(), date), null);
    }

    /// <summary>抓「帳戶用得到、但不是基準幣」的那些幣對。人工填過的不會被蓋掉（同一天 manual 優先）。</summary>
    public (int Updated, List<string> Errors) Refresh(LedgerStore store)
    {
        var errors = new List<string>();
        int updated = 0;
        foreach (var from in store.NeededCurrencies())
        {
            var (quote, error) = Fetch(from, store.BaseCurrency);
            if (quote is null) { errors.Add($"{from} → {store.BaseCurrency}：{error}"); continue; }
            store.SetRate(from, quote.Rate, store.BaseCurrency, "yahoo", quote.RateDate);
            updated++;
        }
        return (updated, errors);
    }

    /// <summary>「測試連線」用：回一句人看得懂的話（含狀態，不要讓人靠猜）。</summary>
    public string Test(LedgerStore store)
    {
        var to = store.BaseCurrency;
        var from = store.NeededCurrencies().FirstOrDefault() ?? (to == "USD" ? "JPY" : "USD");
        var (quote, error) = Fetch(from, to);
        return quote is null ? $"{from} → {to}：{error}"
                             : $"{from} → {to} = {quote.Rate}（{quote.RateDate}，proxy {(string.IsNullOrWhiteSpace(ProxyUrl) ? "沒設" : ProxyUrl)}）";
    }

    sealed class HttpFetch : IFetch
    {
        readonly string? _proxy;
        public HttpFetch(string? proxy) => _proxy = string.IsNullOrWhiteSpace(proxy) ? null : proxy;

        public string Get(string url)
        {
            using var handler = new HttpClientHandler { UseProxy = _proxy is not null, Proxy = _proxy is null ? null : new WebProxy(_proxy) };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            var res = http.GetAsync(url).GetAwaiter().GetResult();
            if (!res.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP {(int)res.StatusCode} {res.ReasonPhrase}");
            return res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }
    }
}
