using System.ComponentModel;
using dFinance.Data.Localization;
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
            // 只存 Id；顯示文字是另一個欄位 Category，要由編輯端一起換（見 LedgerTab 的分類欄），
            // 否則格子裡還是舊字串，看起來像沒儲存。
            set => Set(ref _categoryId, value);
        }
        public string? Category { get => _category; set => Set(ref _category, value); }
        public long AmountMinor { get; init; }
        public string Kind { get; init; } = "";
        public string Currency { get; init; } = "TWD";
        /// <summary>這個幣別顯示幾位小數（<c>currency.scale</c>）；加總一律用 AmountMinor，不用這個。</summary>
        public int Scale { get; init; } = 2;
        /// <summary>清單的金額欄：顯示小數位跟幣別走（美金 1,250.00、台幣 3,000）。</summary>
        public string AmountText => Money.Format(AmountMinor, Scale);
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
            "cleared" => Loc.T("Store_Status_Settled"), "imported" => Loc.T("Store_Status_Pending"), "pending" => Loc.T("Store_Status_NotYet"),
            "ignored" => Loc.T("Store_Status_Ignored"), "duplicate" => Loc.T("Store_Status_Duplicate"), _ => Status,
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
        if (f.CategoryId is { } cat)
        {
            where += " AND e.category_id IN (WITH RECURSIVE sub(id) AS (SELECT $cat UNION SELECT c.id FROM category c JOIN sub s ON c.parent_id = s.id) SELECT id FROM sub)";
            ps.Add(("$cat", cat));
        }
        if (f.Uncategorized) where += " AND e.category_id IS NULL AND e.kind NOT IN ('transfer','refund')";
        // 沒有消費日的列不論篩哪個區間都要看得到，否則使用者永遠沒機會補它（排在最後＋紅色 ?）
        if (f.From is { } from) { where += " AND (e.txn_date IS NULL OR e.txn_date >= $from)"; ps.Add(("$from", from)); }
        if (f.To is { } to) { where += " AND (e.txn_date IS NULL OR e.txn_date <= $to)"; ps.Add(("$to", to)); }
        if (!string.IsNullOrWhiteSpace(f.Search)) { where += " AND e.merchant LIKE $q"; ps.Add(("$q", $"%{f.Search.Trim()}%")); }

        var sql = $"""
            WITH RECURSIVE cat_path(id, path) AS (
                SELECT id, CAST(name AS TEXT) FROM category WHERE parent_id IS NULL
                UNION ALL
                SELECT c.id, cat_path.path || '／' || c.name FROM category c JOIN cat_path ON c.parent_id = cat_path.id)
            SELECT e.id, e.txn_date, e.merchant, e.note, e.category_id,
                   cp.path, e.amount_minor, e.kind, e.currency, e.orig_currency,
                   a.id, a.name, a.type, e.status, e.edited_at, e.orphaned_at,
                   e.source_raw, d.file_name, e.source_page, d.issuer, d.parser, d.archived_path, ec.scale
            FROM entry e
            JOIN account a ON a.id = e.account_id
            LEFT JOIN cat_path cp ON cp.id = e.category_id
            LEFT JOIN document d ON d.id = e.source_document_id
            LEFT JOIN currency ec ON ec.code = e.currency
            WHERE {where}
            ORDER BY (e.txn_date IS NULL), e.txn_date DESC, e.id DESC
            LIMIT $limit;
            """;

        var rows = new List<Row>();
        var expenseCats = Categories("expense", leavesOnly: true);
        var incomeCats = Categories("income", leavesOnly: true);
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
                    Scale = r.IsDBNull(22) ? 2 : r.GetInt32(22),
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

    /// <param name="UnconvertedRows">這個月有幾筆的幣別完全沒有匯率，所以沒算進上面那些金額（不是 0，是不知道）。</param>
    public sealed record Kpi(long IncomeMinor, long ExpenseMinor, long NetMinor, int Uncategorized, long UncategorizedMinor,
                             int UnconvertedRows = 0);

    /// <summary>
    /// 本月收支。加總把 transfer 排除：繳款列若算成支出會_double count_（見 db-design）。
    /// <para>錢一律以基準幣表達 —— 跟 <see cref="NetWorth"/>、<see cref="CategoryPivot"/> 同一把尺：
    /// 先照幣別加總、乘匯率只准經 <see cref="Money.Convert"/>（SQL 裡乘 REAL 會飄）；
    /// 完全沒有匯率的幣別照淨值的規矩<b>不計入金額</b>、只回報筆數，不准假設 1:1。</para>
    /// <para>「待分類」的筆數跟清單一樣含沒有消費日的列（否則清單躺著紅色 ? 而概況說 0 筆），
    /// 但金額只算折得出來的那幾筆。</para>
    /// </summary>
    public Kpi Totals(string? from, string? to, long? accountId)
    {
        // 金額只加有日期、落在區間內的列；但「待分類」要跟清單看到的一樣（清單刻意保留無日期的列），
        // 否則會出現「待分類 0 筆」而下面躺著幾列紅色 ? 等補日期。
        const string range = "(($from IS NULL OR e.txn_date >= $from) AND ($to IS NULL OR e.txn_date <= $to))";
        var sql = $"""
            SELECT e.currency,
                   SUM(CASE WHEN {range} AND e.kind = 'income' THEN e.amount_minor ELSE 0 END),
                   SUM(CASE WHEN {range} AND e.kind = 'expense' THEN e.amount_minor ELSE 0 END),
                   SUM(CASE WHEN {range} AND e.kind = 'adjustment' THEN e.amount_minor ELSE 0 END),
                   SUM(CASE WHEN {range} AND e.kind = 'refund' THEN e.amount_minor ELSE 0 END),
                   SUM(CASE WHEN e.category_id IS NULL AND e.kind NOT IN ('transfer','refund') THEN 1 ELSE 0 END),
                   SUM(CASE WHEN e.category_id IS NULL AND e.kind NOT IN ('transfer','refund') THEN e.amount_minor ELSE 0 END),
                   SUM(CASE WHEN {range} THEN 1 ELSE 0 END)
            FROM entry e
            WHERE {Live} AND e.kind <> 'transfer'
              AND (e.txn_date IS NULL OR {range})
              AND ($acct IS NULL OR e.account_id = $acct)
            GROUP BY e.currency;
            """;
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$from", (object?)from ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$to", (object?)to ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$acct", (object?)accountId ?? DBNull.Value);

        var baseCcy = BaseCurrency;
        var rates = RatesTo(baseCcy);
        long income = 0, expense = 0, adjust = 0, refund = 0, uncatMinor = 0;
        int uncat = 0, unconverted = 0;
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var ccy = Db.NormalizeCurrencyCode(Str(r, 0));
                decimal factor = ccy.Equals(baseCcy, StringComparison.Ordinal) ? 1m
                               : rates.TryGetValue(ccy, out var rate) ? rate : -1m;
                // 沒匯率：金額不計（宁可不計也不要 1:1），但筆數要報出來，否則人以為這個月真的沒花。
                if (factor < 0) { unconverted += (int)Lang(r, 7); uncat += (int)Lang(r, 5); continue; }
                income += Money.Convert(Lang(r, 1), factor);
                expense += Money.Convert(Lang(r, 2), factor);
                adjust += Money.Convert(Lang(r, 3), factor);
                refund += Money.Convert(Lang(r, 4), factor);
                uncat += (int)Lang(r, 5);
                uncatMinor += Money.Convert(Lang(r, 6), factor);
            }
        }
        // 退款是負的 refund（負負得正 = 錢進來），跟 CategoryPivot 的總計同一個算法，不靠分類標記。
        return new Kpi(income, expense, income + adjust - expense - refund, uncat, uncatMinor, unconverted);
    }

    public sealed record AccountBalance(long Id, string Name, string Type, string Currency, long BalanceMinor,
                                        bool CountedInNet, long ConvertedMinor, decimal? Rate, int ForeignEntries,
                                        int Scale = 2, string BaseCcy = "TWD", int BaseScale = 0)
    {
        /// <summary>總表給人看的文字。口徑留在 Data 層，UI 不准自己拼。</summary>
        public string TypeLabel => LedgerStore.TypeLabel(Type);
        public string BalanceText => Money.Format(BalanceMinor, Scale);          // 帳戶幣別的小數位
        public string ConvertedText => CountedInNet ? Money.Format(ConvertedMinor, BaseScale) : "—";   // 基準幣的小數位
        public string CountedText => ForeignEntries > 0
            ? (CountedInNet ? "✓" : Loc.T("Store_NotConvertedShort")) + Loc.F("Store_BalanceForeign", ForeignEntries)
            : CountedInNet ? (Currency == BaseCcy ? "✓" : $"✓（{Currency} → {BaseCcy} × {Rate:0.####}）")
                           : Loc.F("Store_Unconverted", Currency, BaseCcy);
        /// <summary>首頁淨值表的分組：銀行 → 信用卡 → 其它。</summary>
        public int GroupOrder => Type switch { "bank" => 0, "credit_card" => 1, _ => 2 };
        public string Group => Type switch { "bank" => Loc.T("Store_Group_Bank"), "credit_card" => Loc.T("Store_Group_Card"), _ => Loc.T("Store_Group_Other") };
    }
    /// <summary>淨值報告。所有金額都以 <paramref name="BaseCurrency"/> 表達；
    /// <paramref name="UnconvertedAccounts"/> 大於 0 代表這個數字不完整（寧可不計也不瞎算 1:1）。</summary>
    public sealed record NetWorthReport(long AssetsMinor, long LiabilitiesMinor, long NetMinor,
                                        int UnconvertedAccounts, IReadOnlyList<AccountBalance> Accounts,
                                        string BaseCurrency = "TWD")
    {
        /// <summary>還缺哪些幣對（給「幣別…」對話框跟警示列用：沒這些就湊不出淨值）。</summary>
        public IReadOnlyList<string> MissingPairs =>
            Accounts.Where(a => !a.CountedInNet).Select(a => $"{a.Currency} → {BaseCurrency}").Distinct().ToList();
    };

    /// <summary>
    /// 餘額一律当场由交易推（derived）：期初 + 收入 −（支出/繳款/退款）+ 轉入。
    /// 餘額快照（account_balance，含人工盤點）只拿來當校驗，對不上就在警示列講，不覆蓋這個數字。
    /// foreign_rows = 幣別跟帳戶不一樣的列有幾筆（王道那類：台幣帳戶裡混了美金列），
    /// 那些金額是照帳戶幣別算進餘額的，所以要在總表上讲明白，别让人当准数。
    /// <c>{f}</c> 是給 entry 的額外條件：全部交易填 <c>1=1</c>，「只算到某一天」填日期條件。
    /// 餘額校驗要用後者 —— 單子上印的是「那天結算的數」（兆豐 10 月的單印 115/09 月底餘額），
    /// 拿之後的交易一起加會讓期初快照天天誤報。口徑只能有一個，所以兩邊走同一支。
    /// </summary>
    const string DerivedBalancesCore = """
        SELECT a.id, a.name, a.type, a.currency, a.is_archived, a.opening_balance_minor, a.opening_date,
               a.opening_balance_minor + COALESCE(s.delta, 0) + COALESCE(t.in_minor, 0) AS bal,
               COALESCE(f.n, 0) AS foreign_rows, COALESCE(cc.scale, 2) AS scale
        FROM account a
        LEFT JOIN currency cc ON cc.code = a.currency
        LEFT JOIN (
            SELECT account_id, SUM(CASE WHEN kind IN ('income','adjustment') THEN amount_minor ELSE -amount_minor END) AS delta
            FROM entry WHERE deleted_at IS NULL AND status IN ('cleared','imported') AND {f}
            GROUP BY account_id
        ) s ON s.account_id = a.id
        LEFT JOIN (
            SELECT to_account_id AS account_id, SUM(amount_minor) AS in_minor
            FROM entry WHERE kind = 'transfer' AND deleted_at IS NULL AND status IN ('cleared','imported') AND {f}
            GROUP BY to_account_id
        ) t ON t.account_id = a.id
        LEFT JOIN (
            SELECT e.account_id, COUNT(*) AS n
            FROM entry e JOIN account a2 ON a2.id = e.account_id
            WHERE e.deleted_at IS NULL AND e.status IN ('cleared','imported') AND e.currency <> a2.currency
            GROUP BY e.account_id
        ) f ON f.account_id = a.id
        """;

    /// <summary>單子上沒日期的列（txn_date NULL）当成「早就在帳上」，不然會從快照裡漏算。</summary>
    const string Undated = "1970-01-01";

    /// <summary>derived 算式加上 entry 的額外條件（口徑只准這一支在拼）。</summary>
    static string DerivedCore(string entryFilter) => DerivedBalancesCore.Replace("{f}", entryFilter);

    static readonly string DerivedBalances = $"""
        SELECT id, name, type, currency, bal, foreign_rows, scale
        FROM ({DerivedCore("1=1")}) WHERE is_archived = 0 ORDER BY type, name
        """;

    /// <summary>基準幣（報表與匯率的「對照幣」）。存在 settings，改它不用清匯率。</summary>
    public string BaseCurrency => Db.BaseCurrency(_db);

    /// <summary>抓匯率要走的 proxy（例 http://10.191.190.5:3128）；空白 = 直連。公司網不填就永遠連不出去。</summary>
    public string RateProxy
    {
        get => Db.Scalar(_db, "SELECT value FROM settings WHERE key='rate_proxy_url';") as string ?? "";
        set => Db.Exec(_db, "INSERT OR REPLACE INTO settings(key, value) VALUES('rate_proxy_url', $v);", ("$v", (value ?? "").Trim()));
    }

    /// <summary>換基準幣。代碼要在 <c>currency</c> 字典裡，否則報表會出現查不到的幣別。</summary>
    public void SetBaseCurrency(string code)
    {
        var ccy = Db.NormalizeCurrencyCode(code);
        if (Db.Scalar(_db, "SELECT code FROM currency WHERE code=$c;", ("$c", ccy)) is null)
            Db.Exec(_db, "INSERT INTO currency(code, name, scale) VALUES($c, $c, 2);", ("$c", ccy));
        Db.Exec(_db, "INSERT OR REPLACE INTO settings(key, value) VALUES('base_currency', $c);", ("$c", ccy));
        Touch();
    }

    /// <summary>幣別字典（含顯示小數位與匯入用的別名）。</summary>
    public sealed record CurrencyRow(string Code, string Name, int Scale, string? Aliases)
    {
        public string Display => $"{Code}　{Name}" + (string.IsNullOrEmpty(Aliases) ? "" : $"　（{Aliases}）");
    }

    public List<CurrencyRow> CurrencyList()
    {
        var list = new List<CurrencyRow>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT code, name, scale, aliases FROM currency ORDER BY code;";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new CurrencyRow(r.GetString(0), r.GetString(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetString(3)));
        return list;
    }

    /// <summary>新增／改幣別字典。小數位只影響顯示，別名是匯入時對映用的原文（「美金|美元」）。</summary>
    public void SaveCurrency(string code, string name, int scale, string? aliases, string? oldCode = null)
    {
        code = Db.NormalizeCurrencyCode(code);
        if (code.Length != 3 || !code.All(c => c >= 'A' && c <= 'Z')) throw new ArgumentException(Loc.T("Store_BadCurrencyCode"), nameof(code));
        if (scale is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(scale), Loc.T("Store_BadScale"));
        name = string.IsNullOrWhiteSpace(name) ? code : name.Trim();
        aliases = string.IsNullOrWhiteSpace(aliases) ? null : aliases.Trim();

        var exists = Db.Scalar(_db, "SELECT code FROM currency WHERE code=$c;", ("$c", code)) is not null;
        if (oldCode is null)
        {
            Db.Exec(_db, """
                INSERT INTO currency(code, name, scale, aliases) VALUES($c, $n, $s, $a)
                ON CONFLICT(code) DO UPDATE SET name=excluded.name, scale=excluded.scale, aliases=excluded.aliases;
                """, ("$c", code), ("$n", name), ("$s", scale), ("$a", (object?)aliases ?? DBNull.Value));
        }
        else
        {
            oldCode = Db.NormalizeCurrencyCode(oldCode);
            if (!exists) Db.Exec(_db, "INSERT INTO currency(code, name, scale, aliases) VALUES($c, $n, $s, $a);",
                                ("$c", code), ("$n", name), ("$s", scale), ("$a", (object?)aliases ?? DBNull.Value));
            else Db.Exec(_db, "UPDATE currency SET name=$n, scale=$s, aliases=$a WHERE code=$c;",
                         ("$n", name), ("$s", scale), ("$a", (object?)aliases ?? DBNull.Value), ("$c", code));
            if (oldCode != code) Db.Exec(_db, "DELETE FROM currency WHERE code=$o;", ("$o", oldCode));
        }
        Touch();
    }

    /// <summary>帳戶裡出現過、但還不在字典裡的幣別（匯入時兵分兩路：一個進 entry、一個自己建）。</summary>
    public List<string> CurrenciesWithoutDictEntry()
        => Db.Query(_db, "SELECT DISTINCT currency FROM account UNION SELECT DISTINCT currency FROM entry")
             .Select(o => Db.NormalizeCurrencyCode((string)o!))
             .Where(c => Db.Scalar(_db, "SELECT code FROM currency WHERE code=$c;", ("$c", c)) is null)
             .OrderBy(c => c, StringComparer.Ordinal).ToList();

    /// <summary>顯示要用的小數位（字典沒這個幣就當 2 位）。</summary>
    public int ScaleOf(string currency)
        => Db.Scalar(_db, "SELECT scale FROM currency WHERE code=$c;", ("$c", Db.NormalizeCurrencyCode(currency))) is long s ? (int)s : 2;

    /// <summary>每種幣 → 基準幣的最新匯率（1 from = ? 基準幣）。人工填的優先於抓來的。</summary>
    public Dictionary<string, decimal> Rates() => RatesTo(BaseCurrency);

    /// <summary>某幣對的最新匯率。同一天的 manual 優先於 yahoo（抓的蓋不掉人打的）。</summary>
    public Dictionary<string, decimal> RatesTo(string toCcy)
    {
        var map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT from_ccy, rate FROM (
                SELECT from_ccy, rate, ROW_NUMBER() OVER (
                    PARTITION BY from_ccy ORDER BY rate_date DESC, CASE WHEN source='manual' THEN 0 ELSE 1 END) rn
                FROM exchange_rate WHERE to_ccy=$to) WHERE rn = 1;
            """;
        cmd.Parameters.AddWithValue("$to", Db.NormalizeCurrencyCode(toCcy));
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetString(0)] = Convert.ToDecimal(r.GetValue(1));
        return map;
    }

    public sealed record RateRow(string From, string To, decimal Rate, string RateDate, string Source, string? FetchedAt)
    {
        /// <summary>超過 <see cref="LedgerStore.StaleAfterDays"/> 沒更新就要標出來，否則人不知道這數字是多久以前的。</summary>
        public bool IsStale => DateOnly.TryParseExact(RateDate, "yyyy-MM-dd", out var d)
                            && DateOnly.FromDateTime(DateTime.Today).DayNumber - d.DayNumber > StaleAfterDays;
        public string StaleText => IsStale ? Loc.F("Store_Stale", StaleAfterDays) : "";
        public const int StaleAfterDays = 3;
    }

    /// <summary>基準幣那一側的全部匯率（給「幣別…」對話框的匯率表）。</summary>
    public List<RateRow> RateRows() => RateRowsTo(BaseCurrency);

    public List<RateRow> RateRowsTo(string toCcy)
    {
        var list = new List<RateRow>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT from_ccy, to_ccy, rate, rate_date, source, fetched_at FROM exchange_rate
            WHERE to_ccy=$t ORDER BY from_ccy, rate_date DESC;
            """;
        cmd.Parameters.AddWithValue("$t", Db.NormalizeCurrencyCode(toCcy));
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new RateRow(r.GetString(0), r.GetString(1), Convert.ToDecimal(r.GetValue(2)),
                                 r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        return list;
    }

    /// <summary>
    /// 寫一筆「1 from = rate to」的匯率。<paramref name="toCcy"/> 省略就是基準幣。
    /// 留空 rate = 故意不折算（那個帳戶就不進淨值，比瞎算 1:1 好）。
    /// </summary>
    public void SetRate(string fromCcy, decimal? rate, string? toCcy = null, string source = "manual", string? rateDate = null)
    {
        var from = Db.NormalizeCurrencyCode(fromCcy);
        var to = Db.NormalizeCurrencyCode(toCcy ?? BaseCurrency);
        if (from == to) return;                                  // 同一種幣沒有匯率這回事
        if (rate is null) Db.Exec(_db, "DELETE FROM exchange_rate WHERE from_ccy=$f AND to_ccy=$t;", ("$f", from), ("$t", to));
        else Db.Exec(_db, """
            INSERT INTO exchange_rate(from_ccy, to_ccy, rate_date, rate, source, fetched_at) VALUES($f, $t, $d, $r, $s, $at)
            ON CONFLICT(from_ccy, to_ccy, rate_date, source) DO UPDATE SET rate=excluded.rate, fetched_at=excluded.fetched_at;
            """, ("$f", from), ("$t", to), ("$d", rateDate ?? Db.Today()), ("$r", (double)rate.Value),
            ("$s", source), ("$at", source == "manual" ? (object)DBNull.Value : Db.Now()));
        Touch();
    }

    /// <summary>帳戶裡出現過、但还不是基準幣的幣別 —— 要抓匯率就是抓這幾個對基準幣的價。</summary>
    public List<string> NeededCurrencies()
        => Db.Query(_db, "SELECT DISTINCT currency FROM account WHERE is_archived=0;")
             .Select(o => Db.NormalizeCurrencyCode((string)o!))
             .Where(c => c != BaseCurrency).OrderBy(c => c, StringComparer.Ordinal).ToList();

    /// <summary>
    /// 淨值。以基準幣表達：基準幣帳戶直接計，其他幣別有匯率才計，沒匯率寧可不計（不瞎算 1:1），
    /// 只用「N 個帳戶未折算」+ <see cref="NetWorthReport.MissingPairs"/> 讓人知道這個數字不完整。
    /// </summary>
    public NetWorthReport NetWorth()
    {
        var list = new List<AccountBalance>();
        var baseCcy = BaseCurrency;
        var baseScale = ScaleOf(baseCcy);
        var rates = RatesTo(baseCcy);
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
                decimal? rate = currency.Equals(baseCcy, StringComparison.OrdinalIgnoreCase) ? 1m
                              : rates.TryGetValue(currency, out var rv) ? rv : null;
                bool counted = rate is not null;                       // 沒匯率寧可不計，不瞎算 1:1
                var converted = counted ? Money.Convert(bal, rate!.Value) : 0;
                list.Add(new AccountBalance(r.GetInt64(0), r.GetString(1), r.GetString(2), currency, bal,
                                            counted, converted, rate, r.GetInt32(5), r.GetInt32(6), baseCcy, baseScale));
                if (!counted) { unconverted++; continue; }
                if (converted >= 0) assets += converted; else liabilities += converted;
            }
        }
        return new NetWorthReport(assets, liabilities, assets + liabilities, unconverted, list, baseCcy);
    }

    /// <summary>
    /// 把「幣別跟帳戶不一樣」的列搬進獨立的帳戶（舊資料：王道的美金列直接倒進台幣帳戶）。
    /// 不搬的話那些金額是照帳戶幣別算進淨值的，⚠ 只是標起來、數字仍然错。
    /// </summary>
    public int SplitForeignCurrencyEntries()
    {
        int moved = 0;
        var rows = new List<(long AccountId, string Currency, long Id, string Name, string Type, string? Issuer, string? Mask)>();
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT e.account_id, e.currency, e.id, a.name, a.type, a.issuer, a.mask
                FROM entry e JOIN account a ON a.id = e.account_id
                WHERE e.deleted_at IS NULL AND e.currency <> a.currency;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add((r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4),
                          r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6)));
        }

        // 要嘛全部搬完，要嘛一個都不動：半途丢例外會留下一堆新建的沒錢帳戶 + 搬了一半的列。
        using var tx = _db.BeginTransaction();
        var target = new Dictionary<(long, string), long>();
        foreach (var (accountId, currency, entryId, name, type, issuer, mask) in rows)
        {
            if (!target.TryGetValue((accountId, currency), out var to))
            {
                var ccy = Db.NormalizeCurrencyCode(currency);
                // 「兆豐 1234 USD」這種帳戶常常已經存在（匯入本來就照一帳戶一幣別建）。
                // 以前這裡直接插同名 → 撞 account.name 的 UNIQUE，整個按鈕炸掉（一筆都沒搬到）。
                // 名字是我们要的那個就沿用；被别人占了就加號碼（跟 StatementImporter.CreateAccount 同一套路）。
                var baseName = $"{name} {ccy}";
                to = Db.Scalar(_db, "SELECT id FROM account WHERE name=$n AND currency=$c AND is_archived = 0;", ("$n", baseName), ("$c", ccy)) is long existing
                    ? existing
                    : Db.InsertId(_db, """
                        INSERT INTO account(name, type, currency, issuer, mask, created_at)
                        VALUES($n, $t, $c, $i, $m, $at);
                        """, ("$n", FreeName(baseName)), ("$t", type), ("$c", ccy),
                        ("$i", (object?)issuer ?? DBNull.Value), ("$m", (object?)mask ?? DBNull.Value), ("$at", Db.Now()));
                target[(accountId, currency)] = to;
            }
            Db.Exec(_db, "UPDATE entry SET account_id=$to, updated_at=$at WHERE id=$id;",
                    ("$to", to), ("$at", Db.Now()), ("$id", entryId));
            moved++;
        }
        tx.Commit();
        if (moved > 0) Touch();
        return moved;
    }

    public sealed record MonthPoint(DateOnly Month, long NetMinor);

    /// <summary>
    /// 每月月底的總資產（基準幣），首頁折線圖用。口徑跟 <see cref="NetWorth"/> 一樣：
    /// 只算未歸檔帳戶、沒匯率的外幣不計。shortcut: 全部用「現在」的匯率，不存歷史匯率（要準得先有 rates 歷史）。
    /// </summary>
    public List<MonthPoint> MonthlyNetWorth(DateOnly? through = null)
    {
        var endMonth = through ?? DateOnly.FromDateTime(DateTime.Today);
        endMonth = new DateOnly(endMonth.Year, endMonth.Month, 1);

        var baseCcy = BaseCurrency;
        var rates = RatesTo(baseCcy);
        var accounts = new Dictionary<long, (long BalanceMinor, decimal Rate)>();
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT id, currency, opening_balance_minor FROM account WHERE is_archived = 0;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var currency = r.GetString(1);
                decimal? rate = currency.Equals(baseCcy, StringComparison.OrdinalIgnoreCase) ? 1m
                              : rates.TryGetValue(currency, out var rv) ? rv : null;
                if (rate is not null) accounts[r.GetInt64(0)] = (r.GetInt64(2), rate.Value);
            }
        }
        if (accounts.Count == 0) return [new MonthPoint(endMonth, 0)];

        // 逐月的餘額變動。轉帳從轉出帳戶扣、加到轉入帳戶（淨值不變，但兩個帳戶的餘額要對）。
        var moves = new List<(string Month, long AccountId, long Delta)>();
        using (var cmd = _db.CreateCommand())
        {
            // 沒有消費日的列也要算：淨值那一側把它当成「早就在帳上」（見 Undated），圖必須同一個口徑，
            // 否则同一畫面左邊淨值與折線最後一點會差那些列（實測 −5,935 vs −4,290）。
            cmd.CommandText = """
                SELECT account_id, to_account_id, kind, amount_minor, substr(txn_date, 1, 7)
                FROM entry
                WHERE deleted_at IS NULL AND status IN ('cleared','imported');
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var kind = r.GetString(2);
                var amount = r.GetInt64(3);
                if (accounts.ContainsKey(r.GetInt64(0)))
                    Add(Str(r, 4), r.GetInt64(0), kind is "income" or "adjustment" ? amount : -amount);
                if (kind == "transfer" && !r.IsDBNull(1) && accounts.ContainsKey(r.GetInt64(1)))
                    Add(Str(r, 4), r.GetInt64(1), amount);
            }
        }

        // 沒有消費日的列直接進期初：它們在帳上的時間不明，当成第一點之前就在（跟淨值同一個假設）。
        void Add(string? month, long accountId, long delta)
        {
            if (month is null)
            {
                var (bal, rate) = accounts[accountId];
                accounts[accountId] = (bal + delta, rate);
                return;
            }
            moves.Add((month, accountId, delta));
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
    /// <param name="BillId">有值 = 這是一張信用卡帳單（不是 entry），界面上才能按「已繳」。</param>
    /// <param name="AmountText">給人看的那個數：小數位照該筆自己的 <c>currency.scale</c>，非基準幣還把幣別寫在後面
    /// （這一欄混著台幣與外幣，不標就會被當成同一種錢加）。</param>
    public sealed record UpcomingItem(string Date, string Label, decimal Amount, long? BillId = null, string AmountText = "")
    { public bool IsBill => BillId is not null; }

    public List<UpcomingItem> Upcoming()
    {
        var list = new List<UpcomingItem>();
        var baseCcy = BaseCurrency;
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT due, label, amount_minor, bill_id, ccy, scale FROM (
                SELECT e.txn_date AS due,
                       COALESCE(e.merchant, $unnamed) || '（' || a.name || '）' AS label,
                       e.amount_minor AS amount_minor, NULL AS bill_id, e.currency AS ccy, ifnull(ec.scale, 2) AS scale
                FROM entry e JOIN account a ON a.id = e.account_id
                LEFT JOIN currency ec ON ec.code = e.currency
                WHERE e.deleted_at IS NULL AND e.status IN ('cleared','imported','pending')
                  AND e.txn_date > $today
                UNION ALL
                SELECT b.due_date, $cardBill || ' ' || a.name, b.total_due_minor, b.id,
                       a.currency, ifnull(ak.scale, 2)
                FROM credit_card_bill b JOIN card k ON k.id = b.card_id JOIN account a ON a.id = k.account_id
                LEFT JOIN currency ak ON ak.code = a.currency
                WHERE b.paid_at IS NULL AND b.due_date >= $today AND b.total_due_minor IS NOT NULL
                  AND a.is_archived = 0
            ) ORDER BY due LIMIT 50;
            """;
        cmd.Parameters.AddWithValue("$today", Db.Today());
        cmd.Parameters.AddWithValue("$unnamed", Loc.T("Store_Unnamed"));
        cmd.Parameters.AddWithValue("$cardBill", Loc.T("Home_CardBillLabel"));   // 「信用卡帳單」是介面字，不能燒在 SQL 裡
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var ccy = Db.NormalizeCurrencyCode(r.GetString(4));
            var minor = r.GetInt64(2);
            list.Add(new UpcomingItem(r.GetString(0), r.GetString(1), Money.FromMinor(minor), NullableLong(r, 3),
                                      Money.Format(minor, r.GetInt32(5)) + (ccy == baseCcy ? "" : $" {ccy}")));
        }
        return list;
    }

    /// <summary>
    /// 標一張信用卡帳單為已繳（預設繳清）。「卡未繳」與「未來帳單」都只看 <c>paid_at</c>，
    /// 沒有這個動作它們就只增不減 —— 繳完帳也不會消失。
    /// </summary>
    public void MarkBillPaid(long billId, bool paid = true)
        => Write(paid ? "UPDATE credit_card_bill SET paid_at=$at, paid_minor=total_due_minor WHERE id=$id;"
                      : "UPDATE credit_card_bill SET paid_at=NULL, paid_minor=NULL WHERE id=$id;",
                 ("$at", Db.Now()), ("$id", billId));

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
    /// 快照有兩種來源：存款單解析出來的（statement）與人工盤點（manual）—— 同一天有兩者時以盤點為準（人當場數的比單子新）。
    ///
    /// 比法分三種（拿全部快照去跟「期初 ＋ 全部交易」比會天天誤報：匯入時自動建的帳戶根本不知道開戶以來的歷史）：
    /// 1. **同一帳戶相鄰兩個快照 → 比區間差**：這段區間的交易累加 ＝ 兩個餘額的差。這是唯一不需假設期初的比法，
    ///    也是抓漏解析的主力（區間內少讀一筆就紅）；兆豐那份就是靠它（單上印 115/09 月底餘額 + 115/10 帳戶總覽）。
    /// 2. **第一個快照**：只有在帳戶有明確起點時才比（人填過期初，或本来就是人工盤點在說「这天就是这么多」）。
    ///    沒起點的（單子印的餘額、帳戶是匯入自動建的）跳過 —— 假設期初 0 只會造成誤報。
    /// 3. 跳幣別的快照（快照幣別 ≠ 帳戶幣別）不進校驗。
    ///
    /// 區間要同一份單子才比（`account_balance.document_id`，0010）：跨單子的兩個快照中間可能根本沒單子
    /// （兆豐 4 月的單印 03/04 月底、10 月的單印 09 月底與 10/31，中間 5–8 月沒人匯過），那種區間报紅是假的。
    /// 沒有 document_id 的（人工盤點、或 0010 之前的舊快照）只能跟同為 NULL 的配對。
    ///
    /// 只算到 as_of 當天（含）：單子上印的是那天結算的數，把之後的交易也算進來會讓期初快照天天誤報。
    /// 回報的 `AsOf` 對區間比法會是「2026-04-30 → 2026-09-30」這種窗口（要讓人知道去翻哪一期單子）。
    /// <c>Scale</c> = 那個帳戶的顯示小數位：差額要跟餘額同一個單位講（美金帳戶的 0.71 不能被顯示成 1）。
    /// </summary>
    public List<(string Account, long DiffMinor, string Source, string AsOf, int Scale)> BalanceMismatches()
    {
        var list = new List<(string, long, string, string, int)>();
        var snapshots = new List<(long Id, string Name, long Balance, string Source, string AsOf, int Scale, bool Anchored, long Doc)>();
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT ab.account_id, v.name, ab.balance_minor, ab.source, ab.as_of, v.scale,
                       (v.opening_date IS NOT NULL OR v.opening_balance_minor <> 0) AS anchored, ab.grp
                FROM (
                    SELECT account_id, as_of, currency, balance_minor, source, ifnull(document_id, 0) AS grp FROM (
                        SELECT account_id, as_of, currency, balance_minor, source, document_id,
                               ROW_NUMBER() OVER (PARTITION BY account_id, as_of
                                                  ORDER BY (source = 'manual') DESC) rn
                        FROM account_balance) WHERE rn = 1
                ) ab
                JOIN ({DerivedCore("1=1")}) v ON v.id = ab.account_id AND v.is_archived = 0
                WHERE ab.currency = v.currency
                ORDER BY ab.account_id, ab.grp, ab.as_of;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                snapshots.Add((r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4),
                               r.IsDBNull(5) ? 2 : r.GetInt32(5), r.GetInt32(6) != 0, r.GetInt64(7)));
        }

        // 只算到 as_of 當天（含）；日期不明的当成早就在帳上，不然會從快照裡漏算。
        // shortcut: 每個快照跑一次查詢（N = 有快照的帳戶數 × 快照數），帳戶數上到幾百才會感覺；
        //           要改就把 as_of 当欄一起塞回一條 GROUP BY 的 SQL。
        var through = $"(CASE WHEN txn_date IS NOT NULL THEN txn_date ELSE ifnull(posting_date,'{Undated}') END) <= $asof";
        long DerivedThrough(long id, string asOf)
            => Db.Scalar(_db, $"SELECT bal FROM ({DerivedCore(through)}) WHERE id=$id;", ("$asof", asOf), ("$id", id)) is long v ? v : 0;

        for (var i = 0; i < snapshots.Count; i++)
        {
            var (id, name, balance, source, asOf, scale, anchored, doc) = snapshots[i];
            var first = i == 0 || snapshots[i - 1].Id != id || snapshots[i - 1].Doc != doc;
            long diff;
            string window;
            if (first)
            {
                if (!anchored && source != "manual") continue;                 // 沒有起點，比了只會误報
                diff = balance - DerivedThrough(id, asOf);
                window = asOf;
            }
            else
            {
                var prev = snapshots[i - 1];
                diff = (balance - prev.Balance) - (DerivedThrough(id, asOf) - DerivedThrough(id, prev.AsOf));
                window = $"{prev.AsOf} → {asOf}";
            }
            if (diff != 0) list.Add((name, diff, source, window, scale));
        }
        return list;
    }

    // ---------------------------------------------------------------- 依帳期彙總（信用卡）

    /// <summary>
    /// 一張列 = 一份信用卡帳單。只有跟卡帳戶**同幣別**的列才算進那一期：
    /// 把日圓與台幣加在同一列就是憑空多出一種錢，而且應繳會被算兩次（同一份單出現兩列，每列都扣一次應繳）。
    /// 幣別不符的列另外走下面那支查詢，以「（幣別不符）」列呈現。
    /// 金額一律<b>照單上的幣別</b>，因为这张表是用来跟纸本帳單对帐的；
    /// 只有合計列折成基準幣（<see cref="BillPeriodReport.BaseCurrency"/>）。
    /// </summary>
    public sealed record BillPeriod(long BillId, long AccountId, string AccountName, string Currency, int Scale,
                                    string? PeriodStart, string PeriodEnd, string? DueDate,
                                    int Entries, long SpendMinor, long? TotalDueMinor, long? DiffMinor,
                                    long TransferMinor, string? PaidAt, bool Converted, bool Foreign = false)
    {
        public bool Paid => PaidAt is not null;
        /// <summary>挂不上任何帳單的卡帳列（卡號拿不到時 <c>bill_id</c> 会是 NULL）：不静靜吃掉。</summary>
        public bool Unmapped => BillId == 0;
        /// <summary>消費合計與單上應繳對不上。應繳 NULL 時沒得比 → 不算對不上（也不給差額）。</summary>
        public bool Mismatch => DiffMinor is { } d && d != 0;
        /// <summary>這個幣別完全沒匯率 → 這一期的金額不進合計列，列上要標 ⚠。</summary>
        public bool NeedsRate => !Converted;
        public string PeriodText => Foreign ? Loc.T("Bill_ForeignPeriod")
                             : Unmapped ? Loc.T("Bill_NoPeriod")
                             : string.IsNullOrEmpty(PeriodStart) ? PeriodEnd : $"{PeriodStart} ~ {PeriodEnd}";
        public string SpendText => Money.Format(SpendMinor, Scale);
        public string TransferText => TransferMinor == 0 ? "" : Money.Format(TransferMinor, Scale);
        public string DueText => TotalDueMinor is { } v ? Money.Format(v, Scale) : "";
        /// <summary>0 顯示 «—»（對上了，不是少一筆）；NULL 留白（單上沒印應繳，沒得比）。</summary>
        public string DiffText => DiffMinor is not { } v ? "" : v == 0 ? "—" : Money.Format(v, Scale);
        public string PaidText => PaidAt is null ? "" : PaidAt[..10];
        /// <summary>⚠ 那一欄（只靠顏色看不夠）。四種不一樣的「不對」要分得開：
        /// 對不上（漏解析的候選）、缺匯率（比不了）、挂不上帳期（沒單可對）、幣別不符（不屬於那一期）。</summary>
        public string MarkText => Foreign ? "⚠ " + Loc.T("Bill_WarnForeign")
                             : Unmapped ? "⚠ " + Loc.T("Bill_WarnUnmapped")
                             : NeedsRate ? "⚠ " + Loc.T("Bill_WarnRate")
                             : Mismatch ? "⚠ " + Loc.T("Bill_WarnDiff") : "";
    }

    /// <param name="UnmappedEntries">卡帳但挂不上任何帳單的列數（要講、不能静靜吃掉）。</param>
    /// <param name="UnconvertedBills">因為完全沒匯率而沒算進合計列的期數。</param>
    public sealed record BillPeriodReport(IReadOnlyList<BillPeriod> Rows,
                                          long TotalSpendMinor, long TotalDueMinor, long TotalDiffMinor,
                                          int UnmappedEntries, int UnmappedBills,
                                          int UnconvertedBills, string BaseCurrency, int BaseScale)
    {
        public string SpendText => Money.Format(TotalSpendMinor, BaseScale);
        public string DueText => Money.Format(TotalDueMinor, BaseScale);
        public string DiffText => TotalDiffMinor == 0 ? "—" : Money.Format(TotalDiffMinor, BaseScale);
    }

    /// <summary>
    /// 依帳期彙總：拿「明細合計」跟「單上應繳」對帳，對不上的那幾期就是 parser 漏列的候選。
    /// <para>歸期靠 <c>entry.bill_id</b>（外鍵），<b>不用日期區間</b>：跨期消費與 <c>txn_date</c> 為 NULL 的列
    /// 會被日期區間漏掉。原料由匯入端寫（<c>bill_id</c>/<c>card_id</c>），这张表不建 rollup。</para>
    /// <para>消費合計 = <see cref="Live"/> 且 <c>kind ≠ 'transfer'</c> 的金額直接相加：pending/deleted 不計，
    /// 繳款（轉帳）不算消費（另進「繳款」欄，否則支出會 double count），退款在 DB 裡是負的所以直接加
    /// 就是淨消費。差額 = 消費合計 − 單上應繳；應繳 NULL → 差額也 NULL（不准當 0）。</para>
    /// <para>只有信用卡：存款單/繳費單沒有「帳期」這個概念，不進這張表。</para>
    /// </summary>
    /// <param name="periodsPerCard">每張卡只留最近 N 期（0 = 全部）。視窗函数照 <c>period_end</c> 排。</param>
    /// <param name="onlyMismatched">只留對不上的期（含缺匯率的）。</param>
    public BillPeriodReport BillPeriods(int periodsPerCard = 12, bool onlyMismatched = false)
    {
        var baseCcy = BaseCurrency;
        var baseScale = BaseCurrencyScale;
        var rates = RatesTo(baseCcy);
        var rows = new List<BillPeriod>();
        long totalSpend = 0, totalDue = 0, totalDiff = 0;
        int unconverted = 0;

        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT b.id, k.account_id, a.name, a.currency,
                       b.period_start, b.period_end, b.due_date, b.total_due_minor, b.paid_at,
                       ifnull(g.currency, a.currency),
                       COALESCE(g.spend, 0), COALESCE(g.transfers, 0), COALESCE(g.n, 0)
                FROM (SELECT id, card_id, period_start, period_end, due_date, total_due_minor, paid_at,
                             ROW_NUMBER() OVER (PARTITION BY card_id ORDER BY period_end DESC) rn
                      FROM credit_card_bill) b
                JOIN card k ON k.id = b.card_id
                JOIN account a ON a.id = k.account_id
                LEFT JOIN (SELECT e.bill_id, e.currency,
                                  SUM(CASE WHEN e.kind <> 'transfer' THEN e.amount_minor ELSE 0 END) AS spend,
                                  SUM(CASE WHEN e.kind = 'transfer'  THEN e.amount_minor ELSE 0 END) AS transfers,
                                  COUNT(CASE WHEN e.kind <> 'transfer' THEN 1 END) AS n
                           FROM entry e WHERE {Live} AND e.bill_id IS NOT NULL
                           GROUP BY e.bill_id, e.currency) g ON g.bill_id = b.id AND upper(g.currency) = upper(a.currency)
                WHERE a.is_archived = 0 AND ($n = 0 OR b.rn <= $n)
                ORDER BY b.period_end DESC, a.name, ifnull(g.currency, a.currency);
                """;
            cmd.Parameters.AddWithValue("$n", periodsPerCard <= 0 ? 0 : periodsPerCard);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var ccy = Db.NormalizeCurrencyCode(Str(r, 9)!);
                var scale = ScaleOf(ccy);
                var spend = Lang(r, 10);
                var transfers = Lang(r, 11);
                var due = r.IsDBNull(7) ? (long?)null : Lang(r, 7);
                var diff = due is null ? (long?)null : spend - due.Value;
                var converted = ccy.Equals(baseCcy, StringComparison.Ordinal) || rates.ContainsKey(ccy);
                var row = new BillPeriod(r.GetInt64(0), r.GetInt64(1), r.GetString(2), ccy, scale,
                                         Str(r, 4), Str(r, 5)!, Str(r, 6), (int)Lang(r, 12),
                                         spend, due, diff, transfers, Str(r, 8), converted);
                if (onlyMismatched && !row.Mismatch && !row.NeedsRate) continue;
                rows.Add(row);
                if (!converted) { unconverted++; continue; }                 // 没匯率就是不知道，不准假設 1:1
                var factor = ccy.Equals(baseCcy, StringComparison.Ordinal) ? 1m : rates[ccy];
                totalSpend += Money.Convert(spend, factor);
                totalDue += Money.Convert(due ?? 0, factor);
                totalDiff += Money.Convert(diff ?? 0, factor);
            }
        }

        // 没算進任何帳期的卡帳列：挂不到單（卡號被遮）、或币別跟帳單帳戶不符（舊庫混进去的、
        // 或「拆帳戶」把列搬走了但 bill_id 留著 —— 那種准算回原期會讓應繳重計）。footer 要報數。
        var unmapped = new List<BillPeriod>();
        int unmappedEntries = 0;
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT e.account_id, a.name, e.currency,
                       SUM(CASE WHEN e.kind <> 'transfer' THEN e.amount_minor ELSE 0 END) AS spend,
                       SUM(CASE WHEN e.kind = 'transfer'  THEN e.amount_minor ELSE 0 END) AS transfers,
                       COUNT(CASE WHEN e.kind <> 'transfer' THEN 1 END) AS n,
                       CASE WHEN e.bill_id IS NULL OR ba.currency IS NULL THEN 1 ELSE 0 END AS loose
                FROM entry e
                JOIN account a ON a.id = e.account_id
                LEFT JOIN credit_card_bill b ON b.id = e.bill_id
                LEFT JOIN card k ON k.id = b.card_id
                LEFT JOIN account ba ON ba.id = k.account_id
                WHERE {Live} AND a.type = 'credit_card' AND a.is_archived = 0
                  AND (e.bill_id IS NULL OR ba.currency IS NULL OR upper(e.currency) <> upper(ba.currency))
                GROUP BY e.account_id, e.currency, loose
                ORDER BY a.name, e.currency;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var ccy = Db.NormalizeCurrencyCode(Str(r, 2)!);
                var row = new BillPeriod(0, r.GetInt64(0), r.GetString(1), ccy, ScaleOf(ccy),
                                         null, "", null, (int)Lang(r, 5), Lang(r, 3), null, null, Lang(r, 4), null,
                                         ccy.Equals(baseCcy, StringComparison.Ordinal) || rates.ContainsKey(ccy),
                                         Foreign: Lang(r, 6) == 0);
                unmapped.Add(row);
                unmappedEntries += row.Entries;
                if (onlyMismatched) continue;
                rows.Add(row);                                                // 只加进列，不進合計（沒有單可對）
                if (!row.Converted) unconverted++;
            }
        }
        return new BillPeriodReport(rows, totalSpend, totalDue, totalDiff,
                                    unmappedEntries, unmapped.Count, unconverted, baseCcy, baseScale);
    }

    // ---------------------------------------------------------------- 下拉的資料

    public sealed record Option(long Id, string Name);

    List<(string Kind, bool Leaves, List<Option> Items)> _categoryCache = [];

    /// <summary>分類下拉。只給方向對的那組：expense 列選不到 income 分類（靠 CHECK 會炸，不如不給選）。</summary>
    public List<Option> Categories(string kind, bool leavesOnly = false)
    {
        var hit = _categoryCache.FirstOrDefault(c => c.Kind == kind && c.Leaves == leavesOnly);
        if (hit.Items is not null) return hit.Items;
        var items = new List<Option>();
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = """
                WITH RECURSIVE cp(id, path) AS (
                    SELECT id, CAST(name AS TEXT) FROM category WHERE parent_id IS NULL
                    UNION ALL
                    SELECT c.id, cp.path || '／' || c.name FROM category c JOIN cp ON c.parent_id = cp.id)
                SELECT c.id, cp.path
                FROM category c JOIN cp ON cp.id = c.id
                WHERE c.kind = $k ORDER BY c.sort_order, cp.path;
                """;
            cmd.Parameters.AddWithValue("$k", kind);
            using var r = cmd.ExecuteReader();
            while (r.Read()) items.Add(new Option(r.GetInt64(0), r.GetString(1)));
        }
        // 只留最底層：下拉裡「餐飲」下面一定有「餐飲／⋯」這種路徑，有子類的就不是 leaf。
        // shortcut: 靠路徑字串判，跟 CategoryRow.Indented 同一個假設（分類名不准含全形斜線），要改兩处。
        if (leavesOnly)
            items = items.Where(c => !items.Any(ch => ch.Name.StartsWith(c.Name + '／', StringComparison.Ordinal))).ToList();
        _categoryCache.Add((kind, leavesOnly, items));
        return items;
    }

    /// <summary>「新增分類」：同名（同父、同方向）就沿用既有的，不准憑空多一個一樣的。父分類必須是同方向的頂層分類（兩層上限）。</summary>
    public long AddCategory(string name, string kind, long? parentId = null)
    {
        name = name.Trim();
        if (parentId is { } pid && Db.Scalar(_db, "SELECT id FROM category WHERE id = $i AND kind = $k;", ("$i", pid), ("$k", kind)) is null)
            throw new ArgumentException(Loc.T("Store_BadParent"));
        var parent = (object?)parentId ?? DBNull.Value;
        var existing = Db.Scalar(_db, "SELECT id FROM category WHERE name=$n AND kind=$k AND COALESCE(parent_id, 0) = COALESCE($p, 0);", ("$n", name), ("$k", kind), ("$p", parent));
        if (existing is long id) return id;
        var newId = Db.InsertId(_db, "INSERT INTO category(name, kind, parent_id, is_system) VALUES($n, $k, $p, 0);", ("$n", name), ("$k", kind), ("$p", parent));
        _categoryCache = [];
        return newId;
    }

    public sealed record CategoryRow(long Id, string Name, string Kind, string Path, int Uses, bool IsSystem)
    {
        /// <summary>樹狀顯示：每深一層縮排兩個全形空白，子分類前加「└」。</summary>
        public string Indented
        {
            get
            {
                var depth = Path.Count(c => c == '／');
                return depth == 0 ? Name : new string('　', depth * 2) + "└ " + Name;
            }
        }
    }

    /// <summary>分類管理用：列出所有分類（含使用筆數），依方向與排序。</summary>
    public List<CategoryRow> AllCategories()
    {
        var list = new List<CategoryRow>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"""
            WITH RECURSIVE cp(id, path) AS (
                SELECT id, CAST(name AS TEXT) FROM category WHERE parent_id IS NULL
                UNION ALL
                SELECT c.id, cp.path || '／' || c.name FROM category c JOIN cp ON c.parent_id = cp.id)
            SELECT c.id, c.name, c.kind, cp.path,
                   (SELECT COUNT(*) FROM entry e WHERE e.category_id = c.id AND e.deleted_at IS NULL), c.is_system
            FROM category c JOIN cp ON cp.id = c.id
            ORDER BY c.kind, cp.path;
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new CategoryRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetInt32(5) == 1));
        return list;
    }

    /// <summary>改分類名稱（同方向不准重名）。已有交易的分類照樣改，交易跟著走。</summary>
    public void RenameCategory(long id, string name)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException(Loc.T("Store_NeedCategoryName"));
        var kind = Db.Scalar(_db, "SELECT kind FROM category WHERE id=$i;", ("$i", id)) as string
                   ?? throw new ArgumentException(Loc.T("Store_CategoryNotFound"));
        if (Db.Scalar(_db, "SELECT id FROM category WHERE name=$n AND kind=$k AND id <> $i;", ("$n", name), ("$k", kind), ("$i", id)) is long)
            throw new ArgumentException(Loc.F("Store_DuplicateCategory", name, kind == "income" ? Loc.T("Entry_Kind_Income") : Loc.T("Entry_Kind_Expense")));
        Write("UPDATE category SET name=$n WHERE id=$i;", ("$n", name), ("$i", id));
        _categoryCache = [];
    }

    /// <summary>未分類的商家與筆數（報表口徑，依筆數由多到少），給分類建議用。</summary>
    public List<(string Merchant, int Count)> UncategorizedMerchants(string? from = null, string? to = null, long? accountId = null)
    {
        var list = new List<(string, int)>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"""
            SELECT COALESCE(e.merchant, $unnamed), COUNT(*)
            FROM entry e
            WHERE {Live} AND e.category_id IS NULL AND e.kind NOT IN ('transfer','refund')
              AND ($acct IS NULL OR e.account_id = $acct)
              AND (e.txn_date IS NULL OR $from IS NULL OR e.txn_date >= $from) AND (e.txn_date IS NULL OR $to IS NULL OR e.txn_date <= $to)
            GROUP BY 1
            ORDER BY COUNT(*) DESC, 1;
            """;
        cmd.Parameters.AddWithValue("$from", (object?)from ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$to", (object?)to ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$acct", (object?)accountId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$unnamed", Loc.T("Store_Unnamed"));
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), (int)r.GetInt64(1)));
        return list;
    }

    /// <summary>帳戶名稱是 UNIQUE 的（含已歸檔的），所以要名字前先問一遍；被占了就加號碼。</summary>
    string FreeName(string baseName)
    {
        var name = baseName;
        for (int n = 2; Db.Scalar(_db, "SELECT id FROM account WHERE name=$n;", ("$n", name)) is not null; n++)
            name = $"{baseName} {n}";
        return name;
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

    /// <summary>帳戶型別：CHECK 認得的就這八個。UI 下拉直接用這個，不准自己加（加了存下去會被擋）。
    /// 刻意是 property 不是欄位：寫成 static readonly 會把標籤固定在第一次取用的語系上。</summary>
    public static (string Value, string Label)[] AccountTypes =>
    [
        ("cash", Loc.T("Store_Type_Cash")), ("bank", Loc.T("Store_Type_Bank")), ("wallet", Loc.T("Store_Type_Wallet")), ("credit_card", Loc.T("Store_Type_Card")),
        ("term_deposit", Loc.T("Store_Type_Deposit")), ("investment", Loc.T("Store_Type_Investment")), ("loan", Loc.T("Store_Type_Loan")), ("asset", Loc.T("Store_Type_Asset")),
    ];

    public static string TypeLabel(string type) => AccountTypes.FirstOrDefault(t => t.Value == type).Label ?? type;

    public List<string> Currencies()
        => Db.Query(_db, "SELECT code FROM currency ORDER BY code;").Select(o => (string)o!).ToList();

    /// <summary>帳戶管理表單的一筆。<see cref="BalanceMinor"/> 是交易推出來的，<see cref="ManualBalanceMinor"/> 是上次盤點的。</summary>
    public sealed record AccountEdit(long Id, string Name, string Type, string Currency, string? Issuer, string? Mask,
                                     long OpeningBalanceMinor, string? OpeningDate, bool IsArchived,
                                     long? CreditLimitMinor, long? AutopayAccountId, long BalanceMinor,
                                     long? ManualBalanceMinor, string? ManualBalanceDate, int Scale = 2,
                                     string BaseCcy = "TWD")
    {
        /// <summary>帳戶管理左側清單那一行。</summary>
        public string ListLabel => $"{Name}　{(Currency == BaseCcy ? "" : Currency + " ")}{Money.Format(BalanceMinor, Scale)}"
                                 + (IsArchived ? Loc.T("Store_ArchivedSuffix") : "");
    }

    static readonly string AccountsAllSql = $"""
        SELECT a.id, a.name, a.type, a.currency, a.issuer, a.mask, a.opening_balance_minor, a.opening_date,
               a.is_archived, k.credit_limit_minor, k.autopay_account_id, v.bal, mb.balance_minor, mb.as_of, v.scale
        FROM ({DerivedCore("1=1")}) v
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
        var baseCcy = BaseCurrency;
        using var cmd = _db.CreateCommand();
        cmd.CommandText = AccountsAllSql;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new AccountEdit(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), Str(r, 4), Str(r, 5),
                                     r.GetInt64(6), Str(r, 7), r.GetInt32(8) == 1, NullableLong(r, 9), NullableLong(r, 10),
                                     r.GetInt64(11), NullableLong(r, 12), Str(r, 13), r.IsDBNull(14) ? 2 : r.GetInt32(14), baseCcy));
        return list;
    }

    /// <summary>帳戶管理表单用：账户就那十幾個，直接从清单里找，不另拼一份 SQL。</summary>
    public AccountEdit? GetAccount(long id) => AccountsAll().FirstOrDefault(a => a.Id == id);

    /// <summary>分類 × 月 透視表的一格。金額留 long（排序要比數值），顯示由 UI 決定。</summary>
    public sealed record PivotCell(long CategoryId, string Month, long TotalMinor);

    /// <summary>分類樹的一列：Depth 0 = 頂層（收支那一層是 <c>kind</c>，不是節點），1 = 明細層。</summary>
    public sealed record PivotCategory(long Id, long? ParentId, string Kind, int Depth, string Name, string Path);

    /// <summary>報表原料：整棵分類樹（含沒資料的）＋有資料的格子＋換算狀況（折算了幾筆、沒匯率跳過幾筆）。</summary>
    /// <summary>每個月「收−支」的數（CategoryId 一律 0，只看 Month/TotalMinor）：給報表最下面那列總計。</summary>
    public sealed record PivotData(List<PivotCategory> Categories, List<PivotCell> Cells, List<PivotCell> NetCells,
                                   long SkippedForeignEntries, long ConvertedForeignEntries, string BaseCurrency);

    /// <summary>
    /// 分類 × 月 的原料（僅 <see cref="Live"/>）。小計不在 SQL 加（要就多一整段遞迴 UNION），
    /// 交給 <see cref="PivotTree.Build"/>，小計規則才能用測試鎖住。
    /// 非基準幣照「對基準幣现有的匯率」折進去（同一把尺：跟淨值用同一份 <see cref="RatesTo"/>，
    /// 同一天 manual 優先），完全沒匯率的才跳過並回報筆數 —— 不准假設 1:1。
    /// shortcut: 換算是照现有最新匯率，不是照該月当天的匯率；等匯率資料夠密（有歷史）再改成按月挑，
    ///           到時這裡要補一個「同一幣對不同月用不同rate」的測試。
    /// 沒有消費日的列不會進表（它沒有月可以放）。
    /// </summary>
    public PivotData CategoryPivot(string? from = null, string? to = null, long? accountId = null)
    {
        var filters = (from is not null ? " AND e.txn_date >= $from" : "")
                    + (to is not null ? " AND e.txn_date <= $to" : "")
                    + (accountId.HasValue ? " AND e.account_id = $acct" : "");
        var scope = $"({LedgerStore.Live}){filters} AND e.kind <> 'transfer' AND e.txn_date IS NOT NULL";
        var ps = new List<(string, object?)> { ("$from", (object?)from), ("$to", (object?)to) };
        if (accountId.HasValue) ps.Add(("$acct", accountId.Value));

        var categories = new List<PivotCategory>();
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = """
                WITH RECURSIVE cp(id, path, depth) AS (
                    SELECT id, CAST(name AS TEXT), 0 FROM category WHERE parent_id IS NULL
                    UNION ALL
                    SELECT c.id, cp.path || '／' || c.name, cp.depth + 1
                    FROM category c JOIN cp ON c.parent_id = cp.id)
                SELECT c.id, c.parent_id, c.kind, cp.depth, c.name, cp.path
                FROM category c JOIN cp ON cp.id = c.id
                ORDER BY c.kind, c.sort_order, cp.path;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                categories.Add(new PivotCategory(r.GetInt64(0), NullableLong(r, 1), r.GetString(2), r.GetInt32(3), r.GetString(4), r.GetString(5)));
        }

        var baseCcy = Db.BaseCurrency(_db);
        // 先照（分類, 月, 幣別）加總，外幣那一批才折算：乘匯率只准經 Money.Convert（SQL 裡乘 REAL 會飄），
        // 所以这一步在 C# 做，也才能用測試鎖住折算的數學。
        var groups = new List<(long Cid, string Month, string Ccy, long Minor, long Count, bool Inflow)>();
        using (var cmd = _db.CreateCommand())
        {
            // category_id IS NULL 的列歸到 id 0（未分類）：報表上就是一列「未分類」，不會憑空消失。
            cmd.CommandText = $"""
                SELECT COALESCE(e.category_id, 0), substr(e.txn_date, 1, 7), e.currency, SUM(e.amount_minor), COUNT(*),
                       CASE WHEN e.kind = 'income' THEN 1 ELSE 0 END
                FROM entry e WHERE {scope}
                GROUP BY 1, 2, 3, 6 ORDER BY 1, 2, 3;
                """;
            foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            using var r = cmd.ExecuteReader();
            while (r.Read()) groups.Add((r.GetInt64(0), r.GetString(1), Db.NormalizeCurrencyCode(r.GetString(2)),
                                          r.GetInt64(3), r.GetInt64(4), r.GetInt32(5) == 1));
        }

        var rates = RatesTo(baseCcy);
        var merged = new Dictionary<(long Cid, string Month), long>();
        var net = new Dictionary<string, long>();
        long skipped = 0, converted = 0;
        foreach (var g in groups)
        {
            var minor = g.Minor;
            if (!string.Equals(g.Ccy, baseCcy, StringComparison.Ordinal))
            {
                if (rates.TryGetValue(g.Ccy, out var rate)) minor = Money.Convert(minor, rate);
                else { skipped += g.Count; continue; }           // 沒匯率就寧可不計，也不要 1:1
                converted += g.Count;
            }
            merged[(g.Cid, g.Month)] = merged.GetValueOrDefault((g.Cid, g.Month)) + minor;
            // 收−支照交易 kind 判，不靠分類：退款是负的支出（負負得正，錢進來），分類標錯也不會把這個數算錯。
            net[g.Month] = net.GetValueOrDefault(g.Month) + (g.Inflow ? minor : -minor);
        }
        var cells = merged.Select(kv => new PivotCell(kv.Key.Cid, kv.Key.Month, kv.Value))
                          .OrderBy(c => c.CategoryId).ThenBy(c => c.Month, StringComparer.Ordinal).ToList();

        var netCells = net.Select(kv => new PivotCell(0, kv.Key, kv.Value))
                           .OrderBy(c => c.Month, StringComparer.Ordinal).ToList();
        return new PivotData(categories, cells, netCells, skipped, converted, baseCcy);
    }

    /// <summary>報表金額欄要顯示幾位小數（讀 <c>currency.scale</c>：美金 2、台幣 0）。加總一律用 minor，不用這個。</summary>
    public int BaseCurrencyScale => Db.Scalar(_db, "SELECT scale FROM currency WHERE code=$c;", ("$c", Db.BaseCurrency(_db))) is long s ? (int)s : 2;

    /// <summary>
    /// 單筆分類的明細交易（可用於 drill down 點擊）。
    /// </summary>
    public sealed record CatEntry(long Id, string Date, string Merchant, long AmountMinor, string CategoryName);

    /// <summary>交易推出來的餘額（已歸檔的也算，盘點跟對帳要看得到）。</summary>
    public long DerivedBalance(long accountId)
        => Db.Scalar(_db, $"SELECT bal FROM ({DerivedCore("1=1")}) WHERE id=$id;", ("$id", accountId)) is long v ? v : 0;

    /// <summary>
    /// 驗證帳戶欄位。
    /// 已有交易的帳戶不准改幣別/類型：餘額是按「帳戶幣別 × 匯率」折算的，台幣帳戶改成 USD
    /// 會讓既有交易被當成美金再乘一次匯率（淨值瞬間幾十倍），而且看不出是何時開始錯的。
    /// </summary>
    void ValidateAccount(AccountEdit e)
    {
        if (string.IsNullOrWhiteSpace(e.Name)) throw new ArgumentException(Loc.T("Store_NeedAccountName"));
        if (!AccountTypes.Any(t => t.Value == e.Type)) throw new ArgumentException(Loc.F("Store_UnknownType", e.Type));
        if (string.IsNullOrWhiteSpace(e.Currency)) throw new ArgumentException(Loc.T("Store_NeedCurrency"));
        if (e.OpeningDate is not null && !TryDate(e.OpeningDate, out _)) throw new ArgumentException(Loc.T("Store_BadOpeningDate"));
        if (e.AutopayAccountId is { } ap && (ap == e.Id || Db.Scalar(_db, "SELECT 1 FROM account WHERE id=$i;", ("$i", ap)) is null))
            throw new ArgumentException(Loc.T("Store_BadAutoPay"));

        if (e.Id <= 0) return;
        var rows = Db.Scalar(_db, "SELECT COUNT(*) FROM entry WHERE account_id=$id;", ("$id", e.Id)) is long n ? n : 0;
        if (rows == 0 || Db.Scalar(_db, "SELECT currency || '|' || type FROM account WHERE id=$id;", ("$id", e.Id)) is not string now) return;
        var parts = now.Split('|');
        if (!string.Equals(parts[0], e.Currency.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(Loc.F("Store_CurrencyLocked", rows));
        if (parts.Length > 1 && parts[1] != e.Type)
            throw new ArgumentException(Loc.F("Store_TypeLocked", rows));
    }

    /// <summary>同名先擋下来講人話，不讓 UNIQUE 的 SqliteException 冒到 UI。</summary>
    void EnsureNameFree(string name, long selfId)
    {
        if (Db.Scalar(_db, "SELECT id FROM account WHERE name=$n AND id <> $id;", ("$n", name), ("$id", selfId)) is long)
            throw new ArgumentException(Loc.F("Store_DuplicateAccount", name));
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
        if (e.Id <= 0) throw new ArgumentException(Loc.T("Store_NoAccountSelected"));
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
        if (!TryDate(asOf, out var d) || d is null) throw new ArgumentException(Loc.T("Store_BadCountDate"));
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

    /// <summary>
    /// 鐵律：entry 只能標最底層分類（下面沒有子分類的）。標在父類，報表上父列小計就不等於子列相加,
    /// 看起來像記錯帳。下拉已經只列最底層，這裡擋的是「分類快取變舊」那種漏網。
    /// </summary>
    void EnsureLeaf(long? categoryId)
    {
        if (categoryId is not { } id) return;
        if (Db.Scalar(_db, "SELECT EXISTS(SELECT 1 FROM category ch WHERE ch.parent_id = $c);", ("$c", id)) is long n and > 0)
            throw new InvalidOperationException(Loc.T("Store_NotLeafCategory"));
    }

    /// <summary>使用者動過的列要寫 <c>edited_at</c>：重匯時匯入器只准更新 source_raw，不然人工分類會被蓋掉。</summary>
    public void SetCategory(long id, long? categoryId)
    {
        EnsureLeaf(categoryId);
        Write("UPDATE entry SET category_id=$c, status=CASE WHEN $c IS NOT NULL AND status='imported' THEN 'cleared' ELSE status END, edited_at=$at, updated_at=$at WHERE id=$id;",
              ("$c", (object?)categoryId ?? DBNull.Value), ("$at", Db.Now()), ("$id", id));
    }

    /// <summary>已有分類但仍是待補（imported）的列改成核銷（cleared）；啟動時跑一次，冪等。</summary>
    public int PromoteCategorizedImports()
    {
        Db.Exec(_db, "UPDATE entry SET status='cleared' WHERE status='imported' AND category_id IS NOT NULL AND deleted_at IS NULL;");
        return Db.Scalar(_db, "SELECT changes();") is long n ? (int)n : 0;
    }

    /// <summary>
    /// 依商家批次補分類，只動尚未分類的列。<c>accountId</c> 要跟著建議視窗的篩選：
    /// 不帶它，看的是 A 帳戶的 3 筆、實際連 B 帳戶同商家一起改。
    /// </summary>
    public int SetCategoryForMerchant(string merchant, long categoryId, long? accountId = null)
    {
        EnsureLeaf(categoryId);
        var merchantKey = merchant == Loc.T("Store_Unnamed") ? null : merchant;
        Write($"""
            UPDATE entry SET category_id=$c, status=CASE WHEN status='imported' THEN 'cleared' ELSE status END, edited_at=$at, updated_at=$at
            WHERE deleted_at IS NULL AND status IN ('cleared','imported')
              AND category_id IS NULL AND kind NOT IN ('transfer','refund')
              AND ($acct IS NULL OR account_id = $acct)
              AND (merchant IS $m OR ($m IS NULL AND merchant IS NULL));
            """,
              ("$c", categoryId), ("$at", Db.Now()), ("$m", (object?)merchantKey ?? DBNull.Value),
              ("$acct", (object?)accountId ?? DBNull.Value));
        return Db.Scalar(_db, "SELECT changes();") is long n ? (int)n : 0;
    }

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
            throw new ArgumentException(Loc.F("Store_UnknownStatus", status));
        Write("UPDATE entry SET status=$s, edited_at=$at, updated_at=$at WHERE id=$id;",
              ("$s", status), ("$at", Db.Now()), ("$id", id));
    }

    /// <summary>轉帳兩側要同一種幣別：否則「轉 1,000」在兩個帳戶會變成不同數額的錢（要換匯得先記雨筆）。</summary>
    void EnsureSameCurrency(long accountId, long toAccountId)
    {
        if (Db.Scalar(_db, "SELECT currency FROM account WHERE id=$a;", ("$a", accountId)) is string a &&
            Db.Scalar(_db, "SELECT currency FROM account WHERE id=$t;", ("$t", toAccountId)) is string b &&
            Db.NormalizeCurrencyCode(a) != Db.NormalizeCurrencyCode(b))
            throw new ArgumentException(Loc.F("Store_TransferCurrency", a, b));
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
            throw new ArgumentException(Loc.F("Store_UnknownKind", e.Kind));
        if (e.Kind is "expense" or "income" && e.AmountMinor <= 0) throw new ArgumentException(Loc.T("Store_AmountPositive"));
        if (e.Kind is not ("refund" or "adjustment") && e.AmountMinor == 0) throw new ArgumentException(Loc.T("Store_AmountNonZero"));
        if (e.Kind == "transfer" && (e.ToAccountId is null || e.ToAccountId == e.AccountId))
            throw new ArgumentException(Loc.T("Store_TransferTarget"));
        if (e.Kind == "transfer" && e.CategoryId is not null) throw new ArgumentException(Loc.T("Store_TransferNoCategory"));
        EnsureLeaf(e.CategoryId);
        // 一個帳戶只放一種幣別（db-design）。幣別跟帳戶不符的列會被「拆到對的帳戶」搬走、在淨值總表標 ⚠ ——
        // 那是舊庫的債，不准從人工輸入這條路再製造一筆（記一筆的視窗以前就默默把美金帳戶的筆寫成 TWD）。
        if (Db.Scalar(_db, "SELECT name || '@@' || currency FROM account WHERE id=$a;", ("$a", e.AccountId))
            is not string acctRow) throw new ArgumentException(Loc.T("Store_AccountNotFound"));
        var split = acctRow.IndexOf("@@", StringComparison.Ordinal);
        var (acctName, acctCcy) = (acctRow[..split], Db.NormalizeCurrencyCode(acctRow[(split + 2)..]));
        if (Db.NormalizeCurrencyCode(e.Currency) != acctCcy)
            throw new ArgumentException(Loc.F("Store_CurrencyMismatch", acctName, acctCcy, Db.NormalizeCurrencyCode(e.Currency)));
        if (e.Kind == "transfer") EnsureSameCurrency(e.AccountId, e.ToAccountId!.Value);

        var newId = Db.InsertId(_db, """
            INSERT INTO entry(kind, account_id, to_account_id, category_id, currency, amount_minor,
                              txn_date, merchant, note, status, source, created_at)
            VALUES($kind, $acct, $to, $cat, $cur, $amt, $date, $merchant, $note, 'cleared', 'manual', $at);
            """,
            ("$kind", e.Kind), ("$acct", e.AccountId),
            ("$to", (object?)e.ToAccountId ?? DBNull.Value),
            ("$cat", (object?)e.CategoryId ?? DBNull.Value), ("$cur", Db.NormalizeCurrencyCode(e.Currency)),
            ("$amt", e.AmountMinor), ("$date", e.TxnDate),
            ("$merchant", (object?)e.Merchant ?? DBNull.Value),
            ("$note", (object?)e.Note ?? DBNull.Value), ("$at", Db.Now()));
        Touch();                                                  // 記一筆/盤點調整也要讓開著的分頁知道（跟其他寫入一樣）
        return newId;                                             // 之前先在這裡 return，Touch() 永远跑不到
    }

    // ---------------------------------------------------------------- 小工具

    static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    static long? NullableLong(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    static int? NullableInt(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    static long Lang(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetInt64(i);
    static long Lang(SqliteConnection c, string sql) => Db.Scalar(c, sql) is long v ? v : 0;
}
