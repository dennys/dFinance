using dFinance.Classification;

namespace dFinance.Data;

/// <summary>
/// 往外面 call HTTPS 的那條路，只留這一個洞（也只這一個方法）。預設 <see cref="NullAdvisor"/> 回空字典
/// ⇒ 行為等於「沒有 AI」，測試與 <c>--uismoke</c> 一律走它（CLASSIFICATION.md 決定 11：這輪只做洞，不做實作）。
/// 不要 <c>IsAvailable</c> 之類的屬性：要不要顯示按鈕由 Data 讀 <c>settings.llm_enabled</c> 決定，不是問 advisor。
/// </summary>
public interface IMerchantAdvisor
{
    /// <param name="unknowns">③④ 之後仍認不到的商家。
    /// <b>不准有帳號與 <c>source_raw</c></b>（單子原始行有帳號/戶號），只准給商家鍵與庫裡見過的原文變體。</param>
    /// <returns>商家鍵 → leaf id。對不上傳進去的 leaf 白名單的一律丢：不准讓模型發明分類名。</returns>
    IReadOnlyDictionary<string, long> Suggest(IReadOnlyList<MerchantHint> unknowns, CancellationToken ct);
}

/// <summary>
/// shortcut: 這輪不實作，所以只帶「現在 dump 得出來」的欄位。真做 LLM 時要連幣別/發卡機構一起給
/// —— 那兩個要連 <c>EntryFact</c> 一起補，不要在這裡先開一個填不出來的欄位。
/// </summary>
public sealed record MerchantHint(string MerchantKey, IReadOnlyList<string> RawVariants, string Kind);

/// <summary>沒接模型就是這個：回空字典，建議清單跟「沒有 AI」完全一樣。</summary>
public sealed class NullAdvisor : IMerchantAdvisor
{
    public IReadOnlyDictionary<string, long> Suggest(IReadOnlyList<MerchantHint> unknowns, CancellationToken ct)
        => new Dictionary<string, long>();
}

/// <summary>
/// UI 唯一允許碰的門面：抓事實 dump → 叫 <see cref="MerchantClassifier.Run"/> →（有人按按鈕才）問 advisor → 寫回。
/// 規則 CRUD 是普通 DB 存取，不屬於分類契約（UI 只准經這裡讀 <c>classify_rule</c>，不准自己拼比對）。
/// </summary>
public sealed class SuggestionService
{
    readonly LedgerStore _store;
    readonly IMerchantAdvisor? _advisor;

    public SuggestionService(LedgerStore store, IMerchantAdvisor? advisor = null)
    {
        _store = store;
        _advisor = advisor;
    }

    /// <summary>規則原封不動倒給規則網格（含停用的與紅標的 —— 紅標要看得见，不准藏）。</summary>
    public IReadOnlyList<ClassifyRule> Rules() => _store.ClassifyRules();

    /// <summary>不寫庫：照報表口徑 dump 未分類商家 → 比對 →（有接 advisor 才）補認不到的那批。</summary>
    public ClassifyOutput Preview(string? from = null, string? to = null, long? accountId = null)
    {
        var nodes = _store.CategoryNodes();
        var output = MerchantClassifier.Run(new ClassifyInput(_store.MerchantFacts(from, to, accountId), Rules(), nodes));
        return _advisor is null || output.Rows.All(r => r.Source != SuggestionSource.None)
            ? output
            : AskAdvisor(output, nodes);
    }

    /// <summary>
    /// 套用一个群組。<paramref name="categoryId"/> 是人在視窗上選的那個（可能不是規則建議的）；
    /// 一律經 <see cref="LedgerStore.SetCategoryForGroup"/> ⇒ <c>EnsureLeaf</c> 會擋標在父類，而且不寫 <c>edited_at</c>。
    /// </summary>
    public int Apply(SuggestionRow row, long categoryId, long? accountId = null)
        => _store.SetCategoryForGroup(row.Kind, row.NoMerchant ? null : row.RawVariants, categoryId, accountId);

    /// <summary>⑤ 只有人在按按鈕時會走到這裡（預設 <see cref="NullAdvisor"/> ⇒ 回空、行為不變）。</summary>
    ClassifyOutput AskAdvisor(ClassifyOutput output, IReadOnlyList<CategoryNode> nodes)
    {
        var leafIds = new HashSet<long>(nodes.Where(n => n.IsLeaf).Select(n => n.Id));
        var unknowns = output.Rows.Where(r => r.Source == SuggestionSource.None && !r.NoMerchant)
                                  .Select(r => new MerchantHint(r.MerchantKey, r.RawVariants, r.Kind)).ToList();
        if (unknowns.Count == 0) return output;

        var answer = _advisor!.Suggest(unknowns, CancellationToken.None);
        var rows = output.Rows.Select(r =>
            answer.TryGetValue(r.MerchantKey, out var id) && leafIds.Contains(id)                // 對不上白名單的一律丢
                ? r with { CategoryId = id, CategoryPath = nodes.FirstOrDefault(n => n.Id == id)?.Path,
                           PreApply = false, Source = SuggestionSource.Advisor }
                : r).ToList();
        return output with { Rows = rows };
    }
}
