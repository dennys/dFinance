using System.Security.Cryptography;
using System.Text.Json;
using PDFtoImage;
using RapidOcrNet;
using SkiaSharp;

namespace dFinance.Parsing;

/// <summary>
/// 圖片式帳單（中信/匯豐：中文是被畫成一格一格的小圖，文字層讀不到）的 OCR。
///
/// 引擎是 RapidOcrNet（PP-OCRv5，CPU ONNX，離線）。模型不進版控也不連網抓：
/// 要自帶 OCR 就要把兩個檔放好（SHA256 記在 README）：
///   ch_PP-OCRv5_rec_mobile.onnx、ppocrv5_dict.txt
/// 放的位置（找不到就整個 OCR 跳過，走原本的警告，不 crash）：
///   環境變數 DFINANCE_OCR_MODELS → 從 exe 往上找 ocr_models\ 或 models\（repo 根就是 models\）
/// 偵測器/方向分類器是 RapidOcrNet 的 nupkg 內附（ch_PP-OCRv5_mobile_det），不用自己抓。
///
/// 產物是 PDF 座標系的 WordBox（FromOcr=true），所以現成的 Row/StatementTable/ValueFor
/// 直接能用，不用為 OCR 另建一套解析路徑。
/// </summary>
public static class OcrReader
{
    /// <summary>模型、DPI、座標換算方式改了就 +1（快取檔名帶它，換版重跑）。</summary>
    public const int Version = 1;

    private const int Dpi = 150;                              // 實測 150 與 300 準度相同，圖小一半
    private const string RecModel = "ch_PP-OCRv5_rec_mobile.onnx";
    private const string RecDict = "ppocrv5_dict.txt";

    private static readonly object Gate = new();          // ONNX session 不 thread-safe，一次只能辨識一頁

    /// <summary>整份 PDF 逐頁 OCR。回 page → 該頁讀到的字（PDF 座標）。模型沒放好或辨識失敗回 null。</summary>
    public static Dictionary<int, List<WordBox>>? Read(string path, string? password, string? dataDirectory,
                                                       IProgress<string>? progress = null)
    {
        string? models = ModelsDirectory(dataDirectory);
        if (models is null) return null;

        string sha = Sha256(path);
        string cacheDir = string.IsNullOrEmpty(dataDirectory) ? "" : Path.Combine(dataDirectory, "ocr");
        var result = new Dictionary<int, List<WordBox>>();

        byte[] pdf = File.ReadAllBytes(path);
        int pages;
        try { pages = Conversion.GetPageCount(pdf, password ?? ""); }
        catch (Exception) { return null; }

        for (int page = 1; page <= pages; page++)
        {
            var cached = Load(cacheDir, sha, page);
            if (cached is not null) { result[page] = cached; continue; }

            // 一頁十幾秒，不回報進度看起來像卡住（OCR 本來就是逐頁跑，不是技術上的限制）。
            progress?.Report($"OCR 辨識中… 第 {page}/{pages} 頁（CPU，第一次比較慢，之後有快取）");
            var boxes = DetectPage(pdf, password ?? "", page, models);
            if (boxes is null) return null;
            Save(cacheDir, sha, page, boxes);
            result[page] = boxes;
        }
        return result;
    }

    private static List<WordBox>? DetectPage(byte[] pdf, string password, int page, string models)
    {
        using var bmp = Conversion.ToImage(pdf, page - 1, password, new RenderOptions { Dpi = Dpi });

        lock (Gate)
        try
        {
            var engine = Engine(models);
            if (engine is null) return null;

            var res = engine.Detect(bmp, RapidOcrOptions.Default);
            double scale = 72.0 / Dpi;

            // 圖是上下顛倒的座標（y 由上往下），PdfPig 的 Top 是从頁面底部算 → 要翻。
            return res.TextBlocks.Select(b =>
            {
                float score = b.CharScores is { Length: > 0 } s ? s.Average() : 0f;
                return new WordBox(b.Text, b.BoxPoints.Min(p => p.X) * scale,
                                   (bmp.Height - b.BoxPoints.Min(p => p.Y)) * scale,
                                   b.BoxPoints.Max(p => p.X) * scale, true, score);
            }).Where(w => w.Text.Length > 0).ToList();
        }
        catch (Exception) { return null; }
    }

