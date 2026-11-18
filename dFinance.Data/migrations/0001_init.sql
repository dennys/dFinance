-- dFinance 初始結構。
-- 型別一律用 SQLite 真正的四個原始型別（INTEGER/TEXT/REAL/BLOB）：
-- DECIMAL/VARCHAR/BOOLEAN/TIMESTAMP 這些寫了不會生效（只是註解），所以約束一律靠 CHECK。
-- 錢一律 *_minor 結尾，單位 = 1/100 元（見 Money.cs）。日期一律 TEXT 'yyyy-MM-dd'。
-- 外鍵強制執行要靠連線（Foreign Keys=True），腳本裡這行只是提醒。

CREATE TABLE migration (
    version     INTEGER PRIMARY KEY,
    name        TEXT NOT NULL,
    applied_at  TEXT NOT NULL
);

CREATE TABLE settings (
    key    TEXT PRIMARY KEY,
    value  TEXT NOT NULL
);

CREATE TABLE currency (
    code   TEXT PRIMARY KEY,                                  -- ISO 四碼，如 TWD
    name   TEXT NOT NULL,
    scale  INTEGER NOT NULL DEFAULT 2 CHECK (scale BETWEEN 0 AND 4)   -- 只影響顯示小數位
);

CREATE TABLE member (
    id          INTEGER PRIMARY KEY,
    name        TEXT NOT NULL UNIQUE,
    is_archived INTEGER NOT NULL DEFAULT 0 CHECK (is_archived IN (0, 1)),
    created_at  TEXT NOT NULL
);

CREATE TABLE account (
    id                  INTEGER PRIMARY KEY,
    name                TEXT NOT NULL UNIQUE,
    type                TEXT NOT NULL CHECK (type IN ('cash', 'bank', 'wallet', 'credit_card', 'term_deposit', 'investment', 'loan', 'asset')),
    currency            TEXT NOT NULL DEFAULT 'TWD',
    issuer              TEXT,                        -- 發行機構：聯邦商銀 / 台灣電力公司 / JKO Pay
    mask                TEXT,                        -- 帳號或卡號末幾碼
    opening_balance_minor INTEGER NOT NULL DEFAULT 0,
    opening_date        TEXT,
    is_archived         INTEGER NOT NULL DEFAULT 0 CHECK (is_archived IN (0, 1)),
    created_at          TEXT NOT NULL
);
CREATE INDEX ix_account_issuer_mask ON account(issuer, mask);   -- 匯入比對用；不做 UNIQUE（同末四碼可能帳戶+卡並存）

CREATE TABLE category (
    id         INTEGER PRIMARY KEY,
    parent_id  INTEGER REFERENCES category(id),
    name       TEXT NOT NULL,
    kind       TEXT NOT NULL CHECK (kind IN ('income', 'expense')),
    sort_order INTEGER NOT NULL DEFAULT 0,
    is_system  INTEGER NOT NULL DEFAULT 0 CHECK (is_system IN (0, 1)),
    UNIQUE (parent_id, kind, name)
);

CREATE TABLE document (
    id               INTEGER PRIMARY KEY,
    sha256           TEXT NOT NULL UNIQUE,           -- 同一份檔重匯直接擋掉
    file_path        TEXT NOT NULL,                  -- 匯入時的來源路徑
    file_name        TEXT NOT NULL,                  -- 給人在介面上看的原名（檔存檔名不用它）
    archived_path    TEXT,                           -- 留底的 PDF 副本（原樣存，含原本的密碼保護）
    imported_at      TEXT NOT NULL,
    issuer           TEXT,
    parser           TEXT NOT NULL,                  -- 哪個 parser 解析的
    parser_ver       INTEGER NOT NULL,               -- 改版後能問「哪些列是舊版解析的」
    account_mask     TEXT,
    statement_start  TEXT,
    statement_end    TEXT,
    due_date         TEXT,
    page_count       INTEGER,
    skipped_zero     INTEGER NOT NULL DEFAULT 0,     -- 跳過的金額 0 列（水費退款抵掉那种）
    warnings         TEXT,                           -- JSON 陣字串，原封不動存 parser 的警告
    superseded_at    TEXT,                           -- 同一帳期被較新的匯入取代
    is_deleted       INTEGER NOT NULL DEFAULT 0 CHECK (is_deleted IN (0, 1))
);
CREATE INDEX ix_document_issuer ON document(issuer, statement_end);

CREATE TABLE card (
    id                 INTEGER PRIMARY KEY,
    account_id         INTEGER NOT NULL REFERENCES account(id),
    mask               TEXT,
    card_name          TEXT,
    autopay_account_id INTEGER REFERENCES account(id),   -- 自動扣繳的銀行帳戶
    credit_limit_minor INTEGER,
    is_active          INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
    UNIQUE (account_id, mask)
);

