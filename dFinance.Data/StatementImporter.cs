using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using dFinance.Parsing;

namespace dFinance.Data;

public enum StatementKind { Card, Deposit, Bill, Unknown }

public sealed record ImportResult(
    string FileName, string? Issuer, StatementKind Kind, long DocumentId,
    int Inserted, int Updated, int SkippedZero, int AutoCreatedAccounts, bool Duplicate,
    string? Message = null, List<string>? Warnings = null)
{
    public string Summary => Duplicate
        ? $"{FileName}：跳過 — {Message}"
        : $"{FileName}：{Issuer} {Kind} 新 {Inserted} 筆、更新 {Updated} 筆"
          + (SkippedZero > 0 ? $"（跳過 {SkippedZero} 筆 0 元）" : "")
          + (AutoCreatedAccounts > 0 ? $"，自動建帳戶 {AutoCreatedAccounts} 個" : "");
}

/// <summary>
/// 把解析好的帳單落庫：留底 PDF、記來源、去重、同帳期重匯時保留人工改過的分類。
/// 一個匯入 = 一個交易（失敗整個回滾，不會留半份）。
/// </summary>
public sealed class StatementImporter
{
    private readonly SqliteConnection _db;
    private readonly string _documentsDir;

    public StatementImporter(SqliteConnection db, string? documentsDir = null)
    {
        _db = db;
        _documentsDir = documentsDir ?? Db.DocumentsDirectory;
    }

    /// <summary>匯入一份 PDF：先擋重複檔案，再試密碼解析，最後落庫。</summary>
    public ImportResult ImportPdf(string pdfPath, IReadOnlyList<string> passwords)
    {
        var bytes = File.ReadAllBytes(pdfPath);
        var sha = Sha256(bytes);
        var seen = Db.Scalar(_db, "SELECT imported_at || ' 自 ' || file_path FROM document WHERE sha256=$s AND is_deleted=0;", ("$s", sha));
        if (seen is not null)
            return new ImportResult(Path.GetFileName(pdfPath), "", StatementKind.Unknown, 0, 0, 0, 0, 0, true, $"同一份檔案已匯入過（{seen}）");

        var statement = ParseWithPasswords(pdfPath, passwords);
        return Import(statement, pdfPath, sha, bytes);
    }

