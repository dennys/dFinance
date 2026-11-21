using System.Globalization;
using System.Text;

namespace dFinance.Classification;

/// <summary>
/// 商家 → 分類的建議。整個模組只對外開這一個 function：正規化、按正規化鍵群組、剝 ignore 詞、
/// 比對、leaf 白名單檢查與 id→path 全住在這裡（所以 Data 層不需要再拿正規化自己做一遍，兩邊不會不一致）。
/// 管線（CLASSIFICATION.md §5）：① 正規化 → ①' 按正規化鍵＋收支側群組 → ② 剝 ignore 詞 → ③ 規則比對 → ④ leaf 檢查。
/// 比較一律 <see cref="StringComparison.Ordinal"/> ＋ <c>ToLowerInvariant</c>：換成 tr-TR/de-DE 不准讓結果變（AGENTS「語系只換 UI」）。
/// </summary>
public static class MerchantClassifier
{
    const string Any = "any";

    /// <summary>「（無商家）」那一群的群組鍵。<c>\0</c> 不可能出現在正規化後的商家名裡，所以它不会被真商家碰上。</summary>
    public const string NoMerchantKey = "\u0000";

    public static ClassifyOutput Run(ClassifyInput input)
    {
        var nodes = new Dictionary<long, CategoryNode>();
        foreach (var c in input.Categories) nodes[c.Id] = c;

        // ④ 之前先把規則本身檢查完：目標被刪 / 目標變成父類 / 規則的方向與目標分類互相矛盾，
        //    全部當紅標回報（照樣跳過，但不准靜默 —— 靜默就是 B2：認到了卻跟沒認到長得一樣）。
        var broken = new List<BrokenRule>();
        var active = new List<ClassifyRule>();
        var ignores = new List<string>();
        foreach (var r in input.Rules)
        {
            if (!r.Enabled) continue;
            if (r.Match == RuleMatch.Ignore)
            {
                var k = Key(r.Pattern);
                if (k.Length > 0) ignores.Add(k);           // 純標點的 pattern 正規化後是空字串 ⇒ 會到处剝,直接丟
                continue;
            }
            if (r.CategoryId is not { } id || !nodes.TryGetValue(id, out var node))
            { broken.Add(new BrokenRule(r.Id, BrokenReason.CategoryGone, r.Pattern)); continue; }
            if (!node.IsLeaf)
            { broken.Add(new BrokenRule(r.Id, BrokenReason.TargetNotLeaf, r.Pattern)); continue; }
            if (r.Kind != Any && r.Kind != node.Kind)
            { broken.Add(new BrokenRule(r.Id, BrokenReason.KindMismatch, r.Pattern)); continue; }
            active.Add(r);
        }
        ignores = ignores.OrderByDescending(k => k.Length)
                         .ThenBy(k => k, StringComparer.Ordinal).ToList();   // 街口電支 要先於 電支
        var exact = Compile(active.Where(r => r.Match == RuleMatch.Exact));
        var fuzzy = Compile(active.Where(r => r.Match is RuleMatch.Contains or RuleMatch.Glob));

        // ①' 同鍵的原文變體合成一列（B5）；收支側也進鍵（B7：income 列不能被標成 expense 分類）。
        // 群組鍵用「剝完渠道詞」的那個鍵：不然 <c>A- 中油－…</c> 跟 <c>中油－…</c> 會當兩間店（人就要按兩次）。
        var groups = new Dictionary<(string Key, string Side), Group>();
        foreach (var e in input.Entries)
        {
            var side = Side(e.Kind);
            var key = e.Merchant is null ? NoMerchantKey : GroupKey(e.Merchant, ignores);
            if (!groups.TryGetValue((key, side), out var g)) groups[(key, side)] = g = new Group(key, side);
            g.Count += e.Count;
            if (e.Merchant is { } m && !g.Variants.Contains(m, StringComparer.Ordinal)) g.Variants.Add(m);
        }

        var rows = new List<SuggestionRow>(groups.Count);
        foreach (var g in groups.Values)
        {
            // ② 剝渠道詞：反覆剝到不動點；剝完變空就退回原字串（整列都是渠道名的商家不准整個消失）。
            // 原字串也照樣拿進去比一次 ⇒「keyword 贏 ignore」：一卡通 同時是渠道名與交通關鍵字。
            var candidates = new List<string>();
            foreach (var raw in g.Variants.OrderBy(v => v, StringComparer.Ordinal))
            {
                Add(candidates, Key(StripIgnores(Fold(raw), ignores)));
                Add(candidates, Key(raw));
            }

            ClassifyRule? hit = null;
            foreach (var (rule, pattern) in exact)
                if (Applies(rule, g.Side) && candidates.Any(c => string.Equals(c, pattern, StringComparison.Ordinal))) { hit = rule; break; }
            if (hit is null)
                foreach (var (rule, pattern) in fuzzy)          // 規則順序在外、變體在內：priority 小的先贏,不看哪個變體先中
                {
                    if (!Applies(rule, g.Side)) continue;
                    if (rule.Match == RuleMatch.Glob
                        ? candidates.Any(c => Glob(pattern, c))
                        : candidates.Any(c => c.Contains(pattern, StringComparison.Ordinal)))
                    { hit = rule; break; }
                }

            var leaf = hit is null ? null : nodes[hit.CategoryId!.Value];
            // advisor 來的永不預勾（要人看過）；seed/user 是使用者明說的規則,照預勾。
            var pre = hit is not null && hit.Source != RuleSource.Advisor;
            rows.Add(new SuggestionRow(g.Key, g.Variants, g.Count, g.Side,
                                       hit?.CategoryId, leaf?.Path, pre,
                                       hit is null ? SuggestionSource.None :
                                       hit.Source == RuleSource.Advisor ? SuggestionSource.Advisor : SuggestionSource.Rule,
                                       hit?.Id));
        }

        return new ClassifyOutput(rows.OrderByDescending(r => r.Count)
                                        .ThenBy(r => r.MerchantKey, StringComparer.Ordinal).ToList(), broken);
    }