CREATE TABLE credit_card_bill (
    id                      INTEGER PRIMARY KEY,
    card_id                 INTEGER NOT NULL REFERENCES card(id),
    period_start            TEXT,
    period_end              TEXT NOT NULL,
    due_date                TEXT,
    total_due_minor         INTEGER,
    minimum_due_minor       INTEGER,
    statement_document_id   INTEGER REFERENCES document(id),
    paid_at                 TEXT,
    paid_minor              INTEGER,
    UNIQUE (card_id, period_end)                      -- 同帳期只留一份；重匯走「取代」
);

-- 所有金流統一一張表。記帳基準日是 txn_date（消費日）。
CREATE TABLE entry (
    id                  INTEGER PRIMARY KEY,
    kind                TEXT NOT NULL CHECK (kind IN ('income', 'expense', 'transfer', 'refund', 'adjustment')),
    account_id          INTEGER NOT NULL REFERENCES account(id),
    to_account_id       INTEGER REFERENCES account(id),    -- 只有 transfer 用
    category_id         INTEGER REFERENCES category(id),   -- 匯入時通常是 NULL，等人工或 rule 補
    member_id           INTEGER REFERENCES member(id),
    card_id             INTEGER REFERENCES card(id),
    bill_id             INTEGER REFERENCES credit_card_bill(id),
    currency            TEXT NOT NULL DEFAULT 'TWD',
    amount_minor        INTEGER NOT NULL,
    orig_amount_minor   INTEGER,                      -- 海外消費的原幣金額（只做顯示）
    orig_currency       TEXT,
    txn_date            TEXT,                         -- 可 NULL：解析不到日期時不准塞 0001-01-01
    posting_date        TEXT,
    merchant            TEXT,
    note                TEXT,
    status              TEXT NOT NULL DEFAULT 'imported' CHECK (status IN ('cleared', 'imported', 'pending', 'ignored', 'duplicate')),
    source              TEXT NOT NULL DEFAULT 'import' CHECK (source IN ('import', 'manual')),
    source_document_id  INTEGER REFERENCES document(id),
    source_page         INTEGER,
    source_line_hash    TEXT,                         -- 去重與重匯比對的穩定鍵
    source_raw          TEXT,                         -- 原始那行文字，核對用
    edited_at           TEXT,                         -- 有值 = 人工改過，重匯時只准更新 source_raw
    orphaned_at         TEXT,                         -- 新版解析不再出現（可能是漏解析，不准刪）
    created_at          TEXT NOT NULL,
    updated_at          TEXT,
    deleted_at          TEXT,
    CHECK (amount_minor > 0 OR kind IN ('refund', 'adjustment')),
    CHECK ((kind = 'transfer') = (to_account_id IS NOT NULL)),
    CHECK (kind <> 'transfer' OR category_id IS NULL),
    CHECK (txn_date IS NULL OR txn_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'),
    CHECK (posting_date IS NULL OR posting_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]')
);
CREATE UNIQUE INDEX ux_entry_line ON entry(source_document_id, source_page, source_line_hash)
    WHERE source_line_hash IS NOT NULL;
CREATE INDEX ix_entry_date ON entry(txn_date);
CREATE INDEX ix_entry_account_date ON entry(account_id, txn_date);
CREATE INDEX ix_entry_category ON entry(category_id);
CREATE INDEX ix_entry_document ON entry(source_document_id);

-- 帳單餘額快照。「既有交易累加 == 最近一次快照」是偵測 parser 漏列的校驗。
CREATE TABLE account_balance (
    account_id    INTEGER NOT NULL REFERENCES account(id),
    as_of         TEXT NOT NULL,
    currency      TEXT NOT NULL DEFAULT 'TWD',
    balance_minor INTEGER NOT NULL,
    source        TEXT NOT NULL CHECK (source IN ('statement', 'manual')),
    PRIMARY KEY (account_id, as_of, currency)
);

-- 先建表。這一期沒有匯入來源（Yahoo Finance 之後再做，且要使用者明確開啟才連網）。
CREATE TABLE exchange_rate (
    currency  TEXT NOT NULL,
    rate_date TEXT NOT NULL,
    rate      REAL NOT NULL,
    source    TEXT NOT NULL DEFAULT 'manual' CHECK (source IN ('manual', 'yahoo')),
    PRIMARY KEY (currency, rate_date)
);

INSERT INTO currency (code, name, scale) VALUES
    ('TWD', '新台幣', 0), ('USD', '美元', 2), ('JPY', '日圓', 0),
    ('CNY', '人民幣', 2), ('EUR', '歐元', 2), ('GBP', '英鎊', 2);