    /// <summary>
    /// 落庫主體。測試可以直接遞解析好的結果（不給 bytes 就不留底），不必有真的 PDF。
    /// </summary>
    public ImportResult Import(ParsedStatement s, string sourcePath, string? sha256 = null, byte[]? pdfBytes = null)
    {
        var kind = Classify(s.Parser);
        var now = Db.Now();
        var warnings = s.Warnings.ToList();
        var autoCreated = 0;
        var skippedZero = 0;
        int inserted = 0, updated = 0;

        using var tx = _db.BeginTransaction();

        var (accountId, cardId) = ResolveTargets(s, kind, warnings, ref autoCreated);

        var docId = Db.InsertId(_db, """
            INSERT INTO document(sha256, file_path, file_name, imported_at, issuer, parser, parser_ver,
                                 account_mask, statement_start, statement_end, due_date, page_count, warnings)
            VALUES($sha, $path, $name, $at, $issuer, $parser, $ver,
                   $mask, $start, $end, $due, $pages, $warnings);
            """,
            ("$sha", sha256 ??= SyntheticSha()),
            ("$path", sourcePath),
            ("$name", Path.GetFileName(sourcePath)),
            ("$at", now),
            ("$issuer", (object?)s.Issuer ?? DBNull.Value),
            ("$parser", string.IsNullOrEmpty(s.Parser) ? "unknown" : s.Parser),
            ("$ver", StatementParser.Version),
            ("$mask", (object?)s.AccountMask ?? DBNull.Value),
            ("$start", Date(s.StatementStartDate)), ("$end", Date(s.StatementEndDate)), ("$due", Date(s.DueDate)),
            ("$pages", DBNull.Value),
            ("$warnings", warnings.Count == 0 ? (object)DBNull.Value : JsonSerializer.Serialize(warnings)));

        var touched = new List<long>();
        var billId = LinkBill(s, cardId, docId, warnings);      // 先建帳單記錄，下面每筆才能挂上去
        var occurrences = new Dictionary<string, int>();      // 同鍵出現第幾次（同天同額刷兩次要存成兩筆）
        foreach (var t in s.Transactions)
        {
            var minor = Money.ToMinor(t.Amount);
            if (minor == 0) { skippedZero++; continue; }                       // 水費退款抵掉那种 0 元列，不進帳

            var currency = EnsureCurrency(t.Currency, warnings);
            var txnDate = t.Date == default ? null : Iso(t.Date);
            if (txnDate is null) warnings.Add($"有一筆拿不到消費日（{t.Merchant} {t.Amount}），先留空等人補。");

            var lineKind = minor < 0 ? "refund" : "expense";
            var baseHash = BaseHash(s, t, minor, currency);
            int n = occurrences.TryGetValue(baseHash, out var c) ? c + 1 : 1;
            occurrences[baseHash] = n;
            var hash = n == 1 ? baseHash : $"{baseHash}#{n}";

            var existing = FindExisting(hash, accountId, docId, kind);
            if (existing is not null && touched.Contains(existing.Value))      // 保底：同一筆不准被兩列同時認領
            {
                hash = NextFreeHash(hash, accountId, docId, kind);
                existing = null;
            }

            if (existing is not null && Edited(existing.Value))
            {
                // 人工改過：只准更新比對用的原始資料，分類/狀態/備註一律不動。
                Db.Exec(_db, """
                    UPDATE entry SET source_raw=$raw, source_page=$page, source_document_id=$doc,
                                     updated_at=$at, orphaned_at=NULL
                    WHERE id=$id;
                    """,
                    ("$raw", (object?)t.RawLine ?? DBNull.Value), ("$page", Page(t)), ("$doc", docId),
                    ("$at", now), ("$id", existing.Value));
                touched.Add(existing.Value);
                updated++;
                continue;
            }

            if (existing is not null)
            {
                Db.Exec(_db, """
                    UPDATE entry SET kind=$kind, amount_minor=$amt, currency=$cur, txn_date=$date, posting_date=$post,
                                     merchant=$merchant, source_raw=$raw, source_page=$page, source_document_id=$doc,
                                     card_id=$card, bill_id=$bill, updated_at=$at, orphaned_at=NULL
                    WHERE id=$id;
                    """,
                    ("$kind", lineKind), ("$amt", minor), ("$cur", currency),
                    ("$date", (object?)txnDate ?? DBNull.Value), ("$post", Date(t.PostingDate)),
                    ("$merchant", (object?)t.Merchant ?? DBNull.Value),
                    ("$raw", (object?)t.RawLine ?? DBNull.Value), ("$page", Page(t)), ("$doc", docId),
                    ("$card", (object?)cardId ?? DBNull.Value), ("$bill", (object?)billId ?? DBNull.Value),
                    ("$at", now), ("$id", existing.Value));
                touched.Add(existing.Value);
                updated++;
                continue;
            }

            var newId = Db.InsertId(_db, """
                INSERT INTO entry(kind, account_id, card_id, bill_id, currency, amount_minor, txn_date, posting_date,
                                  merchant, status, source, source_document_id, source_page, source_line_hash,
                                  source_raw, created_at)
                VALUES($kind, $account, $card, $bill, $cur, $amt, $date, $post,
                       $merchant, 'imported', 'import', $doc, $page, $hash, $raw, $at);
                """,
                ("$kind", lineKind), ("$account", accountId), ("$card", (object?)cardId ?? DBNull.Value),
                ("$bill", (object?)billId ?? DBNull.Value),
                ("$cur", currency), ("$amt", minor),
                ("$date", (object?)txnDate ?? DBNull.Value), ("$post", Date(t.PostingDate)),
                ("$merchant", (object?)t.Merchant ?? DBNull.Value),
                ("$page", Page(t)), ("$hash", hash), ("$doc", docId),
                ("$raw", (object?)t.RawLine ?? DBNull.Value), ("$at", now));
            touched.Add(newId);
            inserted++;
        }

        if (s.Transactions.Any(t => t.Amount < 0))
            warnings.Add("有負額列先當 refund；其中可能是繳款列，要等 parser 拆出「繳款/消費」後改記 transfer。");

        var superseded = SupersedePrior(s, accountId, docId, touched, kind);

        // 警告可能在中途增加，最後一次寫回。
        Db.Exec(_db, "UPDATE document SET warnings=$w, skipped_zero=$z WHERE id=$id;",
            ("$w", warnings.Count == 0 ? (object)DBNull.Value : JsonSerializer.Serialize(warnings)),
            ("$z", skippedZero), ("$id", docId));

        tx.Commit();

        var archived = pdfBytes is null ? null : Archive(pdfBytes, sourcePath, sha256);
        if (archived is not null) Db.Exec(_db, "UPDATE document SET archived_path=$p WHERE id=$id;", ("$p", archived), ("$id", docId));

        return new ImportResult(Path.GetFileName(sourcePath), s.Issuer, kind, docId, inserted, updated,
                                skippedZero, autoCreated, false,
                                superseded > 0 ? $"取代 {superseded} 份舊的同帳期匯入" : null, warnings);
    }

