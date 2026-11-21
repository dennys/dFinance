namespace dFinance.Classification;

/// <summary>規則怎麼比。<see cref="Ignore"/> 是渠道詞（不是分類），<c>CategoryId</c> 一律 null。</summary>
public enum RuleMatch { Contains, Exact, Glob, Ignore }

/// <summary>規則哪來的。<c>learned</c> 不在這裡：那是 Run 當下從 entry 推的，不落庫（落庫就會變第二份口徑）。</summary>
public enum RuleSource { Seed, User, Advisor }

/// <summary>
/// 一條規則。<see cref="Kind"/> 是 <c>"expense"</c> / <c>"income"</c> / <c>"any"</c>：
/// 沒有它，<c>薪資 → 薪資收入／本薪</c> 這種規則會去命中同名的支出列，
/// 而 <c>EnsureLeaf</c> 只擋父類不擋方向 ⇒ 分類報表的塊小計與「收−支」總計行會互相對不上（CLASSIFICATION.md B7）。
/// </summary>
public sealed record ClassifyRule(long Id, string Pattern, RuleMatch Match, long? CategoryId,
                                  int Priority, bool Enabled, RuleSource Source, string Kind, string? Note);

/// <summary>
/// 帳本裡的一個商家群組（<c>Kind</c> 已經是「收支那一側」：<c>adjustment</c> 歸 expense，<c>transfer</c>/<c>refund</c> 由 Data 擋掉）。
/// shortcut: 這輪不做 learned（決定 4 暫緩），要時補 <c>CategoryId?</c> 讓它能把人已標的帶進來推建議。
/// </summary>
public sealed record EntryFact(string? Merchant, string Kind, int Count);

/// <summary>分類樹的一個節點。<see cref="IsLeaf"/> 讓「目標被刪掉」與「目標變成父類」講得出差別（兩個紅標的處理方式不一樣）。</summary>
public sealed record CategoryNode(long Id, string Path, string Kind, bool IsLeaf);

/// <summary>四個清單，全部是「庫的純事實」：沒有 DB 連線、沒有語系、沒有網路。</summary>
public sealed record ClassifyInput(IReadOnlyList<EntryFact> Entries,
                                   IReadOnlyList<ClassifyRule> Rules,
                                   IReadOnlyList<CategoryNode> Categories);

public enum SuggestionSource { None, Rule, Advisor }

/// <summary>
/// 給 UI 綁的一列。<see cref="MerchantKey"/> 是正規化鍵（同鍵的原文變體已合併進這一列）；
/// 「（無商家）」那一群用 <see cref="NoMerchantKey"/> 當鍵，不拿本地化 label 當群組鍵（B11）。
/// </summary>
public sealed record SuggestionRow(string MerchantKey, IReadOnlyList<string> RawVariants, int Count, string Kind,
                                   long? CategoryId, string? CategoryPath, bool PreApply,
                                   SuggestionSource Source, long? RuleId)
{
    /// <summary>「（無商家）」那一群：套用時要寫 <c>merchant IS NULL</c>，不准拿本地化 label 往還（B11）。</summary>
    public bool NoMerchant => MerchantKey == MerchantClassifier.NoMerchantKey;
}

public enum BrokenReason { TargetNotLeaf, CategoryGone, KindMismatch }

/// <summary>規則網格上的紅標：照樣跳過，但要讓人看得见為什麼跳過（靜默失效就是 B2 那個形状）。</summary>
public sealed record BrokenRule(long RuleId, BrokenReason Reason, string Pattern);

public sealed record ClassifyOutput(IReadOnlyList<SuggestionRow> Rows, IReadOnlyList<BrokenRule> Broken);
