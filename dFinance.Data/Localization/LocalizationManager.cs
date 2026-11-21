using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Threading;

namespace dFinance.Data.Localization;

/// <summary>
/// RESX 語系管理：換語系不用重開程式、不用重建視窗。
///
/// 為什麼不用 <c>{x:Static Resources.Save}</c>：那只在載入時取一次值，改了 <see cref="CultureInfo"/>
/// 現有的控件也不會更新，最後只能叫人重開程式。這裡改成索引子綁定（<see cref="P:string"/>），
/// 換語系時發一條 <c>Item[]</c> 變更通知，所有綁定自動重取。XAML 那端用 <c>{loc:Loc Key}</c>。
///
/// 只動 <b>UI</b> 語系：金額格式、日期格式、數字解析一律還是吃 <see cref="CultureInfo.CurrentCulture"/>
/// （不變），否則把 CurrentCulture 切成 de-DE 之類的小數點是逗號的語系，parser 的金額會被解錯一百倍。
/// 查字串用的是這裡存的 <see cref="UiCulture"/>，不吃執行緒語系，所以 Task.Run 裡的匯入也拿得到對的語言。
/// </summary>
public sealed class LocalizationManager : INotifyPropertyChanged
{
    public static LocalizationManager Current { get; } = new();

    /// <summary>預設語系 = <c>Strings.resx</c> 的原文（AGENTS.md：文案用繁體中文）。</summary>
    public const string DefaultCulture = "zh-TW";

    /// <summary>支援的語系（選單用）。代碼 → 該語言自己的寫法，不翻譯，否則切換後會找不到自己要的。</summary>
    public static IReadOnlyList<(string Code, string Native)> Languages { get; } =
        [("zh-TW", "繁體中文"), ("en-US", "English"), ("zh-CN", "简体中文"), ("ja-JP", "日本語")];

    /// <summary>字串目錄（測試用它比對各語系的 key 是否齊全）。</summary>
    public static ResourceManager Catalog { get; } = new("dFinance.Data.Localization.Strings", typeof(LocalizationManager).Assembly);

    volatile CultureInfo _ui = CultureInfo.GetCultureInfo(DefaultCulture);

    LocalizationManager() { }

    /// <summary>目前查字串用的語系（跟 <see cref="CultureInfo.CurrentCulture"/> 無關）。</summary>
    public CultureInfo UiCulture => _ui;

    /// <summary>綁定用：換語系時 WPF 靠這條重取所有 <c>[Key]</c> 綁定。</summary>
    public string this[string key] => Get(key, _ui);

    /// <summary>語系換好了（MainWindow 用來重建分頁：code 裡一次指派出去的欄頭不吃綁定）。</summary>
    public event Action? CultureChanged;

    /// <summary>各語系都查不到的 key（打錯字）。缺譯不會進這裡 —— 會悄悄退回預設語系，靠測試擋。</summary>
    public static readonly ConcurrentDictionary<string, byte> MissingKeys = new();

    public string Get(string key) => Get(key, _ui);

    /// <summary>帶參數的字串（resx 裡寫 <c>{0}</c>）。語序交給譯者，不要在程式裡拼句子。</summary>
    public string Format(string key, params object?[] args) => string.Format(Get(key, _ui), args);

    static string Get(string key, CultureInfo culture)
    {
        var text = Catalog.GetString(key, culture);
        if (text is not null) return text;
        MissingKeys.TryAdd(key, 0);                       // 打錯字：讓 --uismoke 與測試抓得到
        return "!" + key + "!";
    }

    /// <summary>換語系。未知的代碼退回預設語系（選單被人改壞也不會炸）。</summary>
    public void ChangeCulture(string? code)
    {
        CultureInfo culture;
        try { culture = CultureInfo.GetCultureInfo(string.IsNullOrWhiteSpace(code) ? DefaultCulture : code!); }
        catch (CultureNotFoundException) { culture = CultureInfo.GetCultureInfo(DefaultCulture); }

        _ui = culture;
        // 只為 BCL 的格式化（DatePicker 的月曆、WPF 的 Language）；查字串不吃它。
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UiCulture)));
        CultureChanged?.Invoke();
    }

    /// <summary>這個代碼是不是我們真的有檔的語系。</summary>
    public static bool IsSupported(string? code)
        => Languages.Any(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>code-behind / Data 層查字串的短寫法：<c>Loc.T("Common_Save")</c>、<c>Loc.F("Ledger_Rows", 12)</c>。</summary>
public static class Loc
{
    public static string T(string key) => LocalizationManager.Current.Get(key);
    public static string F(string key, params object?[] args) => LocalizationManager.Current.Format(key, args);
    public static CultureInfo UiCulture => LocalizationManager.Current.UiCulture;
}
