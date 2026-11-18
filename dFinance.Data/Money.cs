namespace dFinance.Data;

/// <summary>
/// 錢的單位換算只准出現在這個檔案。SQLite 沒有十進位型別，REAL 會飄，
/// 所以儲存一律用 INTEGER（見 migrations/0001_init.sql 的註解）。
/// 帳本用「分」，投資用 1/10^8（股數與單價要八位小數）。
/// </summary>
public static class Money
{
    public const int LedgerScale = 100;
    public const int AssetScale = 100_000_000;

    public static long ToMinor(decimal amount) => (long)Math.Round(amount * LedgerScale, MidpointRounding.AwayFromZero);
    public static decimal FromMinor(long minor) => minor / (decimal)LedgerScale;

    /// <summary>匯率換算（1 外幣 = rate 台幣）。四捨五入到分，不准用 REAL 一路飄。</summary>
    public static long Convert(long minor, decimal rate)
        => (long)Math.Round(minor * rate, MidpointRounding.AwayFromZero);

    public static long ToAssetUnits(decimal amount) => (long)Math.Round(amount * AssetScale, MidpointRounding.AwayFromZero);
    public static decimal FromAssetUnits(long units) => units / (decimal)AssetScale;
}
