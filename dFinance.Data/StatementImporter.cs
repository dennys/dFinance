using System.Security.Cryptography;
using dFinance.Data.Localization;
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
        ? Loc.F("Imp_Skipped", FileName, Message)
        : Loc.F("Imp_Line", FileName, Issuer, Kind, Inserted, Updated)
          + (SkippedZero > 0 ? Loc.F("Imp_SkippedZero", SkippedZero) : "")
          + (AutoCreatedAccounts > 0 ? Loc.F("Imp_AutoCreated", AutoCreatedAccounts) : "");
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
    public ImportResult ImportPdf(string pdfPath, IReadOnlyList<string> passwords, IProgress<string>? progress = null)
    {
        var bytes = File.ReadAllBytes(pdfPath);
        var sha = Sha256(bytes);
        var seen = Db.Scalar(_db, "SELECT imported_at || $sep || file_path FROM document WHERE sha256=$s AND is_deleted=0;", ("$sep", Loc.T("Imp_SeenSeparator")), ("$s", sha));   // 串接用的連接詞也走語系
        if (seen is not null)
            return new ImportResult(Path.GetFileName(pdfPath), "", StatementKind.Unknown, 0, 0, 0, 0, 0, true, Loc.F("Imp_DuplicateFile", seen));

        progress?.Report(Loc.F("Imp_Identifying", Path.GetFileName(pdfPath)));
        var statement = ParseWithPasswords(pdfPath, passwords, progress);
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

        // 留底要在交易前就寫好：寫檔失敗（磁碟滿、網路盤斷線）→ 整個匯入回滾，可以重來。
        // 之前是 commit 之後才寫，失敗會留下「有帳、沒留底」，而重匯又被 sha256 擋死 → 永久沒有原始單據可查。
        sha256 ??= SyntheticSha();
        var archived = pdfBytes is null ? null : Archive(pdfBytes, sourcePath, sha256);

        using var tx = _db.BeginTransaction();

        var (accountId, cardId) = ResolveTargets(s, kind, warnings, ref autoCreated);

        // sha256 是 UNIQUE：同一份內容被軟刪過（is_deleted=1）時插新的會直接被打死，所以沿用那一列。
        Db.Exec(_db, """
            INSERT INTO document(sha256, file_path, file_name, imported_at, issuer, parser, parser_ver,
                                 account_mask, statement_start, statement_end, due_date, page_count, warnings)
            VALUES($sha, $path, $name, $at, $issuer, $parser, $ver,
                   $mask, $start, $end, $due, $pages, $warnings)
            ON CONFLICT(sha256) DO UPDATE SET
                is_deleted=0, file_path=excluded.file_path, file_name=excluded.file_name,
                imported_at=excluded.imported_at, issuer=excluded.issuer, parser=excluded.parser,
                parser_ver=excluded.parser_ver, account_mask=excluded.account_mask,
                statement_start=excluded.statement_start, statement_end=excluded.statement_end,
                due_date=excluded.due_date, page_count=excluded.page_count,
                warnings=NULL, skipped_zero=0, archived_path=NULL, superseded_at=NULL;
            """,
            ("$sha", sha256),
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
        var docId = (long)Db.Scalar(_db, "SELECT id FROM document WHERE sha256=$s;", ("$s", sha256))!;

        var touched = new List<long>();
        var lineAccounts = new Dictionary<string, long>();          // 列級帳號 → account.id（同帳號只查/建一次）
        var billId = LinkBill(s, cardId, docId, warnings);      // 先建帳單記錄，下面每筆才能挂上去
        var occurrences = new Dictionary<string, int>();      // 同鍵出現第幾次（同天同額刷兩次要存成兩筆）
        // 繳款列的錢要從那個帳戶扣（卡的自動扣繳帳戶 → 預設付款帳戶）；認不出來就不拆（見 PayerOf）。
        long? payer = s.Transactions.Any(t => t.IsPayment) ? PayerOf(cardId) : null;
        int transfers = 0;
        foreach (var t in s.Transactions)
        {
            var signed = Money.ToMinor(t.Amount);            // 單上的符號（負＝退款/提款/抵帳）
            if (signed == 0) { skippedZero++; continue; }                       // 水費退款抵掉那种 0 元列，不進帳

            var currency = EnsureCurrency(t.Currency, warnings);
            var account = AccountFor(s, t.Account, currency, accountId, kind, lineAccounts, warnings, ref autoCreated);
            var txnDate = t.Date == default ? null : Iso(t.Date);
            if (txnDate is null) warnings.Add(Loc.F("Imp_NoDate", t.Merchant, t.Amount));

            // kind 由單據種類定，不是只看正負號：存款對帳單的正數是「存入」（銀行的借貸記法跟卡帳相反），
            // 只照正負號記就會把薪資記成 expense、提款記成 refund —— 餘額與淨值整個反（見 CHANGELOG + 0009 遷移）。
            // 存進 DB 的 income/expense/transfer 一律正值（entry 的 CHECK），加減由 kind 決定。
            var lineKind = kind == StatementKind.Deposit
                ? (signed > 0 ? "income" : "expense")
                : (signed < 0 ? "refund" : "expense");
            var minor = lineKind == "expense" && signed < 0 ? -signed : signed;
            // 繳款列不是消費也不是退款：那是從另一個帳戶打錢進來把卡帳抵掉，所以要記成轉帳
            // （扣款帳戶 → 卡帳帳戶；卡帳的 derived 是負的，轉入才會往 0 走）。
            // 扣款帳戶認不出來就宁願留 refund：性質錯比憑空讓某個帳戶少一筆錢好。
            long? toAccount = null;
            bool isTransfer = t.IsPayment && kind == StatementKind.Card && payer is long;
            if (isTransfer)
            {
                lineKind = "transfer";
                toAccount = account;                       // 收錢的那側才是這份單子的帳戶
                account = payer!.Value;
                minor = Math.Abs(minor);                   // transfer 的 CHECK 要正數
                transfers++;
            }
            // 哈希一律照「單上的符號」算，不准照存進 DB 的那個：0009 遷移改過 kind 與正負號，
            // 哈希跟著 DB 走會讓舊列全部認不出來 → 重匯变成存兩份。
            var baseHash = BaseHash(s, t, isTransfer ? Math.Abs(signed) : signed, currency);
            // 跳檔案比的資格只看有沒有可靠身份（不是看單據類型）：缴費單現在會抽戶號，抽不到就退回檔案內比。
            bool crossFile = kind != StatementKind.Unknown &&
                             !(string.IsNullOrWhiteSpace(t.Account) && string.IsNullOrWhiteSpace(s.AccountMask));
            int n = occurrences.TryGetValue(baseHash, out var c) ? c + 1 : 1;
            occurrences[baseHash] = n;
            var hash = n == 1 ? baseHash : $"{baseHash}#{n}";

            var existing = FindExisting(hash, account, docId, crossFile);
            if (existing is not null && touched.Contains(existing.Value))      // 保底：同一筆不准被兩列同時認領
            {
                hash = NextFreeHash(hash, account, docId, crossFile);
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
                    UPDATE entry SET kind=$kind, account_id=$account, to_account_id=$to, amount_minor=$amt,
                                     currency=$cur, txn_date=$date, posting_date=$post,
                                     merchant=$merchant, source_raw=$raw, source_page=$page, source_document_id=$doc,
                                     card_id=$card, bill_id=$bill, updated_at=$at, orphaned_at=NULL
                    WHERE id=$id;
                    """,
                    ("$kind", lineKind), ("$account", account), ("$to", (object?)toAccount ?? DBNull.Value),
                    ("$amt", minor), ("$cur", currency),
                    ("$date", (object?)txnDate ?? DBNull.Value), ("$post", Date(t.PostingDate)),
                    ("$merchant", (object?)t.Merchant ?? DBNull.Value),
                    ("$raw", (object?)t.RawLine ?? DBNull.Value), ("$page", Page(t)), ("$doc", docId),
                    ("$card", (object?)cardId ?? DBNull.Value), ("$bill", (object?)billId ?? DBNull.Value),
                    ("$at", now), ("$id", existing.Value));
                touched.Add(existing.Value);
                updated++;
                continue;
            }

            // 同一筆卡帳繳款會在兩份單子上各出現一次：卡帳那側（「已繳款金額-本行帳戶扣款」，我們落成轉帳）
            // 與扣款帳戶自己的存款單那側（「兆豐卡 兆豐銀行」那种支出）。兩份都匯就會把同一個錢移動扣兩次 ——
            // 那個帳戶的餘額與淨值一起錯（餘額校驗就是這樣抓到它的）。
            // 认得出來就不插新的：先到的那一列留下來当這笔移動的身份，存款單那筆先到時就地升成轉帳
            // （雨側才都對）。认不出來照樣插 —— 宁願餘額校驗报紅，也不要悄悄少一筆。
            var twin = PaymentTwin(isTransfer, account, toAccount, minor, currency, t.Date == default ? null : t.Date, t.Merchant);
            if (twin is not null && !touched.Contains(twin.Value) && !Edited(twin.Value))
            {
                if (isTransfer)                                    // 存款單那筆先到：把它升成轉帳，卡側才不會多一筆
                    Db.Exec(_db, """
                        UPDATE entry SET kind='transfer', to_account_id=$to, card_id=$card, bill_id=$bill,
                                         category_id=NULL, updated_at=$at, orphaned_at=NULL
                        WHERE id=$id;
                        """,
                        ("$to", (object?)toAccount ?? DBNull.Value), ("$card", (object?)cardId ?? DBNull.Value),
                        ("$bill", (object?)billId ?? DBNull.Value), ("$at", now), ("$id", twin.Value));
                warnings.Add(Loc.F("Imp_PaymentTwin", t.Merchant ?? "", txnDate ?? ""));
                touched.Add(twin.Value);
                updated++;
                continue;
            }

            var newId = Db.InsertId(_db, """
                INSERT INTO entry(kind, account_id, to_account_id, card_id, bill_id, currency, amount_minor, txn_date, posting_date,
                                  merchant, status, source, source_document_id, source_page, source_line_hash,
                                  source_raw, created_at)
                VALUES($kind, $account, $to, $card, $bill, $cur, $amt, $date, $post,
                       $merchant, 'imported', 'import', $doc, $page, $hash, $raw, $at);
                """,
                ("$kind", lineKind), ("$account", account), ("$to", (object?)toAccount ?? DBNull.Value),
                ("$card", (object?)cardId ?? DBNull.Value),
                ("$bill", (object?)billId ?? DBNull.Value),
                ("$cur", currency), ("$amt", minor),
                ("$date", (object?)txnDate ?? DBNull.Value), ("$post", Date(t.PostingDate)),
                ("$merchant", (object?)t.Merchant ?? DBNull.Value),
                ("$page", Page(t)), ("$hash", hash), ("$doc", docId),
                ("$raw", (object?)t.RawLine ?? DBNull.Value), ("$at", now));
            touched.Add(newId);
            inserted++;
        }

        // 存款單的負額就是提款/支出（已經照 kind=expense 記好了），不需要提醒；
        // 卡帳與繳費單的負額才會落到 refund，那種要人確認是不是該記成轉帳。
        if (kind != StatementKind.Deposit && s.Transactions.Any(t => t.Amount < 0 && !t.IsPayment))
            warnings.Add(Loc.T("Imp_NegativeRows"));
        if (transfers > 0)
            warnings.Add(Loc.F("Imp_PaymentTransfer", transfers, AccountName(payer!.Value), AccountName(accountId)));
        else if (s.Transactions.Any(t => t.IsPayment))
            warnings.Add(Loc.F("Imp_PaymentNoPayer", s.Transactions.Count(t => t.IsPayment)));

        // 單子上印的餘額進 account_balance（source='statement'）：這是偵測漏解析的自動防呆，
        // 對不對得上由 LedgerStore.BalanceMismatches() 照 as_of 講，這裡只負責存進去。
        foreach (var b in s.Balances)
        {
            if (b.AsOf == default) continue;                                       // 不知道哪天結算的餘額存了只會造成誤報
            var currency = EnsureCurrency(b.Currency, warnings);
            var account = AccountFor(s, b.Account, currency, accountId, kind, lineAccounts, warnings, ref autoCreated);
            if (account == 0) continue;
            Db.Exec(_db, """
                INSERT INTO account_balance(account_id, as_of, currency, balance_minor, source, document_id)
                VALUES($a, $asof, $cur, $amt, 'statement', $doc)
                ON CONFLICT(account_id, as_of, currency) DO UPDATE
                    SET balance_minor = excluded.balance_minor, document_id = excluded.document_id
                  WHERE account_balance.source = 'statement';
                """,
                ("$a", account), ("$asof", Iso(b.AsOf)), ("$cur", currency), ("$amt", Money.ToMinor(b.Amount)),
                ("$doc", docId));
        }

        var superseded = SupersedePrior(s, accountId, docId, touched, kind);

        // 警告可能在中途增加，最後一次寫回；留底路徑也在這裡（同一個交易：要嘛帳與留底一起進，要嘛都沒有）。
        Db.Exec(_db, "UPDATE document SET warnings=$w, skipped_zero=$z, archived_path=$arch WHERE id=$id;",
            ("$w", warnings.Count == 0 ? (object)DBNull.Value : JsonSerializer.Serialize(warnings)),
            ("$z", skippedZero), ("$arch", (object?)archived ?? DBNull.Value), ("$id", docId));

        tx.Commit();

        return new ImportResult(Path.GetFileName(sourcePath), s.Issuer, kind, docId, inserted, updated,
                                skippedZero, autoCreated, false,
                                superseded > 0 ? Loc.F("Imp_Superseded", superseded) : null, warnings);
    }

    /// <summary>依 parser 的命名規則（&lt;銀行&gt;&lt;Card|Deposit|Bill&gt;Parser）判斷單據種類。</summary>
    public static StatementKind Classify(string parser) => parser switch
    {
        _ when parser.EndsWith("CardParser", StringComparison.Ordinal) => StatementKind.Card,
        _ when parser.EndsWith("DepositParser", StringComparison.Ordinal) => StatementKind.Deposit,
        _ when parser.EndsWith("BillParser", StringComparison.Ordinal) => StatementKind.Bill,
        _ => StatementKind.Unknown,
    };

    static ParsedStatement ParseWithPasswords(string pdfPath, IReadOnlyList<string> passwords, IProgress<string>? progress = null)
    {
        var tried = new List<string>();
        foreach (var pw in passwords.Distinct())
        {
            try { return StatementParser.Parse(pdfPath, pw, Db.DataDirectory, progress); }
            catch (Exception e) when (e.GetType().Name.Contains("Password", StringComparison.OrdinalIgnoreCase)
                                   || e.GetType().Name.Contains("Encrypted", StringComparison.OrdinalIgnoreCase))
            { tried.Add(pw.Length == 0 ? Loc.T("Imp_NoPassword") : pw); }
        }
        // 密碼的內容不准進訊息：這句話會被寫到「匯入 PDF」分頁的 log 欄（也可能被截图貼出去），
        // 而承諾是「密碼只在記憶體裡過一遍」。
        throw new InvalidOperationException(Loc.F("Imp_BadPassword", tried.Count));
    }

    (long accountId, long? cardId) ResolveTargets(ParsedStatement s, StatementKind kind, List<string> warnings, ref int autoCreated)
    {
        // 一筆都進不去的（OCR 未實作的中信/匯豐、或 0 元單）不要順手生出帳戶，否则帳戶表會被垃圾填滿。
        if (!s.Transactions.Any(t => t.Amount != 0)) return (0, null);

        var issuer = string.IsNullOrWhiteSpace(s.Issuer) ? Loc.T("Imp_UnknownIssuer") : s.Issuer.Trim();
        var mask = (s.AccountMask ?? "").Trim();

        if (kind == StatementKind.Bill) return (PaymentAccount(warnings, ref autoCreated), null);

        var type = kind == StatementKind.Card ? "credit_card" : "bank";
        var currency = EnsureCurrency(s.Transactions.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Currency))?.Currency, warnings);
        var accountId = FindAccount(issuer, mask, type, currency) ?? CreateAccount(issuer, mask, type, currency, warnings, ref autoCreated);
        if (autoCreated > 0) warnings.Add(Loc.T("Imp_AutoCreatedAccount"));

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

    /// <summary>
    /// 一筆交易（或一個單上餘額）的帳戶。列上寫了帳號就照它（一份單子含多個帳號的存款單），沒寫就跟著整份單子的帳戶；
    /// 幣別永遠 participate 鍵值 —— 一個帳戶只放一種幣別，美金列不會再被倒進台幣帳戶（那個錢照台幣進淨值）。
    /// </summary>
    long AccountFor(ParsedStatement s, string? lineAccount, string currency, long docAccountId, StatementKind kind,
                    Dictionary<string, long> cache, List<string> warnings, ref int autoCreated)
    {
        if (kind == StatementKind.Bill || docAccountId == 0) return docAccountId;
        var key = $"{(lineAccount ?? "").Trim()}|{currency}";
        if (cache.TryGetValue(key, out var cached)) return cached;

        var (docIssuer, docMask, docCurrency) = DocAccount(docAccountId);
        var mask = (lineAccount ?? "").Trim();
        if (mask.Length == 0)
        {
            if (currency == docCurrency) return cache[key] = docAccountId;   // 列上沒帳號也没換幣別 = 就是這份單子的帳戶
            mask = docMask;                                                  // 要換幣別：帳號沿用整份單子的，另開一個幣別的帳戶
        }

        var issuer = (string.IsNullOrWhiteSpace(s.Issuer) ? docIssuer : s.Issuer.Trim()) ?? Loc.T("Imp_UnknownIssuer");
        var type = kind == StatementKind.Card ? "credit_card" : "bank";
        var before = autoCreated;
        var id = FindAccount(issuer, mask, type, currency) ?? CreateAccount(issuer, mask, type, currency, warnings, ref autoCreated);
        if (autoCreated > before) warnings.Add(Loc.T("Imp_AutoCreatedAccount"));
        return cache[key] = id;
    }

    readonly Dictionary<long, (string? Issuer, string Mask, string Currency)> _docAccounts = new();

    (string? Issuer, string Mask, string Currency) DocAccount(long id)
    {
        if (_docAccounts.TryGetValue(id, out var hit)) return hit;
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT issuer, ifnull(mask,''), currency FROM account WHERE id=$i;";
        cmd.Parameters.AddWithValue("$i", id);
        using var r = cmd.ExecuteReader();
        var value = r.Read() ? (r.IsDBNull(0) ? null : r.GetString(0), r.GetString(1), r.GetString(2)) : (null, "", "TWD");
        return _docAccounts[id] = value;
    }

    long? FindAccount(string issuer, string mask, string type, string currency)
        => Db.Scalar(_db, """
            SELECT id FROM account WHERE ifnull(issuer,'')=$i AND ifnull(mask,'')=$m AND type=$t AND currency=$c AND is_archived=0
            ORDER BY id LIMIT 1;
            """, ("$i", issuer), ("$m", mask), ("$t", type), ("$c", currency)) is long id ? id : (long?)null;

    long CreateAccount(string issuer, string mask, string type, string currency, List<string> warnings, ref int autoCreated)
    {
        var baseName = (string.IsNullOrEmpty(mask) ? issuer : $"{issuer} {mask}")
                     + (currency == Db.BaseCurrency(_db) ? "" : $" {currency}");   // 同帳號多幣別時名字要分得開
        var name = baseName;
        for (int n = 2; Db.Scalar(_db, "SELECT id FROM account WHERE name=$n;", ("$n", name)) is not null; n++)
            name = $"{baseName} {n}";

        autoCreated++;
        return Db.InsertId(_db, """
            INSERT INTO account(name, type, currency, issuer, mask, created_at)
            VALUES($n, $t, $c, $i, $m, $at);
            """,
            ("$n", name), ("$t", type), ("$c", currency), ("$i", issuer),
            ("$m", string.IsNullOrEmpty(mask) ? (object)DBNull.Value : mask), ("$at", Db.Now()));
    }

    /// <summary>
    /// 卡帳繳款列的「錢從哪來」：這張卡的自動扣繳帳戶 → settings 的預設付款帳戶（兩個都要没歸檔）。
    /// 兩個都拿不出來就回 null —— 宁願把繳款留成 refund（性質錯，但不會動到別的帳戶），
    /// 也不要憑空猜一個帳戶扣它（猜錯就是悄悄讓那個帳戶的餘額少一筆）。
    /// </summary>
    /// <summary>
    /// 繳款列的另一半：同一筆繳款在另一份單子上已經進來的話回那筆的 id，否則 null。
    /// 条件要硬：同一個扣款帳戶、同金額、同幣別、日期差 ≤ 3 天，而且兩邊的文字要提得到同一張卡
    /// （存款單會寫「兆豐卡 兆豐銀行」那种）。同額同日但不是那張卡的支出（實資料就有四筆同額的現金提取）不准被併掉。
    /// </summary>
    long? PaymentTwin(bool incomingIsTransfer, long accountId, long? cardAccountId, long minor, string currency,
                      DateOnly? txnDate, string? merchant)
    {
        if (txnDate is not DateOnly date) return null;             // 沒日期比不得：宁願留兩筆讓校驗去報
        var candidates = new List<(long Id, string Kind, long? To)>();
        using (var cmd = _db.CreateCommand())
        {
            if (incomingIsTransfer)                                 // 卡帳這筆要進來：找存款單那側的支出（或已经併過的轉帳）
                cmd.CommandText = """
                    SELECT e.id, e.kind, e.to_account_id FROM entry e
                    WHERE e.deleted_at IS NULL AND e.source = 'import' AND e.account_id = $a AND e.amount_minor = $amt
                      AND upper(e.currency) = upper($cur)
                      AND ( (e.kind IN ('expense','refund') AND e.to_account_id IS NULL)
                         OR (e.kind = 'transfer' AND e.to_account_id = $card) )
                      AND abs(julianday(e.txn_date) - julianday($d)) <= $tol
                    ORDER BY abs(julianday(e.txn_date) - julianday($d)), e.id;
                    """;
            else                                                    // 存款單這筆要進來：找卡帳那側已经落地的轉帳
                cmd.CommandText = """
                    SELECT e.id, e.kind, e.to_account_id FROM entry e
                    JOIN account c ON c.id = e.to_account_id
                    WHERE e.deleted_at IS NULL AND e.source = 'import' AND e.kind = 'transfer'
                      AND e.account_id = $a AND e.amount_minor = $amt AND upper(e.currency) = upper($cur)
                      AND c.type = 'credit_card'
                      AND abs(julianday(e.txn_date) - julianday($d)) <= $tol
                    ORDER BY abs(julianday(e.txn_date) - julianday($d)), e.id;
                    """;
            foreach (var (n, v) in new (string, object)[] { ("$a", accountId), ("$amt", minor), ("$cur", currency),
                                                           ("$d", Iso(date)), ("$tol", PaymentTwinDays) })
                cmd.Parameters.AddWithValue(n, v);
            if (incomingIsTransfer) cmd.Parameters.AddWithValue("$card", cardAccountId!.Value);
            using var r = cmd.ExecuteReader();
            while (r.Read()) candidates.Add((r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2)));
        }
        if (candidates.Count == 0) return null;

        // 存款單那側的文字要提得到這張卡（「兆豐卡」对「兆豐國際商業銀行 9306」）：那是判准不准的要害。
        var card = incomingIsTransfer ? cardAccountId : candidates[0].To;
        string name = "", issuer = "", mask = "";
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT name, issuer, mask FROM account WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", card!.Value);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                name = r.GetString(0);
                issuer = r.IsDBNull(1) ? "" : r.GetString(1);
                mask = r.IsDBNull(2) ? "" : r.GetString(2);
            }
        }

        foreach (var (id, kind, to) in candidates)
        {
            if (kind == "transfer" && incomingIsTransfer) return id;      // 卡帳那側已经落成轉帳：身份是結構性的，不用比文字
            var side = incomingIsTransfer
                ? Db.Scalar(_db, "SELECT merchant FROM entry WHERE id=$id;", ("$id", id)) as string   // 要比的是存款單那側的文字
                : merchant;                                                                            // 要比的是這笔進來的文字
            if (MentionsCard(side, name, issuer, mask)) return id;
        }
        return null;
    }

    /// <summary>存款單那側的文字提得到這張卡嗎：卡號/帳號最可靠，再來是發卡行全名與其前兩字（「兆豐」）。</summary>
    static bool MentionsCard(string? text, string name, string issuer, string mask)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!string.IsNullOrWhiteSpace(mask) && mask.Length >= 3 && text.Contains(mask, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var key in new[] { issuer, name })
        {
            var k = (key ?? "").Trim();
            if (k.Length >= 2 && (text.Contains(k, StringComparison.OrdinalIgnoreCase)
                               || text.Contains(k[..2], StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    /// <summary>繳款在兩份單子上的日期差：ACH 入帳日跟卡帳上的繳款日常差個一兩天。</summary>
    const int PaymentTwinDays = 3;

    long? PayerOf(long? cardId)
    {
        if (cardId is not null &&
            Db.Scalar(_db, """
                SELECT k.autopay_account_id FROM card k
                JOIN account a ON a.id = k.autopay_account_id AND a.is_archived = 0
                WHERE k.id=$c;
                """, ("$c", cardId.Value)) is long autopay) return autopay;

        var setting = Db.Scalar(_db, "SELECT value FROM settings WHERE key='default_payment_account_id';") as string;
        return setting is not null && long.TryParse(setting, out var configured)
               && Db.Scalar(_db, "SELECT id FROM account WHERE id=$id AND is_archived=0;", ("$id", configured)) is long id
                ? id : null;
    }

    /// <summary>帳戶名（警告文字要用，不能叫人去查 id）。</summary>
    string AccountName(long id) => Db.Scalar(_db, "SELECT name FROM account WHERE id=$id;", ("$id", id)) as string ?? id.ToString();

    /// <summary>繳費單的付款帳戶：settings 指定的 → 最早建的銀行帳戶 → 建一個現金帳戶並講明。</summary>
    long PaymentAccount(List<string> warnings, ref int autoCreated)
    {
        var setting = Db.Scalar(_db, "SELECT value FROM settings WHERE key='default_payment_account_id';");
        // 已歸檔的帳戶不能用：它不在淨值裡，繳費單記進去會悄悄從報表消失（下面兩個 fallback 就有驗）。
        if (setting is string raw && long.TryParse(raw, out var configured)
            && Db.Scalar(_db, "SELECT id FROM account WHERE id=$id AND is_archived=0;", ("$id", configured)) is not null)
            return configured;

        var picked = Db.Scalar(_db, "SELECT id FROM account WHERE type='bank' AND is_archived=0 ORDER BY id LIMIT 1;")
                  ?? Db.Scalar(_db, "SELECT id FROM account WHERE type='cash' AND is_archived=0 ORDER BY id LIMIT 1;")
                  ?? (object?)null;
        if (picked is long existing)
        {
            Db.Exec(_db, "INSERT OR REPLACE INTO settings(key, value) VALUES('default_payment_account_id', $v);", ("$v", existing.ToString()));
            return existing;
        }

        warnings.Add(Loc.T("Imp_NoBankAccount"));
        autoCreated++;
        var newId = Db.InsertId(_db, """
            INSERT INTO account(name, type, currency, created_at) VALUES('現金', 'cash', $c, $at);
            """, ("$c", Db.BaseCurrency(_db)), ("$at", Db.Now()));
        Db.Exec(_db, "INSERT OR REPLACE INTO settings(key, value) VALUES('default_payment_account_id', $v);", ("$v", newId.ToString()));
        return newId;
    }

    /// <summary>
    /// 單子上的幣別原文 → 代碼。三尺碼直接用；「美金」「日圓」這種中文靠 <c>currency.aliases</c> 對映
    /// —— parser 不碰資料庫，所以對映只能坐在匯入端（parser 只負賣把原文交出來）。
    /// 對不上就用基準幣，但要警告：靜靜當成台幣算是這件事最壞的做法。
    /// </summary>
    string EnsureCurrency(string? raw, List<string> warnings)
    {
        var text = (raw ?? "").Trim();
        var baseCcy = Db.BaseCurrency(_db);
        if (text.Length == 0) return baseCcy;                                  // 單子上根本沒寫幣別

        var code = Db.NormalizeCurrencyCode(text);
        // 三尺碼而且全是英文大寫才算代碼（「新台幣」也是三字，用 char.IsLetter 會被誤判成代碼）。
        if (code.Length != 3 || !code.All(c => c >= 'A' && c <= 'Z')) code = ResolveAlias(text) ?? "";
        if (code.Length == 0)
        {
            warnings.Add(Loc.F("Imp_CurrencyUnmatched", text, baseCcy));
            return baseCcy;
        }

        if (Db.Scalar(_db, "SELECT code FROM currency WHERE code=$c;", ("$c", code)) is null)
        {
            warnings.Add(Loc.F("Imp_CurrencyCreated", code));
            Db.Exec(_db, "INSERT INTO currency(code, name, scale) VALUES($c, $c, 2);", ("$c", code));
        }
        return code;
    }

    /// <summary>aliases 存成「美金|美元」；對不上回 null。</summary>
    string? ResolveAlias(string text)
        => Db.Query(_db, "SELECT code || '@@' || aliases FROM currency WHERE aliases IS NOT NULL")
             .Select(o => ((string)o!).Split("@@", 2))
             .FirstOrDefault(kv => kv[1].Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                    .Any(a => a.Equals(text, StringComparison.OrdinalIgnoreCase)))
             ?.FirstOrDefault();

    long? LinkBill(ParsedStatement s, long? cardId, long docId, List<string> warnings)
    {
        if (cardId is null) return null;
        var periodEnd = s.StatementEndDate ?? s.DueDate
            ?? (s.Transactions.Count > 0 ? s.Transactions.Max(t => t.Date) : default);
        if (periodEnd == default) { warnings.Add(Loc.T("Imp_NoStatementEnd")); return null; }

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
        => Sha256(Encoding.UTF8.GetBytes(string.Join('|', s.Issuer,
               string.IsNullOrWhiteSpace(t.Account) ? s.AccountMask : t.Account, t.Card,
               s.StatementEndDate?.ToString("yyyyMMdd") ?? "",
               t.Date == default ? "" : t.Date.ToString("yyyyMMdd"),
               t.PostingDate?.ToString("yyyyMMdd") ?? "", minor, currency, t.Page)));

    /// <summary>
    /// 哈希比對的範圍。要能跳檔案比，前提是哈希裡有**可靠身份**（卡號/帳號/缴費戶號，<see cref="ParsedStatement.AccountMask"/>
    /// 或列上的 <see cref="ParsedTransaction.Account"/>）：卡靠卡＋帳期＋金額＋日期，存款靠帳號，缴費單靠戶號。
    /// 身份拿不出來就退回只在同一份檔案內比 —— 宁願存兩份，也不要悄悄吃掉一筆（跳檔案判到重覆是**不插入**，不是標記）。
    /// 跳檔案的「疑似重複」已由 <see cref="LedgerStore"/> 的 DuplicateGroupKey 只做標記（也只算信用卡）。
    /// 已知代價：身份為空時，同一份單子第二次取得（網銀重下載/補發 —— 這些 PDF 是伺服器即席產生的，bytes 可能不同）
    /// 會整份存兩份、無警告（細部見 dFinance-db-design.md）。
    /// </summary>
    long? FindExisting(string hash, long accountId, long docId, bool crossFile)
    {
        var ps = new List<(string, object?)> { ("$h", hash), ("$a", accountId) };
        var scope = "";
        if (!crossFile) { scope = " AND source_document_id=$doc"; ps.Add(("$doc", docId)); }
        return Db.Scalar(_db,
            "SELECT id FROM entry WHERE source_line_hash=$h AND account_id=$a AND deleted_at IS NULL" + scope + " ORDER BY id LIMIT 1;",
            [.. ps]) is long id ? id : (long?)null;
    }

    /// <summary>同一個範圍已有一模一樣的鍵 → 加出現次數後綴，讓「同天同店刷兩次同額」能存成兩筆。</summary>
    string NextFreeHash(string hash, long accountId, long docId, bool crossFile)
    {
        if (FindExisting(hash, accountId, docId, crossFile) is null) return hash;
        for (int n = 2; n <= 50; n++)
            if (FindExisting($"{hash}#{n}", accountId, docId, crossFile) is null) return $"{hash}#{n}";
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