    /// <summary>群組鍵：剥完渠道詞的鍵；剥到空就退回原鍵（整列都是渠道名的商家不准整個消失）。</summary>
    static string GroupKey(string raw, IReadOnlyList<string> ignores)
    {
        var stripped = Key(StripIgnores(Fold(raw), ignores));
        return stripped.Length > 0 ? stripped : Key(raw);
    }

    /// <summary>這一條規則管不管這個商家所在的收支側。</summary>
    static bool Applies(ClassifyRule rule, string side) => rule.Kind == Any || rule.Kind == side;

    static void Add(List<string> list, string candidate)
    {
        if (candidate.Length > 0 && !list.Contains(candidate, StringComparer.Ordinal)) list.Add(candidate);
    }

    /// <summary>編譯一批規則：照 (priority, id) 排好，並把 pattern 先過同一把尺。</summary>
    static List<(ClassifyRule Rule, string Pattern)> Compile(IEnumerable<ClassifyRule> rules)
        => rules.OrderBy(r => r.Priority).ThenBy(r => r.Id)
                .Select(r => (r, r.Match == RuleMatch.Glob ? GlobPattern(r.Pattern) : Key(r.Pattern)))
                .Where(x => x.Item2.Length > 0)              // 正規化後是空的 keyword（整個 pattern 都是標點）不准比對
                .ToList();

    /// <summary>glob 的 pattern 不能走 <see cref="Key"/>：那會把 <c>?</c> 跟 <c>*</c> 當標點抹掉，<c>?險</c> 就叉變回 B1 那種永不中。</summary>
    static string GlobPattern(string pattern)
        => new string(Fold(pattern).Where(c => !char.IsWhiteSpace(c)).ToArray());

    /// <summary>反覆剝渠道詞到不動點。8 輪是保底，正常一兩輪就定了（一個商家名前頂多兩三個渠道前綴）。</summary>
    internal static string StripIgnores(string folded, IReadOnlyList<string> ignores)
    {
        var s = folded;
        for (var pass = 0; pass < 8; pass++)
        {
            var hit = false;
            foreach (var p in ignores)
            {
                var at = IndexOfToken(s, p);
                if (at < 0) continue;
                s = s.Remove(at, p.Length);
                hit = true;
                break;                                        // 字串變了就重頭掃,避免邊走邊改
            }
            if (!hit) break;
        }
        return s;
    }

    /// <summary>
    /// 找得到位置就回索引,否則 -1。左邊界一律要（開頭或紧跟分隔符）—— 它只負責剝前綴,不是全域刪除。
    /// 右邊界只在 pattern 是純 ASCII 時要：中文沒有詞邊界（要求會剝不到「代收全家」）,
    /// 而拉丁詞不要求的話 <c>A-</c>（正規化成 <c>a</c>）會把 <c>amazon</c> 咬成 <c>mazon</c>。
    /// </summary>
    internal static int IndexOfToken(string s, string p)
    {
        var needRight = p.All(c => c < 128);
        for (var i = s.IndexOf(p, StringComparison.Ordinal); i >= 0; i = s.IndexOf(p, i + 1, StringComparison.Ordinal))
        {
            if (i > 0 && char.IsLetterOrDigit(s[i - 1])) continue;
            if (needRight && i + p.Length < s.Length && char.IsLetterOrDigit(s[i + p.Length])) continue;
            return i;
        }
        return -1;
    }

    /// <summary>NFKD（全形 → 半形、<c>㈱</c> → 株式会社）＋ 去變音符（CAFÉ → CAFE）＋ invariant 小寫,留標點。</summary>
    internal static string Fold(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var d = s.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) is UnicodeCategory.NonSpacingMark
                or UnicodeCategory.EnclosingMark) continue;
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>比對用的鍵：<see cref="Fold"/> 之後只留 Letter|Digit。<c>7-11</c>/<c>7-ELEVEN</c> 這種標點差異在這裡被抹平。</summary>
    internal static string Key(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var f = Fold(s);
        var sb = new StringBuilder(f.Length);
        foreach (var ch in f) if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }

    /// <summary><c>*</c> = 任意多個字元、<c>?</c> = 一個字元。讓 <c>?險</c> 那種寫法真的有意義（B1）。</summary>
    internal static bool Glob(string pattern, string s)
    {
        int p = 0, i = 0, star = -1, mark = 0;
        while (i < s.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == s[i])) { p++; i++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = i; }
            else if (star >= 0) { p = star + 1; i = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    /// <summary>adjustment 歸支出側（跟帳本清單的下拉同一個口徑）。transfer/refund 不該出現在這裡,由 Data 擋。</summary>
    static string Side(string kind) => kind == "income" ? "income" : "expense";

    sealed class Group(string key, string side)
    {
        public string Key { get; } = key;
        public string Side { get; } = side;
        public int Count;
        public List<string> Variants { get; } = [];
    }
}