    /// <summary>依 parser 的命名規則（&lt;銀行&gt;&lt;Card|Deposit|Bill&gt;Parser）判斷單據種類。</summary>
    public static StatementKind Classify(string parser) => parser switch
    {
        _ when parser.EndsWith("CardParser", StringComparison.Ordinal) => StatementKind.Card,
        _ when parser.EndsWith("DepositParser", StringComparison.Ordinal) => StatementKind.Deposit,
        _ when parser.EndsWith("BillParser", StringComparison.Ordinal) => StatementKind.Bill,
        _ => StatementKind.Unknown,
    };

    static ParsedStatement ParseWithPasswords(string pdfPath, IReadOnlyList<string> passwords)
    {
        var tried = new List<string>();
        foreach (var pw in passwords.Distinct())
        {
            try { return StatementParser.Parse(pdfPath, pw); }
            catch (Exception e) when (e.GetType().Name.Contains("Password", StringComparison.OrdinalIgnoreCase)
                                   || e.GetType().Name.Contains("Encrypted", StringComparison.OrdinalIgnoreCase))
            { tried.Add(pw.Length == 0 ? "(無密碼)" : pw); }
        }
        throw new InvalidOperationException($"密碼不對或這份 PDF 開不了（試過 {tried.Count} 組：{string.Join(", ", tried)}）");
    }

