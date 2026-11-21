using dFinance.Data;
using Xunit;

namespace dFinance.Tests;

/// <summary>
/// Yahoo 抓取。不打網路：餵 2026-11-19 實抓到的回應當 fixture。
/// 三個真實會發生的結局都要有斷言：200 正常、404 符號不存在、429（沒帶 User-Agent 時 Yahoo 直接回純文字）。
/// </summary>
public class RateSourceTests
{
    // 實抓：GET .../chart/USDTWD=X?range=1d&interval=1d（200）
    const string UsdTwd200 = """
        {"chart":{"result":[{"meta":{"currency":"TWD","symbol":"TWD=X","exchangeName":"CCY",
         "instrumentType":"CURRENCY","regularMarketTime":1795056623,"gmtoffset":0,"timezone":"GMT",
         "regularMarketPrice":32.974,"regularMarketChangePercent":0.1458,"longName":"USD/TWD","shortName":"USD/TWD",
         "priceHint":4},"timestamp":[1795046400]}],"error":null}}
        """;

    // 實抓：不存在的符號（HTTP 404 的 body）
    const string NotFound = """
        {"chart":{"result":null,"error":{"code":"Not Found","description":"No data found, symbol may be delisted"}}}
        """;

    // 實抓：沒帶 User-Agent 時 Yahoo 回 HTTP 429 + 這行純文字
    const string TooManyRequests = "Edge: Too Many Requests";

    sealed class Fake(string body) : RateSource.IFetch
    {
        public string? Url { get; private set; }
        public string Get(string url) { Url = url; return body; }
    }

    [Fact]
    public void 正常的報價解析出匯率與報價自己的日期()
    {
        var (quote, error) = RateSource.Parse(UsdTwd200, "USD", "TWD");

        Assert.Null(error);
        Assert.Equal(32.974m, quote!.Rate);
        Assert.Equal("2026-11-19", quote.RateDate);            // 用 Yahoo 的 timestamp，不是本機今天
    }

    [Fact]
    public void 符號不存在時講得出來不是靜靜回空()
    {
        var (quote, error) = RateSource.Parse(NotFound, "USD", "ZZ");

        Assert.Null(quote);
        Assert.Contains("Not Found", error);
    }

    [Fact]
    public void 不是JSON的回應當成錯誤()
    {
        var (quote, error) = RateSource.Parse(TooManyRequests, "USD", "TWD");

        Assert.Null(quote);
        Assert.Contains("不是 JSON", error);
    }

    [Fact]
    public void 報價幣別對不上不敢寫進去()
    {
        var (quote, error) = RateSource.Parse(UsdTwd200, "USD", "JPY");      // 拿台幣的價要寫成對日元

        Assert.Null(quote);
        Assert.Contains("不敢寫進去", error);
    }

    [Fact]
    public void 問的是外幣對基準幣那個符號()
    {
        var fake = new Fake(UsdTwd200);
        var (quote, _) = new RateSource(fake).Fetch("usd", "twd");

        Assert.NotNull(quote);
        Assert.Contains("chart/USDTWD%3DX", fake.Url);          // {from}{to}=X，= 要 escape
        Assert.Contains("range=1d", fake.Url);
    }

    [Fact]
    public void 連不出去回訊息不抛例外()
    {
        var source = new RateSource(new Throwing());
        var (quote, error) = source.Fetch("USD", "TWD");

        Assert.Null(quote);
        Assert.Contains("連不出去", error);
    }

    class Throwing : RateSource.IFetch
    {
        public string Get(string url) => throw new HttpRequestException("No such host is known");
    }

    [Fact]
    public void 基準幣自己的匯率是1不用抓()
    {
        var fake = new Fake(UsdTwd200);
        var (quote, error) = new RateSource(fake).Fetch("TWD", "TWD");

        Assert.Null(error);
        Assert.Equal(1m, quote!.Rate);
        Assert.Null(fake.Url);                                   // 根本不該發請求
    }
}