    private static RapidOcr? _engine;
    private static string? _engineModels;

    private static RapidOcr? Engine(string models)
    {
        if (_engine is not null && _engineModels == models) return _engine;

        // 偵測器/分類器是 nupkg 內附的（解到 exe 旁邊的 models\v5\）；辨識器與字典用使用者放的那份。
        string bundled = Path.Combine(AppContext.BaseDirectory, "models", "v5");
        var (det, cls, rec, keys) = (
            Find(models, bundled, "ch_PP-OCRv5_mobile_det.onnx"),
            Find(models, bundled, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
            Path.Combine(models, RecModel), Path.Combine(models, RecDict));
        if (det is null || cls is null || !File.Exists(rec) || !File.Exists(keys)) return null;

        var ocr = new RapidOcr();
        ocr.InitModels(det, cls, rec, keys);
        _engine = ocr;
        _engineModels = models;
        return ocr;
    }

    private static string? Find(string user, string bundled, string file)
        => File.Exists(Path.Combine(user, file)) ? Path.Combine(user, file)
         : File.Exists(Path.Combine(bundled, file)) ? Path.Combine(bundled, file) : null;

    /// <summary>
    /// 模型目錄：環境變數 DFINANCE_OCR_MODELS → data\ocr_models\ → 從 exe 往上找 ocr_models\ 或 models\。
    /// 找不到就回 null（整個 OCR 跳過）。
    /// </summary>
    public static string? ModelsDirectory(string? dataDirectory = null)
    {
        var env = Environment.GetEnvironmentVariable("DFINANCE_OCR_MODELS");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(Path.Combine(env, RecModel))) return env;
        if (!string.IsNullOrEmpty(dataDirectory) && File.Exists(Path.Combine(dataDirectory, "ocr_models", RecModel)))
            return Path.Combine(dataDirectory, "ocr_models");

        foreach (var name in new[] { "ocr_models", "models" })
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, name, RecModel))) dir = dir.Parent;
            if (dir is not null) return Path.Combine(dir.FullName, name);
        }
        return null;
    }

    private static string CachePath(string? cacheDir, string sha, int page)
        => Path.Combine(cacheDir ?? "", $"{sha}-p{page}-ocr{Version}.json");

    private static List<WordBox>? Load(string? cacheDir, string sha, int page)
    {
        if (string.IsNullOrEmpty(cacheDir) || !File.Exists(CachePath(cacheDir, sha, page))) return null;
        try
        {
            var items = JsonSerializer.Deserialize<List<CachedWord>>(File.ReadAllText(CachePath(cacheDir, sha, page)));
            return items?.Select(c => new WordBox(c.T, c.X, c.Y, c.R, true, c.S)).ToList();
        }
        catch (Exception) { return null; }
    }

    private static void Save(string? cacheDir, string sha, int page, List<WordBox> boxes)
    {
        if (string.IsNullOrEmpty(cacheDir)) return;
        try
        {
            Directory.CreateDirectory(cacheDir);
            var items = boxes.Select(w => new CachedWord { T = w.Text, X = w.Left, Y = w.Top, R = w.Right, S = w.OcrScore });
            File.WriteAllText(CachePath(cacheDir, sha, page), JsonSerializer.Serialize(items));
        }
        catch (Exception) { }                                            // 快取寫不進去頂多重跑，不能讓匯入失敗
    }

    private static string Sha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    private sealed class CachedWord
    {
        public string T { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public double R { get; set; }
        public double S { get; set; }
    }
}
