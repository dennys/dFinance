using dFinance.Parsing;

namespace dFinance.Tests;

/// <summary>
/// 樣本檔與密碼都不進 version control：檔在 <see cref="Dirs"/>（現行 + 舊的兩處，可用環境變數
/// DFINANCE_STATEMENT_DIR 蓋掉），密碼在 repo 根目錄 .env（KEY=a,b：逗號分隔，每段算一組密碼）。
/// 檔不在就 <see cref="Assert.Skip(string)"/> 當場跳過並講原因 —— 之前是 <c>return</c>，
/// 樣本被改名/搬走時測試會「空白通過」，綠燈等於沒測（2026-11-19 就燒過一次）。
/// </summary>
internal static class Samples
{
    /// <summary>找樣本的目錄（依序）。新的在 statement，改名的舊單據在 statement_old。</summary>
    public static IReadOnlyList<string> Dirs =>
        Environment.GetEnvironmentVariable("DFINANCE_STATEMENT_DIR") is { Length: > 0 } one
            ? [one]
            : [@"D:\dennys\statement", @"D:\dennys\statement_old"];

    /// <summary>repo 根目錄（放 data\ 在那裡）。</summary>
    static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, ".env"))) dir = dir.Parent;
        return dir?.FullName;
    }

    /// <summary>OCR 結果的快取/模型位置（data\ocr、data\ocr_models）。</summary>
    public static string? DataDirectory => RepoRoot() is { } r ? Path.Combine(r, "data") : null;

    /// <summary>這份機器有沒有放 OCR 模型（沒放就不能測圖片式帳單）。</summary>
    public static bool OcrReady => OcrReader.ModelsDirectory(DataDirectory) is not null;

    /// <summary>OCR 模型沒放 → 整組跳過（不是靜靜通過）。</summary>
    public static void RequireOcr()
    {
        if (!OcrReady) Assert.Skip($"沒有 OCR 模型（應放在 {DataDirectory}\\ocr_models），圖片式帳單整組跳過");
    }

    /// <summary>樣本的完整路徑；兩處都沒有就回 null。</summary>
    public static string? PathOf(string file) => Dirs.Select(d => System.IO.Path.Combine(d, file)).FirstOrDefault(File.Exists);

    /// <summary>
    /// 解析樣本。檔不在 → 跳過並講清楚（不是 pass）；檔在但密碼打不開 → 直接失敗（那是環境壞了，要让人看見）。
    /// </summary>
    public static ParsedStatement Parse(string file)
    {
        var path = PathOf(file);
        if (path is null)
            Assert.Skip($"樣本 {file} 不在（找過 {string.Join("、", Dirs)}）；測試不能空白通過 —— 把檔放回去，或接受它被跳過");

        foreach (var pw in Passwords())
        {
            try { return StatementParser.Parse(path!, pw, DataDirectory); }
            catch (UglyToad.PdfPig.Exceptions.PdfDocumentEncryptedException) { }
        }
        throw new InvalidOperationException($"{file}：.env 裡沒有任何密碼打得開（試過 {Passwords().Count()} 組）");
    }

    /// <summary>這份樣本在不在（給「有就跑、沒有就跳過」的迴圈用）。</summary>
    public static bool Exists(string file) => PathOf(file) is not null;

    internal static IEnumerable<string> Passwords()
    {
        var root = RepoRoot();
        if (root is null) return [];
        return File.ReadAllLines(Path.Combine(root, ".env"))
                   .Select(l => l.Split('=', 2))
                   .Where(p => p.Length == 2)
                   .SelectMany(p => p[1].Split(','))
                   .Select(p => p.Trim().Trim('"'))
                   .Where(p => p.Length > 0)
                   .Distinct();
    }
}
