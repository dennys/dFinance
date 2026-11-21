-- 0005：把幣別層跟台幣解綁（目标是「錢與幣別通用」，UI 國際化不在這期）。
--
-- 1) exchange_rate 明記幣對。舊表只有 currency 一欄，「1 外幣 = ? 台幣」這個語意只活在註解裡 ——
--    基準幣一旦不是台幣，舊數字的意義會靜靜改變。現在 from/to 兩欄寫死在列上，
--    換基準幣只是換一個 where 條件，匯率列一條都不必清、也不會作廢。
-- 2) currency 加 aliases：匯入時「美金」「日圓」這種單子上的原文靠它對映成代碼，
--    不再各寫一份在 parser 裡（dFinance.Parsing 不參考資料庫，parser 只交原文）。
-- 3) 基準幣進 settings（既有 key/value 表，不新增表）。
-- 4) 代碼一律大寫：SQL 的比較是 BINARY，`usd` 從 `e.currency <> a.currency` 那樣的判斷會漏。
CREATE TABLE exchange_rate_new (
    from_ccy   TEXT NOT NULL CHECK (from_ccy = UPPER(from_ccy)),
    to_ccy     TEXT NOT NULL CHECK (to_ccy = UPPER(to_ccy)),
    rate_date  TEXT NOT NULL,
    rate       REAL NOT NULL CHECK (rate > 0),
    source     TEXT NOT NULL DEFAULT 'manual' CHECK (source IN ('manual','yahoo')),
    fetched_at TEXT,                                  -- 什麼時候抓的（rate_date 是報價自己的日期）
    PRIMARY KEY (from_ccy, to_ccy, rate_date, source) -- 同一天 allow 人工與抓取並存：抓的蓋不掉人打的
);
INSERT OR IGNORE INTO exchange_rate_new(from_ccy, to_ccy, rate_date, rate, source)
    SELECT UPPER(currency), 'TWD', rate_date, rate, source FROM exchange_rate;
DROP TABLE exchange_rate;
ALTER TABLE exchange_rate_new RENAME TO exchange_rate;

ALTER TABLE currency ADD COLUMN aliases TEXT;         -- 「美金|美元」，匯入端對映用
INSERT OR REPLACE INTO settings(key, value) VALUES('base_currency', 'TWD');

-- 銀行單子上的寫法跟 ISO 代碼常常對不上（兆豐寫「美金」「日圓」、王道寫「臺幣」）。
-- 這些別名以前各寫一份在 parser 裡；收在這裡，parser 就只負賣交原文，不必查資料庫。
UPDATE currency SET aliases = '台幣|臺幣|新台幣|新臺幣|TW' WHERE code = 'TWD';
UPDATE currency SET aliases = '美金|美元|US'               WHERE code = 'USD';
UPDATE currency SET aliases = '日圓|日幣|JP'               WHERE code = 'JPY';
UPDATE currency SET aliases = '人民幣|陸幣|CN'             WHERE code = 'CNY';
UPDATE currency SET aliases = '歐元|EU'                    WHERE code = 'EUR';
UPDATE currency SET aliases = '英鎊|GB'                    WHERE code = 'GBP';
