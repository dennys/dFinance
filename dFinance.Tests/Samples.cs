using dFinance.Parsing;

namespace dFinance.Tests;

/// <summary>
/// 樣本檔與密碼都不進 version control：檔在 D:\dennys\statement，密碼在 repo 根目錄 .env
/// （KEY=a,b：逗號分隔，每段算一組密碼）。找不到就讓測試跳過（return，不是 fail）。
/// </summary>
internal static class Samples
{
    public const string Dir = @"D:\dennys\statement";

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

    public static ParsedStatement? Parse(string file)
    {
        var path = Path.Combine(Dir, file);
        if (!File.Exists(path)) return null;

        foreach (var pw in Passwords())
        {
            try { return StatementParser.Parse(path, pw, DataDirectory); }
            catch (UglyToad.PdfPig.Exceptions.PdfDocumentEncryptedException) { }
        }
        throw new InvalidOperationException($"{file}：.env 裡沒有任何密碼打得開（試過 {Passwords().Count()} 組）");
    }

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
