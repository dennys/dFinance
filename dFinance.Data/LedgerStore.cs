using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace dFinance.Data;

/// <summary>
/// 帳本清單與概況的讀寫。報表口徑的單一來源：<see cref="Live"/> 決定「哪些列算數」，
/// UI 端不准自己拼 WHERE，錢的進出只准經 <see cref="Money"/>。
/// </summary>
public sealed class LedgerStore
{
    /// <summary>
    /// 報表鐵律：尚未發生（pending）、不記（ignored）、重複（duplicate）、已軟刪除的，一律排除。
    /// 忘記帶這個條件就會把未發生的款項算進支出（見 dFinance-db-design.md）。
    /// </summary>
    public const string Live = "e.deleted_at IS NULL AND e.status IN ('cleared','imported')";

    /// <summary>
    /// 每次寫入推一格。分頁切回來時比對它，髒了才重查 —— multi-tab 的用意就是「切來切去不重讀」。
    /// 匯入在背景執行，跑完也要 <see cref="Touch"/> 一下。
    /// </summary>
    public static long DataVersion { get; private set; }
    public static void Touch() => DataVersion++;

    readonly SqliteConnection _db;
    public LedgerStore(SqliteConnection db) => _db = db;

    /// <summary>清單的一列。UI 直接綁這個（商家/分類/備註/日期可編輯，所以欄位是 set）。</summary>
    /// <summary>清單的一列。格子裡改完就地更新（不需重查整張表），所以要實作 INPC。</summary>
    public sealed class Row : INotifyPropertyChanged
    {
        string? _date, _merchant, _note, _category;
        long? _categoryId;
        bool _edited;

        public event PropertyChangedEventHandler? PropertyChanged;
        void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value; Raise(name);
        }

        public long Id { get; init; }
        public string? Date                                                 // yyyy-MM-dd；NULL = 等使用者補
        {
            get => _date;
            set { Set(ref _date, value); Raise(nameof(DateText)); Raise(nameof(NeedsDate)); }
        }
        public string? Merchant { get => _merchant; set => Set(ref _merchant, value); }
        public string? Note { get => _note; set => Set(ref _note, value); }
        public long? CategoryId
        {
            get => _categoryId;
            set { Set(ref _categoryId, value); Raise(nameof(Category)); }
        }
        public string? Category { get => _category; set => Set(ref _category, value); }
        public long AmountMinor { get; init; }
        public string Kind { get; init; } = "";
        public string Currency { get; init; } = "TWD";
        public string? OrigCurrency { get; init; }
        public long AccountId { get; init; }
        public string Account { get; init; } = "";
        public string AccountType { get; init; } = "";
        public string Status { get; init; } = "";
        public bool Edited { get => _edited; set => Set(ref _edited, value); }
        public bool Orphaned { get; init; }
        public string? SourceRaw { get; init; }
        public string? SourceFile { get; init; }
        public int? SourcePage { get; init; }
        public string? Issuer { get; init; }
        public string? Parser { get; init; }
        public string? ArchivedPath { get; init; }

