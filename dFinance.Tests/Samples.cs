using dFinance.Parsing;

namespace dFinance.Tests;

/// <summary>
/// 樣本檔與密碼都不進 version control：檔在 D:\dennys\statement，密碼在 repo 根目錄 .env
/// （KEY=a,b：逗號分隔，每段算一組密碼）。找不到就讓測試跳過（return，不是 fail）。
/// </summary>
internal static class Samples
{
    public const string Dir = @"D:\dennys\statement";

    public static ParsedStatement? Parse(string file)
    {
        var path = Path.Combine(Dir, file);
        if (!File.Exists(path)) return null;

        foreach (var pw in Passwords())
        {
            try { return StatementParser.Parse(path, pw); }
            catch (UglyToad.PdfPig.Exceptions.PdfDocumentEncryptedException) { }
        }
        throw new InvalidOperationException($"{file}：.env 裡沒有任何密碼打得開（試過 {Passwords().Count()} 組）");
    }

    internal static IEnumerable<string> Passwords()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, ".env"))) dir = dir.Parent;
        if (dir is null) return [];
        return File.ReadAllLines(Path.Combine(dir.FullName, ".env"))
                   .Select(l => l.Split('=', 2))
                   .Where(p => p.Length == 2)
                   .SelectMany(p => p[1].Split(','))
                   .Select(p => p.Trim().Trim('"'))
                   .Where(p => p.Length > 0)
                   .Distinct();
    }
}
