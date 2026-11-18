using System.IO;
using System.Text.Json;
using dFinance.Data;

namespace dFinance;

/// <summary>
/// 最近開啟的資料庫。清單跟其他東西一樣放可攜的 data\（不寫 AppData/登錄檔），
/// 所以整個資料夾拷走連清單都還在。讀寫失敗就當沒有 —— 不要為了清單檔壞掉擋住開檔。
/// </summary>
static class RecentFiles
{
    const int Max = 8;
    static string FilePath => Path.Combine(Db.DataDirectory, "recent.json");

    public static List<string> Load()
    {
        try { return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? []; }
        catch { return []; }
    }

    public static void Add(string path)
    {
        var list = Load();
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        Save(list.Take(Max));
    }

    public static void Clear() => Save([]);

    static void Save(IEnumerable<string> list)
    {
        try
        {
            Directory.CreateDirectory(Db.DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list));
        }
        catch { /* 清單不是帳本資料，寫不進去就算了 */ }
    }
}