        public decimal Amount => Money.FromMinor(AmountMinor);
        public bool IsTransfer => Kind == "transfer";
        public bool IsNegative => AmountMinor < 0;
        public bool NeedsDate => Date is null;
        /// <summary>CHECK 不準 transfer 有分類，所以轉帳那列的分類下拉直接鎖住。</summary>
        public bool CanPickCategory => !IsTransfer;
        /// <summary>列首的 ●：人工改過。重匯時這種列只准更新 source_raw。</summary>
        public string Dot => Edited ? "●" : "";
        /// <summary>分類下拉的選項，依這列的種類過濾（由 LedgerStore 裝載時填）。</summary>
        public List<Option> Categories { get; set; } = [];
        public string DateText => Date ?? "?";
        public string StatusText => Status switch
        {
            "cleared" => "核銷", "imported" => "待補", "pending" => "未發生",
            "ignored" => "忽略", "duplicate" => "重複", _ => Status,
        };
    }

    public sealed class Filter
    {
        public long? AccountId { get; init; }
        public long? CategoryId { get; init; }
        public bool Uncategorized { get; init; }                    // 下拉的「未分類」
        /// <summary>ledger（預設）/ imported / ignored / duplicate / dupgroup（疑似重複那一組）/ orphan / deleted / all。</summary>
        public string Status { get; init; } = "ledger";
        public string? From { get; init; }
        public string? To { get; init; }
        public string? Search { get; init; }
        public int Limit { get; init; } = 2000;                     // shortcut: 先封頂 2000 列，要更多再做分頁
    }

    // ---------------------------------------------------------------- 清單

    public List<Row> List(Filter f)
    {
        var where = f.Status switch
        {
            "imported"  => "e.deleted_at IS NULL AND e.status = 'imported'",
            "ignored"   => "e.deleted_at IS NULL AND e.status = 'ignored'",
            "duplicate" => "e.deleted_at IS NULL AND e.status = 'duplicate'",
            "dupgroup"  => DuplicateGroupWhere,
            "orphan"    => "e.deleted_at IS NULL AND e.orphaned_at IS NOT NULL",
            "deleted"   => "e.deleted_at IS NOT NULL",
            "all"       => "e.deleted_at IS NULL",
            _           => Live,
        };

        var ps = new List<(string, object?)> { ("$limit", f.Limit) };
        if (f.AccountId is { } acct) { where += " AND e.account_id = $acct"; ps.Add(("$acct", acct)); }
        if (f.CategoryId is { } cat) { where += " AND e.category_id = $cat"; ps.Add(("$cat", cat)); }
        if (f.Uncategorized) where += " AND e.category_id IS NULL";
        // 沒有消費日的列不論篩哪個區間都要看得到，否則使用者永遠沒機會補它（排在最後＋紅色 ?）
        if (f.From is { } from) { where += " AND (e.txn_date IS NULL OR e.txn_date >= $from)"; ps.Add(("$from", from)); }
        if (f.To is { } to) { where += " AND (e.txn_date IS NULL OR e.txn_date <= $to)"; ps.Add(("$to", to)); }
        if (!string.IsNullOrWhiteSpace(f.Search)) { where += " AND e.merchant LIKE $q"; ps.Add(("$q", $"%{f.Search.Trim()}%")); }

        var sql = $"""
            SELECT e.id, e.txn_date, e.merchant, e.note, e.category_id,
                   COALESCE(p.name || '／', '') || c.name, e.amount_minor, e.kind, e.currency, e.orig_currency,
                   a.id, a.name, a.type, e.status, e.edited_at, e.orphaned_at,
                   e.source_raw, d.file_name, e.source_page, d.issuer, d.parser, d.archived_path
            FROM entry e
            JOIN account a ON a.id = e.account_id
            LEFT JOIN category c ON c.id = e.category_id
            LEFT JOIN category p ON p.id = c.parent_id
            LEFT JOIN document d ON d.id = e.source_document_id
            WHERE {where}
            ORDER BY (e.txn_date IS NULL), e.txn_date DESC, e.id DESC
            LIMIT $limit;
            """;

        var rows = new List<Row>();
        var expenseCats = Categories("expense");
        var incomeCats = Categories("income");
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = sql;
            foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var row = new Row
                {
                    Id = r.GetInt64(0), Date = Str(r, 1), Merchant = Str(r, 2), Note = Str(r, 3),
                    CategoryId = NullableLong(r, 4), Category = Str(r, 5), AmountMinor = r.GetInt64(6),
                    Kind = r.GetString(7), Currency = r.GetString(8), OrigCurrency = Str(r, 9),
                    AccountId = r.GetInt64(10), Account = r.GetString(11), AccountType = r.GetString(12),
                    Status = r.GetString(13), Edited = r.IsDBNull(14) == false, Orphaned = r.IsDBNull(15) == false,
                    SourceRaw = Str(r, 16), SourceFile = Str(r, 17), SourcePage = NullableInt(r, 18),
                    Issuer = Str(r, 19), Parser = Str(r, 20), ArchivedPath = Str(r, 21),
                };
                row.Categories = row.Kind == "income" ? incomeCats : expenseCats;   // adjustment 當支出側給選
                rows.Add(row);
            }
        }
        return rows;
    }

    /// <summary>
    /// 跨檔案同額同日 = 疑似重複。只做信用卡帳戶（存款單/繳費單沒有可靠身份，比了會把不同戶號的合成一筆）；
    /// 而且只標記不改資料 —— 兩筆都留在帳本裡，由人決定要不要標 duplicate。
    /// </summary>
    const string DuplicateGroupKey = """
        SELECT e.txn_date, e.amount_minor, e.currency
        FROM entry e JOIN account a ON a.id = e.account_id
        WHERE e.deleted_at IS NULL AND e.source_document_id IS NOT NULL AND e.kind <> 'transfer'
          AND e.txn_date IS NOT NULL AND a.type = 'credit_card'
        GROUP BY e.txn_date, e.amount_minor, e.currency
        HAVING COUNT(DISTINCT e.source_document_id) > 1
        """;

    const string DuplicateGroupWhere = $"""
        e.deleted_at IS NULL AND EXISTS (
            SELECT 1 FROM ({DuplicateGroupKey}) g
            WHERE g.txn_date = e.txn_date AND g.amount_minor = e.amount_minor AND g.currency = e.currency)
        """;

    // ---------------------------------------------------------------- 概況

    public sealed record Kpi(long IncomeMinor, long ExpenseMinor, long NetMinor, int Uncategorized, long UncategorizedMinor);

    /// <summary>本月收支。加總把 transfer 排除：繳款列若算成支出會_double count_（見 db-design）。</summary>
    public Kpi Totals(string? from, string? to, long? accountId)
    {
        var sql = $"""
            SELECT SUM(CASE WHEN e.kind = 'income' THEN e.amount_minor END),
                   SUM(CASE WHEN e.kind = 'expense' THEN e.amount_minor END),
                   SUM(CASE WHEN e.kind IN ('income','adjustment') THEN e.amount_minor ELSE -e.amount_minor END),
                   SUM(CASE WHEN e.category_id IS NULL AND e.kind NOT IN ('transfer','refund') THEN 1 ELSE 0 END),
                   SUM(CASE WHEN e.category_id IS NULL AND e.kind NOT IN ('transfer','refund') THEN e.amount_minor ELSE 0 END)
            FROM entry e
            WHERE {Live} AND e.kind <> 'transfer'
              AND ($from IS NULL OR e.txn_date >= $from) AND ($to IS NULL OR e.txn_date <= $to)
              AND ($acct IS NULL OR e.account_id = $acct);
            """;
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$from", (object?)from ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$to", (object?)to ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$acct", (object?)accountId ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        r.Read();
        return new Kpi(Lang(r, 0), Lang(r, 1), Lang(r, 2), (int)Lang(r, 3), Lang(r, 4));
    }

    public sealed record AccountBalance(long Id, string Name, string Type, string Currency, long BalanceMinor,
                                        bool CountedInNet, long ConvertedMinor, decimal? Rate, int ForeignEntries)
    {
        /// <summary>總表給人看的文字。口徑留在 Data 層，UI 不准自己拼。</summary>
        public string TypeLabel => LedgerStore.TypeLabel(Type);
        public string BalanceText => Money.FromMinor(BalanceMinor).ToString("N0");
        public string ConvertedText => CountedInNet ? Money.FromMinor(ConvertedMinor).ToString("N0") : "—";
        public string CountedText => ForeignEntries > 0
            ? $"{(CountedInNet ? "✓" : "未折算")}　⚠ 含 {ForeignEntries} 筆幣別不符的列（已按帳戶幣別算）"
            : CountedInNet ? (Currency == "TWD" ? "✓" : $"✓（{Currency} × {Rate:0.####}）")
                           : $"未折算（{Currency} 沒有匯率）";
        /// <summary>首頁淨值表的分組：銀行 → 信用卡 → 其它。</summary>
        public int GroupOrder => Type switch { "bank" => 0, "credit_card" => 1, _ => 2 };
        public string Group => Type switch { "bank" => "銀行", "credit_card" => "信用卡", _ => "其它" };
    }
    public sealed record NetWorthReport(long AssetsMinor, long LiabilitiesMinor, long NetMinor,
                                        int UnconvertedAccounts, IReadOnlyList<AccountBalance> Accounts);

    /// <summary>
    /// 餘額一律当场由交易推（derived）：期初 + 收入 −（支出/繳款/退款）+ 轉入。
    /// 餘額快照（account_balance，含人工盤點）只拿來當校驗，對不上就在警示列講，不覆蓋這個數字。
    /// foreign_rows = 幣別跟帳戶不一樣的列有幾筆（王道那類：台幣帳戶裡混了美金列），
    /// 那些金額是照帳戶幣別算進餘額的，所以要在總表上讲明白，别让人当准数。
    /// </summary>
    const string DerivedBalancesCore = """
        SELECT a.id, a.name, a.type, a.currency, a.is_archived, a.opening_balance_minor, a.opening_date,
               a.opening_balance_minor + COALESCE(s.delta, 0) + COALESCE(t.in_minor, 0) AS bal,
               COALESCE(f.n, 0) AS foreign_rows
        FROM account a
        LEFT JOIN (
            SELECT account_id, SUM(CASE WHEN kind IN ('income','adjustment') THEN amount_minor ELSE -amount_minor END) AS delta
            FROM entry WHERE deleted_at IS NULL AND status IN ('cleared','imported')
            GROUP BY account_id
        ) s ON s.account_id = a.id
        LEFT JOIN (
            SELECT to_account_id AS account_id, SUM(amount_minor) AS in_minor
            FROM entry WHERE kind = 'transfer' AND deleted_at IS NULL AND status IN ('cleared','imported')
            GROUP BY to_account_id
        ) t ON t.account_id = a.id
        LEFT JOIN (
            SELECT e.account_id, COUNT(*) AS n
            FROM entry e JOIN account a2 ON a2.id = e.account_id
            WHERE e.deleted_at IS NULL AND e.currency <> a2.currency
            GROUP BY e.account_id
        ) f ON f.account_id = a.id
        """;

    const string DerivedBalances = $"""
        SELECT id, name, type, currency, bal, foreign_rows
        FROM ({DerivedBalancesCore}) WHERE is_archived = 0 ORDER BY type, name
        """;

    /// <summary>每種幣最新的匯率（1 外幣 = ? TWD）。遷移 seed 了 USD 32.9，人工改的也讀同一張表。</summary>
    public Dictionary<string, decimal> Rates()
    {
        var map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT currency, rate FROM (
                SELECT currency, rate, ROW_NUMBER() OVER (PARTITION BY currency ORDER BY rate_date DESC) rn
                FROM exchange_rate) WHERE rn = 1;
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetString(0)] = Convert.ToDecimal(r.GetValue(1));
        return map;
    }

    /// <summary>人工填匯率。留空 = 故意不折算（那个帳戶就不進淨值，比瞎算 1:1 好）。</summary>
    public void SetRate(string currency, decimal? rate)
    {
        if (currency.Equals("TWD", StringComparison.OrdinalIgnoreCase)) return;      // 本幣沒有匯率這回事
        if (rate is null) Db.Exec(_db, "DELETE FROM exchange_rate WHERE currency=$c;", ("$c", currency));
        else Db.Exec(_db, """
            INSERT INTO exchange_rate(currency, rate_date, rate, source) VALUES($c, $d, $r, 'manual')
            ON CONFLICT(currency, rate_date) DO UPDATE SET rate=excluded.rate, source='manual';
            """, ("$c", currency), ("$d", Db.Today()), ("$r", (double)rate.Value));
        Touch();
    }

    /// <summary>
    /// 淨值。台幣帳戶直接計；外幣帳戶有匯率才計（<c>exchange_rate</c>：USD 由遷移 seed 32.9，其餘靠人在帳戶管理裡填，自動更新之後再做），
    /// 只回報「N 個帳戶未折算」讓人知道數字不完整。
    /// </summary>
    public NetWorthReport NetWorth()
    {
        var list = new List<AccountBalance>();
        var rates = Rates();
        long assets = 0, liabilities = 0;
        int unconverted = 0;

        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = DerivedBalances;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var currency = r.GetString(3);
                var bal = r.GetInt64(4);
                decimal? rate = currency.Equals("TWD", StringComparison.OrdinalIgnoreCase) ? 1m
                              : rates.TryGetValue(currency, out var rv) ? rv : null;
                bool counted = rate is not null;                       // 沒匯率寧可不計，不瞎算 1:1
                var converted = counted ? Money.Convert(bal, rate!.Value) : 0;
                list.Add(new AccountBalance(r.GetInt64(0), r.GetString(1), r.GetString(2), currency, bal,
                                            counted, converted, rate, r.GetInt32(5)));
                if (!counted) { unconverted++; continue; }
                if (converted >= 0) assets += converted; else liabilities += converted;
            }
        }
        return new NetWorthReport(assets, liabilities, assets + liabilities, unconverted, list);
    }

    public sealed record MonthPoint(DateOnly Month, long NetMinor);

    /// <summary>
    /// 每月月底的總資產（台幣），首頁折線圖用。口徑跟 <see cref="NetWorth"/> 一樣：
    /// 只算未歸檔帳戶、沒匯率的外幣不計。shortcut: 全部用「現在」的匯率，不存歷史匯率（要準得先有 rates 歷史）。
    /// </summary>
    public List<MonthPoint> MonthlyNetWorth(DateOnly? through = null)
    {
        var endMonth = through ?? DateOnly.FromDateTime(DateTime.Today);
        endMonth = new DateOnly(endMonth.Year, endMonth.Month, 1);

        var rates = Rates();
        var accounts = new Dictionary<long, (long BalanceMinor, decimal Rate)>();
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT id, currency, opening_balance_minor FROM account WHERE is_archived = 0;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var currency = r.GetString(1);
                decimal? rate = currency.Equals("TWD", StringComparison.OrdinalIgnoreCase) ? 1m
                              : rates.TryGetValue(currency, out var rv) ? rv : null;
                if (rate is not null) accounts[r.GetInt64(0)] = (r.GetInt64(2), rate.Value);
            }
        }
        if (accounts.Count == 0) return [new MonthPoint(endMonth, 0)];

        // 逐月的餘額變動。轉帳從轉出帳戶扣、加到轉入帳戶（淨值不變，但兩個帳戶的餘額要對）。
        var moves = new List<(string Month, long AccountId, long Delta)>();
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT account_id, to_account_id, kind, amount_minor, substr(txn_date, 1, 7)
                FROM entry
                WHERE deleted_at IS NULL AND status IN ('cleared','imported') AND txn_date IS NOT NULL;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var month = r.GetString(4);
                var kind = r.GetString(2);
                var amount = r.GetInt64(3);
                if (accounts.ContainsKey(r.GetInt64(0)))
                    moves.Add((month, r.GetInt64(0), kind is "income" or "adjustment" ? amount : -amount));
                if (kind == "transfer" && !r.IsDBNull(1) && accounts.ContainsKey(r.GetInt64(1)))
                    moves.Add((month, r.GetInt64(1), amount));
            }
        }

        var first = moves.Count == 0 ? endMonth
                  : DateOnly.ParseExact(moves.Select(m => m.Month).Min()!, "yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        if (first > endMonth) first = endMonth;

        var byMonth = moves.GroupBy(m => m.Month).ToDictionary(g => g.Key, g => g.ToList());
        var points = new List<MonthPoint>();
        for (var month = first; month <= endMonth; month = month.AddMonths(1))
        {
            if (byMonth.TryGetValue(month.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture), out var list))
                foreach (var m in list)
                {
                    var (bal, rate) = accounts[m.AccountId];
                    accounts[m.AccountId] = (bal + m.Delta, rate);
                }
            points.Add(new MonthPoint(month, accounts.Values.Sum(a => Money.Convert(a.BalanceMinor, a.Rate))));
        }
        return points;
    }

    /// <summary>首頁右下角的未來帳單：還沒到的 entry ＋ 未繳且未到期的信用卡帳單。</summary>
    public sealed record UpcomingItem(string Date, string Label, decimal Amount);

    public List<UpcomingItem> Upcoming()
    {
        var list = new List<UpcomingItem>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT due, label, amount_minor FROM (
                SELECT e.txn_date AS due,
                       COALESCE(e.merchant, '(未命名)') || '（' || a.name || '）' AS label,
                       e.amount_minor AS amount_minor
                FROM entry e JOIN account a ON a.id = e.account_id
                WHERE e.deleted_at IS NULL AND e.status IN ('cleared','imported','pending')
                  AND e.txn_date > $today
                UNION ALL
                SELECT b.due_date, '信用卡帳單 ' || a.name, b.total_due_minor
                FROM credit_card_bill b JOIN card k ON k.id = b.card_id JOIN account a ON a.id = k.account_id
                WHERE b.paid_at IS NULL AND b.due_date >= $today AND b.total_due_minor IS NOT NULL
                  AND a.is_archived = 0
            ) ORDER BY due LIMIT 50;
            """;
        cmd.Parameters.AddWithValue("$today", Db.Today());
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new UpcomingItem(r.GetString(0), r.GetString(1), Money.FromMinor(r.GetInt64(2))));
        return list;
    }

    /// <summary>卡未繳：帳單口徑，查 <c>credit_card_bill</c>，不從 entry 推（尚未發生的款項不進帳本表）。</summary>
    public long CardUnpaidMinor()
        => Lang(_db, """
            SELECT COALESCE(SUM(b.total_due_minor), 0)
            FROM credit_card_bill b JOIN card k ON k.id = b.card_id JOIN account a ON a.id = k.account_id
            WHERE b.paid_at IS NULL AND b.total_due_minor IS NOT NULL AND a.is_archived = 0;
            """);

    /// <summary>底部警示列：孤兒列筆數 + 疑似重複的組數。</summary>
    public (int Orphans, int DuplicateGroups) Flags()
        => ((int)Lang(_db, "SELECT count(*) FROM entry WHERE deleted_at IS NULL AND orphaned_at IS NOT NULL;"),
            (int)Lang(_db, $"SELECT count(*) FROM ({DuplicateGroupKey}) g;"));

    /// <summary>
    /// 餘額快照 vs 交易累加對不上的帳戶（parser 漏列的自動校驗，比「解析到 0 筆才警告」強）。
    /// 快照有兩種來源：存款單解析出來的（statement）與人工盤點（manual）—— 每個帳戶只看最近那一次，
    /// 同一天有雨者時以盤點為準（人當場數的比單子新）。
    /// </summary>
    public List<(string Account, long DiffMinor, string Source, string AsOf)> BalanceMismatches()
    {
        var list = new List<(string, long, string, string)>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"""
            SELECT v.name, ab.balance_minor - v.bal, ab.source, ab.as_of
            FROM (
                SELECT account_id, as_of, currency, balance_minor, source FROM (
                    SELECT account_id, as_of, currency, balance_minor, source,
                           ROW_NUMBER() OVER (PARTITION BY account_id
                                              ORDER BY as_of DESC, (source = 'manual') DESC) rn
                    FROM account_balance) WHERE rn = 1
            ) ab
            JOIN ({DerivedBalances}) v ON v.id = ab.account_id
            WHERE ab.currency = v.currency AND ab.balance_minor <> v.bal;
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetString(3)));
        return list;
    }

    // ---------------------------------------------------------------- 下拉的資料

    public sealed record Option(long Id, string Name);

    List<(string Kind, List<Option> Items)> _categoryCache = [];

    /// <summary>分類下拉。只給方向對的那組：expense 列選不到 income 分類（靠 CHECK 會炸，不如不給選）。</summary>
    public List<Option> Categories(string kind)
    {
        var hit = _categoryCache.FirstOrDefault(c => c.Kind == kind);
        if (hit.Items is not null) return hit.Items;
        var items = new List<Option>();
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT c.id, COALESCE(p.name || '／', '') || c.name
                FROM category c LEFT JOIN category p ON p.id = c.parent_id
                WHERE c.kind = $k ORDER BY c.sort_order, c.name;
                """;
            cmd.Parameters.AddWithValue("$k", kind);
            using var r = cmd.ExecuteReader();
            while (r.Read()) items.Add(new Option(r.GetInt64(0), r.GetString(1)));
        }
        _categoryCache.Add((kind, items));
        return items;
    }

    /// <summary>「新增分類」：同名就沿用既有的，不准憑空多一個一樣的。</summary>
    public long AddCategory(string name, string kind)
    {
        name = name.Trim();
        var existing = Db.Scalar(_db, "SELECT id FROM category WHERE name=$n AND kind=$k;", ("$n", name), ("$k", kind));
        if (existing is long id) return id;
        var newId = Db.InsertId(_db, "INSERT INTO category(name, kind, is_system) VALUES($n, $k, 0);", ("$n", name), ("$k", kind));
        _categoryCache = [];
        return newId;
    }

    public sealed record AccountOption(long Id, string Name, string Type, string Currency, bool IsArchived = false);

    public List<AccountOption> Accounts(bool includeArchived = false)
    {
        var list = new List<AccountOption>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id, name, type, currency, is_archived FROM account"
                        + (includeArchived ? "" : " WHERE is_archived = 0")
                        + " ORDER BY is_archived, type, name;";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new AccountOption(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4) == 1));
        return list;
    }

    // ---------------------------------------------------------------- 帳戶管理

    /// <summary>帳戶型別：CHECK 認得的就這八個。UI 下拉直接用這個，不准自己加（加了存下去會被擋）。</summary>
    public static readonly (string Value, string Label)[] AccountTypes =
    [
        ("cash", "現金"), ("bank", "銀行"), ("wallet", "錢包"), ("credit_card", "信用卡"),
        ("term_deposit", "定存"), ("investment", "投資"), ("loan", "貸款"), ("asset", "資產"),
    ];

    public static string TypeLabel(string type) => AccountTypes.FirstOrDefault(t => t.Value == type).Label ?? type;

    public List<string> Currencies()
        => Db.Query(_db, "SELECT code FROM currency ORDER BY code;").Select(o => (string)o!).ToList();

    /// <summary>帳戶管理表單的一筆。<see cref="BalanceMinor"/> 是交易推出來的，<see cref="ManualBalanceMinor"/> 是上次盤點的。</summary>
    public sealed record AccountEdit(long Id, string Name, string Type, string Currency, string? Issuer, string? Mask,
                                     long OpeningBalanceMinor, string? OpeningDate, bool IsArchived,
                                     long? CreditLimitMinor, long? AutopayAccountId, long BalanceMinor,
                                     long? ManualBalanceMinor, string? ManualBalanceDate)
    {
        /// <summary>帳戶管理左側清單那一行。</summary>
        public string ListLabel => $"{Name}　{(Currency == "TWD" ? "" : Currency + " ")}{Money.FromMinor(BalanceMinor):N0}"
                                 + (IsArchived ? "　（已歸檔）" : "");
    }

    const string AccountsAllSql = $"""
        SELECT a.id, a.name, a.type, a.currency, a.issuer, a.mask, a.opening_balance_minor, a.opening_date,
               a.is_archived, k.credit_limit_minor, k.autopay_account_id, v.bal, mb.balance_minor, mb.as_of
        FROM ({DerivedBalancesCore}) v
        JOIN account a ON a.id = v.id
        LEFT JOIN card k ON k.id = (SELECT MIN(id) FROM card c2 WHERE c2.account_id = a.id)
        LEFT JOIN (
            SELECT account_id, balance_minor, as_of FROM (
                SELECT account_id, balance_minor, as_of, source,
                       ROW_NUMBER() OVER (PARTITION BY account_id ORDER BY as_of DESC, (source = 'manual') DESC) rn
                FROM account_balance) WHERE rn = 1
        ) mb ON mb.account_id = a.id
        ORDER BY a.is_archived, a.type, a.name;
        """;

    /// <summary>含已歸檔的：匯入自動建出來的帳戶看得到，但归檔後若不在清單里就再也改不回來。</summary>
    public List<AccountEdit> AccountsAll()
    {
        var list = new List<AccountEdit>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = AccountsAllSql;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new AccountEdit(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), Str(r, 4), Str(r, 5),
                                     r.GetInt64(6), Str(r, 7), r.GetInt32(8) == 1, NullableLong(r, 9), NullableLong(r, 10),
                                     r.GetInt64(11), NullableLong(r, 12), Str(r, 13)));
        return list;
    }

    /// <summary>帳戶管理表单用：账户就那十幾個，直接从清单里找，不另拼一份 SQL。</summary>
    public AccountEdit? GetAccount(long id) => AccountsAll().FirstOrDefault(a => a.Id == id);

    /// <summary>交易推出來的餘額（已歸檔的也算，盘點跟對帳要看得到）。</summary>
    public long DerivedBalance(long accountId)
        => Db.Scalar(_db, $"SELECT bal FROM ({DerivedBalancesCore}) WHERE id=$id;", ("$id", accountId)) is long v ? v : 0;

    void ValidateAccount(AccountEdit e)
    {
        if (string.IsNullOrWhiteSpace(e.Name)) throw new ArgumentException("帳戶要取名字。");
        if (!AccountTypes.Any(t => t.Value == e.Type)) throw new ArgumentException($"不認識的帳戶型別：{e.Type}");
        if (string.IsNullOrWhiteSpace(e.Currency)) throw new ArgumentException("要選幣別。");
        if (e.OpeningDate is not null && !TryDate(e.OpeningDate, out _)) throw new ArgumentException("期初日期要用 yyyy/MM/dd。");
        if (e.AutopayAccountId is { } ap && (ap == e.Id || Db.Scalar(_db, "SELECT 1 FROM account WHERE id=$i;", ("$i", ap)) is null))
            throw new ArgumentException("自動扣繳帳戶不存在（也不能是自己）。");
    }

    /// <summary>同名先擋下来講人話，不讓 UNIQUE 的 SqliteException 冒到 UI。</summary>
    void EnsureNameFree(string name, long selfId)
    {
        if (Db.Scalar(_db, "SELECT id FROM account WHERE name=$n AND id <> $id;", ("$n", name), ("$id", selfId)) is long)
            throw new ArgumentException($"已經有叫「{name}」的帳戶了（归檔中的也算）。");
    }

    public long CreateAccount(AccountEdit e)
    {
        ValidateAccount(e);
        var name = e.Name.Trim();
        EnsureNameFree(name, 0);
        var id = Db.InsertId(_db, """
            INSERT INTO account(name, type, currency, issuer, mask, opening_balance_minor, opening_date, created_at)
            VALUES($n, $t, $c, $i, $m, $o, $d, $at);
            """,
            ("$n", name), ("$t", e.Type), ("$c", e.Currency.Trim()),
            ("$i", (object?)e.Issuer ?? DBNull.Value), ("$m", (object?)e.Mask ?? DBNull.Value),
            ("$o", e.OpeningBalanceMinor),
            ("$d", (object?)e.OpeningDate ?? DBNull.Value), ("$at", Db.Now()));
        SaveCard(id, e);
        Touch();
        return id;
    }

    public void UpdateAccount(AccountEdit e)
    {
        if (e.Id <= 0) throw new ArgumentException("還沒選帳戶，無法儲存（要新建按「新增」）。");
        ValidateAccount(e);
        var name = e.Name.Trim();
        EnsureNameFree(name, e.Id);
        Db.Exec(_db, """
            UPDATE account SET name=$n, type=$t, currency=$c, issuer=$i, mask=$m,
                               opening_balance_minor=$o, opening_date=$d
            WHERE id=$id;
            """,
            ("$n", name), ("$t", e.Type), ("$c", e.Currency.Trim()),
            ("$i", (object?)e.Issuer ?? DBNull.Value), ("$m", (object?)e.Mask ?? DBNull.Value),
            ("$o", e.OpeningBalanceMinor), ("$d", (object?)e.OpeningDate ?? DBNull.Value), ("$id", e.Id));
        SaveCard(e.Id, e);
        Touch();
    }

    /// <summary>
    /// 卡的額度與自動扣繳在 <c>card</c> 表（一張卡一列）。不用 ON CONFLICT：既有卡列的 mask 常是 NULL，
    /// UNIQUE(account_id, mask) 对 NULL 不當重複，會每次都插一筆新的。
    /// 型別不是信用卡就不動 card（匯入建的卡列留著）。
    /// </summary>
    void SaveCard(long accountId, AccountEdit e)
    {
        if (e.Type != "credit_card") return;
        var n = Db.Exec(_db, "UPDATE card SET credit_limit_minor=$lim, autopay_account_id=$ap WHERE account_id=$a;",
                        ("$lim", (object?)e.CreditLimitMinor ?? DBNull.Value),
                        ("$ap", (object?)e.AutopayAccountId ?? DBNull.Value), ("$a", accountId));
        if (n == 0) Db.Exec(_db, "INSERT INTO card(account_id, credit_limit_minor, autopay_account_id) VALUES($a, $lim, $ap);",
                            ("$a", accountId),
                            ("$lim", (object?)e.CreditLimitMinor ?? DBNull.Value),
                            ("$ap", (object?)e.AutopayAccountId ?? DBNull.Value));
    }

    /// <summary>歸檔（軟刪）。真刪會連交易的血統一起斷，而且帳戶可能已經有交易。</summary>
    public void SetArchived(long id, bool archived)
        => Write("UPDATE account SET is_archived=$a WHERE id=$id;", ("$a", archived ? 1 : 0), ("$id", id));

    /// <summary>
    /// 餘額盤點：人當場數的餘額寫進 <c>account_balance</c>（source='manual'）。
    /// 這是餘額校驗的輸入之一 —— 對不上就進警示列，但不覆蓋 derived 那個數字。
    /// 要補差額請記一筆 adjustment（調帳走 adjustment，金額唯讀）。
    /// </summary>
    public void AddBalanceSnapshot(long accountId, string asOf, string currency, long balanceMinor)
    {
        if (!TryDate(asOf, out var d) || d is null) throw new ArgumentException("盤點日期要用 yyyy/MM/dd。");
        Write("""
            INSERT INTO account_balance(account_id, as_of, currency, balance_minor, source)
            VALUES($a, $d, $c, $b, 'manual')
            ON CONFLICT(account_id, as_of, currency) DO UPDATE SET balance_minor=excluded.balance_minor;
            """, ("$a", accountId), ("$d", d), ("$c", currency), ("$b", balanceMinor));
    }

    // ---------------------------------------------------------------- 寫入

    /// <summary>日期欄位要能補，但准許的格式只有這兩種；其它一律擋下來而不是猜。</summary>
    public static bool TryDate(string? text, out string? dbDate)
    {
        dbDate = null;
        if (string.IsNullOrWhiteSpace(text)) return true;                              // 清空 = 留 NULL 等人補
        var s = text.Trim().Replace('/', '-');
        if (!DateOnly.TryParseExact(s, "yyyy-M-d", out var d)) return false;
        dbDate = d.ToString("yyyy-MM-dd");
        return true;
    }

    void Write(string sql, params (string Name, object? Value)[] ps)
    {
        Db.Exec(_db, sql, ps);
        Touch();
    }

    /// <summary>使用者動過的列要寫 <c>edited_at</c>：重匯時匯入器只准更新 source_raw，不然人工分類會被蓋掉。</summary>
    public void SetCategory(long id, long? categoryId)
        => Write("UPDATE entry SET category_id=$c, edited_at=$at, updated_at=$at WHERE id=$id;",
                 ("$c", (object?)categoryId ?? DBNull.Value), ("$at", Db.Now()), ("$id", id));

    public void SetMerchant(long id, string? merchant)
        => Write("UPDATE entry SET merchant=$m, edited_at=$at, updated_at=$at WHERE id=$id;",
                 ("$m", (object?)merchant ?? DBNull.Value), ("$at", Db.Now()), ("$id", id));

    public void SetNote(long id, string? note)
        => Write("UPDATE entry SET note=$n, edited_at=$at, updated_at=$at WHERE id=$id;",
                 ("$n", (object?)note ?? DBNull.Value), ("$at", Db.Now()), ("$id", id));

    /// <summary>補消費日。解析不到日期的列就靠這個補，不准塞 0001-01-01。</summary>
    public void SetDate(long id, string? dbDate)
        => Write("UPDATE entry SET txn_date=$d, edited_at=$at, updated_at=$at WHERE id=$id;",
                 ("$d", (object?)dbDate ?? DBNull.Value), ("$at", Db.Now()), ("$id", id));

    public void SetStatus(long id, string status)
    {
        if (status is not ("cleared" or "imported" or "pending" or "ignored" or "duplicate"))
            throw new ArgumentException($"不認識的狀態：{status}");
        Write("UPDATE entry SET status=$s, edited_at=$at, updated_at=$at WHERE id=$id;",
              ("$s", status), ("$at", Db.Now()), ("$id", id));
    }

    /// <summary>軟刪除。真刪會連血統一起不見，而血統是之後查 parser 漏列的唯一依據。</summary>
    public void SoftDelete(long id)
        => Write("UPDATE entry SET deleted_at=$at, updated_at=$at WHERE id=$id;", ("$at", Db.Now()), ("$id", id));

    public void Restore(long id)
        => Write("UPDATE entry SET deleted_at=NULL, updated_at=$at WHERE id=$id;", ("$at", Db.Now()), ("$id", id));

    public sealed record ManualEntry(long AccountId, string Kind, long AmountMinor, string TxnDate,
                                     string? Merchant, long? CategoryId, long? ToAccountId = null, string? Note = null,
                                     string Currency = "TWD");

    /// <summary>現金/錢包一定沒有 PDF，手動記的筆直接算數（status=cleared），不必再確認。</summary>
    public long InsertManual(ManualEntry e)
    {
        if (e.Kind is not ("income" or "expense" or "transfer" or "refund" or "adjustment"))
            throw new ArgumentException($"不認識的種類：{e.Kind}");
        if (e.Kind is "expense" or "income" && e.AmountMinor <= 0) throw new ArgumentException("金額要比 0 大（退款用 refund，調整用 adjustment）");
        if (e.Kind is not ("refund" or "adjustment") && e.AmountMinor == 0) throw new ArgumentException("金額不准是 0");
        if (e.Kind == "transfer" && (e.ToAccountId is null || e.ToAccountId == e.AccountId))
            throw new ArgumentException("轉帳要選「轉入帳戶」，而且不能跟轉出帳戶相同");
        if (e.Kind == "transfer" && e.CategoryId is not null) throw new ArgumentException("轉帳不準有分類");

        return Db.InsertId(_db, """
            INSERT INTO entry(kind, account_id, to_account_id, category_id, currency, amount_minor,
                              txn_date, merchant, note, status, source, created_at)
            VALUES($kind, $acct, $to, $cat, $cur, $amt, $date, $merchant, $note, 'cleared', 'manual', $at);
            """,
            ("$kind", e.Kind), ("$acct", e.AccountId),
            ("$to", (object?)e.ToAccountId ?? DBNull.Value),
            ("$cat", (object?)e.CategoryId ?? DBNull.Value), ("$cur", e.Currency),
            ("$amt", e.AmountMinor), ("$date", e.TxnDate),
            ("$merchant", (object?)e.Merchant ?? DBNull.Value),
            ("$note", (object?)e.Note ?? DBNull.Value), ("$at", Db.Now()));
    }

    // ---------------------------------------------------------------- 小工具

    static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    static long? NullableLong(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    static int? NullableInt(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    static long Lang(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetInt64(i);
    static long Lang(SqliteConnection c, string sql) => Db.Scalar(c, sql) is long v ? v : 0;
}
