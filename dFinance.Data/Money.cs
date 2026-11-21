namespace dFinance.Data;

/// <summary>
/// 錢的單位換算只准出現在這個檔案。SQLite 沒有十進位型別，REAL 會飄，
/// 所以儲存一律用 INTEGER（見 migrations/0001_init.sql 的註解）。
/// 帳本用「分」（1/100）。投資那些（股數/單價要八位小數）等做到時再加，不要先擺沒人用的。
/// </summary>
public static class Money
{
    public const int LedgerScale = 100;

    public static long ToMinor(decimal amount) => (long)Math.Round(amount * LedgerScale, MidpointRounding.AwayFromZero);
    public static decimal FromMinor(long minor) => minor / (decimal)LedgerScale;

    /// <summary>匯率換算（1 外幣 = rate 台幣）。四捨五入到分，不准用 REAL 一路飄。</summary>
    public static long Convert(long minor, decimal rate)
        => (long)Math.Round(minor * rate, MidpointRounding.AwayFromZero);

    /// <summary>
    /// 依幣別小數位顯示（scale 來自 <c>currency.scale</c>：TWD/JPY 0、USD/EUR 2）。
    /// 儲存一律 1/100，所以 scale 只動顯示，不准拿去做加總。
    /// shortcut: 只做小數位，不做貨幣符號表（$ / ¥ / NT$ 的寫法各地不同，等有真實需求再說）。
    /// 格式固定 InvariantCulture：介面可以換語系，錢的寫法不跟著換（de-DE 那種 1.250,00 不能進報表）。
    /// </summary>
    public static string Format(long minor, int scale)
        => FromMinor(minor).ToString(scale <= 0 ? "N0" : "N" + scale, System.Globalization.CultureInfo.InvariantCulture);
}
