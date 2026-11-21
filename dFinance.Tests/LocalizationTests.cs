using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Resources;
using dFinance.Data.Localization;
using Xunit;

namespace dFinance.Tests;

/// <summary>
/// 語系目錄的守門。<see cref="ResourceManager.GetString"/> 缺譯會悄悄退回預設語系（不是回 null），
/// 所以「日文檔漏了一條」這種事在執行期完全看不出來 —— 只能在這裡比對各語系的 key 與值。
/// </summary>
public class LocalizationTests
{
    static readonly CultureInfo[] Cultures =
    [
        CultureInfo.GetCultureInfo("zh-TW"),   // = 預設語系（neutral）
        CultureInfo.GetCultureInfo("en-US"),
        CultureInfo.GetCultureInfo("zh-CN"),
        CultureInfo.GetCultureInfo("ja-JP"),
    ];

    static Dictionary<string, string> Entries(CultureInfo culture)
    {
        var set = LocalizationManager.Catalog.GetResourceSet(culture, createIfNotExists: true, tryParents: true)
                  ?? throw new InvalidOperationException($"沒有 {culture.Name} 這套資源");
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry e in set)
            if (e.Key is string k && e.Value is string v && !k.StartsWith("__") && k != "Language")
                map[k] = v;
        return map;
    }

    [Fact]
    public void 每一種語系都有全部的字串()
    {
        var reference = Entries(CultureInfo.GetCultureInfo(LocalizationManager.DefaultCulture));
        Assert.NotEmpty(reference);

        foreach (var culture in Cultures)
        {
            var map = Entries(culture);
            Assert.Equal(reference.Keys.OrderBy(k => k), map.Keys.OrderBy(k => k));
            Assert.Empty(map.Where(e => string.IsNullOrWhiteSpace(e.Value)).Select(e => $"{culture.Name}:{e.Key}"));
        }
    }

    [Fact]
    public void 外語系不能整份Copy中文()
    {
        var zh = Entries(CultureInfo.GetCultureInfo(LocalizationManager.DefaultCulture));
        foreach (var name in new[] { "en-US", "ja-JP" })
        {
            var other = Entries(CultureInfo.GetCultureInfo(name));
            var same = other.Count(e => zh.TryGetValue(e.Key, out var v) && v == e.Value);
            // 少數字串本來就一樣（未分類、USD…），整份一樣就是忘記翻
            Assert.True(same < other.Count / 2, $"{name} 有 {same}/{other.Count} 條跟原文一模一樣");
        }
    }

    [Fact]
    public void 參數佔位符每種語系要一致()
    {
        var zh = Entries(CultureInfo.GetCultureInfo(LocalizationManager.DefaultCulture));
        foreach (var culture in Cultures)
            foreach (var e in Entries(culture))
            {
                var expected = Ph(zh[e.Key]);
                Assert.Equal($"{e.Key}:{expected}", $"{e.Key}:{Ph(e.Value)}");
            }

        static string Ph(string s) => string.Join(",", System.Text.RegularExpressions.Regex.Matches(s, @"\{\d+\}")
            .Select(m => m.Value).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void 查不到的key要有明顯標記並被記錄()
    {
        LocalizationManager.MissingKeys.Clear();
        var text = LocalizationManager.Current["NoSuch_Key_Anywhere"];
        Assert.Equal("!NoSuch_Key_Anywhere!", text);
        Assert.Contains("NoSuch_Key_Anywhere", LocalizationManager.MissingKeys.Keys);
    }

    [Fact]
    public void 換語系要通知索引子綁定()
    {
        var manager = LocalizationManager.Current;
        var original = manager.UiCulture.Name;
        var notified = new List<string?>();
        ((INotifyPropertyChanged)manager).PropertyChanged += (_, e) => notified.Add(e.PropertyName);
        try
        {
            manager.ChangeCulture("en-US");
            Assert.Equal("en-US", manager.UiCulture.Name);
            Assert.Contains("Item[]", notified);                          // XAML 的 [Key] 綁定靠這一條
            Assert.False(string.IsNullOrEmpty(manager["Common_Save"]));
            Assert.Equal("Save", manager["Common_Save"]);

            manager.ChangeCulture("klingon");                             // 不知道的代碼要退回預設語系，不能炸
            Assert.Equal(LocalizationManager.DefaultCulture, manager.UiCulture.Name);
        }
        finally
        {
            manager.ChangeCulture(original);
        }
    }

    [Fact]
    public void 選單列出的語系都要真的有檔()
    {
        foreach (var (code, _) in LocalizationManager.Languages)
        {
            var map = Entries(CultureInfo.GetCultureInfo(code));
            Assert.True(map.Count > 100, $"{code} 只有 {map.Count} 條字串");
        }
    }
}