    (long accountId, long? cardId) ResolveTargets(ParsedStatement s, StatementKind kind, List<string> warnings, ref int autoCreated)
    {
        // 一筆都進不去的（OCR 未實作的中信/匯豐、或 0 元單）不要順手生出帳戶，否则帳戶表會被垃圾填滿。
        if (!s.Transactions.Any(t => t.Amount != 0)) return (0, null);

        var issuer = string.IsNullOrWhiteSpace(s.Issuer) ? "(未辨識發行方)" : s.Issuer.Trim();
        var mask = (s.AccountMask ?? "").Trim();

        if (kind == StatementKind.Bill) return (PaymentAccount(warnings, ref autoCreated), null);

        var type = kind == StatementKind.Card ? "credit_card" : "bank";
        var accountId = FindAccount(issuer, mask, type) ?? CreateAccount(issuer, mask, type, warnings, ref autoCreated);
        if (autoCreated > 0) warnings.Add($"自動建立帳戶，請確認是不是既有帳戶。");

        long? cardId = null;
        if (kind == StatementKind.Card)
        {
            var cardMask = mask.Length > 0 ? mask : (s.Transactions.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Card))?.Card ?? "").Trim();
            cardId = Db.Scalar(_db, "SELECT id FROM card WHERE account_id=$a AND ifnull(mask,'')=$m;",
                               ("$a", accountId), ("$m", cardMask)) is long id ? id : (long?)null;
            if (cardId is null)
                cardId = Db.InsertId(_db, "INSERT INTO card(account_id, mask) VALUES($a, $m);",
                                     ("$a", accountId), ("$m", string.IsNullOrEmpty(cardMask) ? (object)DBNull.Value : cardMask));
        }
        return (accountId, cardId);
    }

    long? FindAccount(string issuer, string mask, string type)
        => Db.Scalar(_db, """
            SELECT id FROM account WHERE ifnull(issuer,'')=$i AND ifnull(mask,'')=$m AND type=$t AND is_archived=0
            ORDER BY id LIMIT 1;
            """, ("$i", issuer), ("$m", mask), ("$t", type)) is long id ? id : (long?)null;

    long CreateAccount(string issuer, string mask, string type, List<string> warnings, ref int autoCreated)
    {
        var baseName = string.IsNullOrEmpty(mask) ? issuer : $"{issuer} {mask}";
        var name = baseName;
        for (int n = 2; Db.Scalar(_db, "SELECT id FROM account WHERE name=$n;", ("$n", name)) is not null; n++)
            name = $"{baseName} {n}";

        autoCreated++;
        return Db.InsertId(_db, """
            INSERT INTO account(name, type, currency, issuer, mask, created_at)
            VALUES($n, $t, 'TWD', $i, $m, $at);
            """,
            ("$n", name), ("$t", type), ("$i", issuer),
            ("$m", string.IsNullOrEmpty(mask) ? (object)DBNull.Value : mask), ("$at", Db.Now()));
    }

    /// <summary>繳費單的付款帳戶：settings 指定的 → 最早建的銀行帳戶 → 建一個現金帳戶並講明。</summary>
    long PaymentAccount(List<string> warnings, ref int autoCreated)
    {
        var setting = Db.Scalar(_db, "SELECT value FROM settings WHERE key='default_payment_account_id';");
        if (setting is string raw && long.TryParse(raw, out var configured)
            && Db.Scalar(_db, "SELECT id FROM account WHERE id=$id;", ("$id", configured)) is not null)
            return configured;

        var picked = Db.Scalar(_db, "SELECT id FROM account WHERE type='bank' AND is_archived=0 ORDER BY id LIMIT 1;")
                  ?? Db.Scalar(_db, "SELECT id FROM account WHERE type='cash' AND is_archived=0 ORDER BY id LIMIT 1;")
                  ?? (object?)null;
        if (picked is long existing)
        {
            Db.Exec(_db, "INSERT OR REPLACE INTO settings(key, value) VALUES('default_payment_account_id', $v);", ("$v", existing.ToString()));
            return existing;
        }

        warnings.Add("還沒有任何銀行帳戶，繳費單先記到新的現金帳戶（之後可在設定改預設付款帳戶）。");
        autoCreated++;
        var newId = Db.InsertId(_db, """
            INSERT INTO account(name, type, currency, created_at) VALUES('現金', 'cash', 'TWD', $at);
            """, ("$at", Db.Now()));
        Db.Exec(_db, "INSERT OR REPLACE INTO settings(key, value) VALUES('default_payment_account_id', $v);", ("$v", newId.ToString()));
        return newId;
    }

    string EnsureCurrency(string? raw, List<string> warnings)
    {
        var code = (raw ?? "").Trim().ToUpperInvariant();
        if (code.Length != 3 || !code.All(char.IsLetter))
        {
            if (!string.IsNullOrWhiteSpace(raw)) warnings.Add($"幣別「{raw}」不是三尺碼，先當 TWD。");
            return "TWD";
        }
        if (Db.Scalar(_db, "SELECT code FROM currency WHERE code=$c;", ("$c", code)) is null)
        {
            warnings.Add($"第一次看到幣別 {code}，先以 2 位小數建立，顯示位數之後可改。");
            Db.Exec(_db, "INSERT INTO currency(code, name, scale) VALUES($c, $c, 2);", ("$c", code));
        }
        return code;
    }

    long? LinkBill(ParsedStatement s, long? cardId, long docId, List<string> warnings)
    {
        if (cardId is null) return null;
        var periodEnd = s.StatementEndDate ?? s.DueDate
            ?? (s.Transactions.Count > 0 ? s.Transactions.Max(t => t.Date) : default);
        if (periodEnd == default) { warnings.Add("拿不到帳期結束日，這份信用卡帳單不建帳單記錄。"); return null; }

        var existing = Db.Scalar(_db, "SELECT id FROM credit_card_bill WHERE card_id=$c AND period_end=$e;",
                                 ("$c", cardId.Value), ("$e", Iso(periodEnd))) is long id ? id : (long?)null;
        if (existing is not null)
        {
            Db.Exec(_db, """
                UPDATE credit_card_bill SET period_start=$s, due_date=$due, total_due_minor=$total,
                       minimum_due_minor=$min, statement_document_id=$doc
                WHERE id=$id;
                """,
                ("$s", Date(s.StatementStartDate)), ("$due", Date(s.DueDate)),
                ("$total", s.TotalDue is null ? (object)DBNull.Value : Money.ToMinor(s.TotalDue.Value)),
                ("$min", s.MinimumDue is null ? (object)DBNull.Value : Money.ToMinor(s.MinimumDue.Value)),
                ("$doc", docId), ("$id", existing.Value));
            return existing;
        }

        return Db.InsertId(_db, """
            INSERT INTO credit_card_bill(card_id, period_start, period_end, due_date, total_due_minor,
                                         minimum_due_minor, statement_document_id)
            VALUES($card, $s, $e, $due, $total, $min, $doc);
            """,
            ("$card", cardId.Value), ("$s", Date(s.StatementStartDate)), ("$e", Iso(periodEnd)),
            ("$due", Date(s.DueDate)),
            ("$total", s.TotalDue is null ? (object)DBNull.Value : Money.ToMinor(s.TotalDue.Value)),
            ("$min", s.MinimumDue is null ? (object)DBNull.Value : Money.ToMinor(s.MinimumDue.Value)),
            ("$doc", docId));
    }

    /// <summary>
    /// 同一帳戶同一帳期的舊匯入：標記被取代，這次沒對到的舊列標孤兒（不准刪，可能只是新版漏解析）。
    /// 只对信用卡做：只有它能用 (card_id, period_end) 當自然鍵確定「同一片帳單」。
    /// shortcut: 存款單/繳費單不做取代（兩張同月份的缴費單會被當成兩份各自對的單子，不该互砍），
    ///           重匯同一份靠 sha256 挡、同內容靠哈希匹配；要連舊列一起清可改走「刪除這次匯入」。
    /// </summary>
    int SupersedePrior(ParsedStatement s, long accountId, long docId, List<long> touched, StatementKind kind)
    {
        if (kind != StatementKind.Card) return 0;
        var prior = Db.Query(_db, """
            SELECT d.id FROM document d
            JOIN entry e ON e.source_document_id = d.id
            WHERE e.account_id=$a AND d.id<>$doc AND d.is_deleted=0 AND d.superseded_at IS NULL
              AND ifnull(d.statement_end,'')=ifnull($end,'') AND ifnull(d.issuer,'')=ifnull($issuer,'')
            GROUP BY d.id;
            """, ("$a", accountId), ("$end", Date(s.StatementEndDate)), ("$issuer", (object?)s.Issuer ?? DBNull.Value), ("$doc", docId))
            .Select(o => Convert.ToInt64(o)).ToList();
        if (prior.Count == 0) return 0;

        foreach (var id in prior)
        {
            Db.Exec(_db, "UPDATE document SET superseded_at=$at WHERE id=$id;", ("$at", Db.Now()), ("$id", id));
            var orphans = Db.Query(_db, """
                SELECT id FROM entry WHERE source_document_id=$doc AND deleted_at IS NULL;
                """, ("$doc", id)).Select(o => Convert.ToInt64(o)).Where(i => !touched.Contains(i)).ToList();
            foreach (var e in orphans)
                Db.Exec(_db, "UPDATE entry SET orphaned_at=$at, updated_at=$at WHERE id=$id;", ("$at", Db.Now()), ("$id", e));
        }
        return prior.Count;
    }

    /// <summary>
    /// 去重與重匯比對用的穩定鍵。商家名刻意不入哈希：parser 之後會清洗商家名（見 TODO），
    /// 進哈希會讓舊列全部認不出來、變成重複列。
    /// </summary>
    static string BaseHash(ParsedStatement s, ParsedTransaction t, long minor, string currency)
        => Sha256(Encoding.UTF8.GetBytes(string.Join('|', s.Issuer, s.AccountMask, t.Card,
               s.StatementEndDate?.ToString("yyyyMMdd") ?? "",
               t.Date == default ? "" : t.Date.ToString("yyyyMMdd"),
               t.PostingDate?.ToString("yyyyMMdd") ?? "", minor, currency, t.Page)));

    /// <summary>
    /// 哈希比對的範圍。信用卡可以跳檔案比（卡＋帳期＋金額＋日期 就是同一筆的身份，重匯要能對回去）；
    /// 存款單与缴費單只在同一份檔案內比 —— 它們沒有可靠身份，跳檔案比會把
    /// 「同一天同額的兩張不同戶號缴費單」合成一筆，那是悄悄吃掉資料，比较严重。
    /// shortcut: 跳檔案的「疑似重複」应該只做標記不擋，屬於帳本清單那一期的 UI（見 TODO）。
    /// </summary>
    long? FindExisting(string hash, long accountId, long docId, StatementKind kind)
    {
        var ps = new List<(string, object?)> { ("$h", hash), ("$a", accountId) };
        var scope = "";
        if (kind != StatementKind.Card) { scope = " AND source_document_id=$doc"; ps.Add(("$doc", docId)); }
        return Db.Scalar(_db,
            "SELECT id FROM entry WHERE source_line_hash=$h AND account_id=$a AND deleted_at IS NULL" + scope + " ORDER BY id LIMIT 1;",
            [.. ps]) is long id ? id : (long?)null;
    }

    /// <summary>同一個範圍已有一模一樣的鍵 → 加出現次數後綴，讓「同天同店刷兩次同額」能存成兩筆。</summary>
    string NextFreeHash(string hash, long accountId, long docId, StatementKind kind)
    {
        if (FindExisting(hash, accountId, docId, kind) is null) return hash;
        for (int n = 2; n <= 50; n++)
            if (FindExisting($"{hash}#{n}", accountId, docId, kind) is null) return $"{hash}#{n}";
        return $"{hash}#many-{Guid.NewGuid():N}";              // 同一鍵超過 50 筆不會發生，保底不卡在迴圈
    }

    bool Edited(long entryId) => Db.Scalar(_db, "SELECT edited_at FROM entry WHERE id=$id;", ("$id", entryId)) is string;

    string Archive(byte[] bytes, string sourcePath, string sha)
    {
        var dir = Path.Combine(_documentsDir, DateTime.Now.ToString("yyyyMM"));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"{sha}.pdf");
        if (!File.Exists(target)) File.WriteAllBytes(target, bytes);
        return target;
    }

    static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    /// <summary>沒有檔案位元組時（測試或從已有資料重跑）用：每次匯入都是新事件，所以要唯一。</summary>
    static string SyntheticSha() => Sha256(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N")));
    static object? Page(ParsedTransaction t) => t.Page == 0 ? (object?)DBNull.Value : t.Page;
    static object? Date(DateOnly? d) => d is null || d.Value == default ? (object?)DBNull.Value : d.Value.ToString("yyyy-MM-dd");
    static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd");
}
