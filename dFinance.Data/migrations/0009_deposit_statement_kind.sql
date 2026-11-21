-- 0009：存款帳戶的「存」不再被記成支出。
--
-- 匯入器原本只看金額正負（StatementImporter 舊第 118 行），存款單上正的錢一律進 expense：
-- 工資、利息、退款、轉入在淨值與收支報表裡都算反（存 40,000 → 淨值 -40,000）。
-- 現在照單子來源決定方向：bank / term_deposit 是「存进来的為正（income），扣款為負（expense）」。
--
-- 已經入庫的列照同樣的規則轉過來（只動 source='import' 且帳戶是 bank / term_deposit 的 ——
-- 手動記的、卡帳的、其它種類的帳戶一筆都不動）。
-- 舊匯入器把負數留成 kind='refund' 且金額是负的：在 derived 裡 refund 走「減 amount」，
-- 負負得正 → 一筆提款反而把銀行餘額加上去。現在翻成 expense + 正額，符號才會對。
-- 分類一起清掉：原本是照「支出」標的，方向反了留著就是錯的帳（要重新標有「建議分類」）。
-- source_line_hash 不重算：它照「單子上的金額」算（StatementImporter），重算會讓重匯把同一筆再插一次。

UPDATE entry
SET kind = 'income',
    category_id = NULL
WHERE source = 'import'
  AND amount_minor > 0
  AND kind = 'expense'
  AND account_id IN (SELECT id FROM account WHERE type IN ('bank', 'term_deposit'));

UPDATE entry
SET kind = 'expense',
    amount_minor = -amount_minor,
    category_id = NULL
WHERE source = 'import'
  AND amount_minor < 0
  AND kind = 'refund'
  AND account_id IN (SELECT id FROM account WHERE type IN ('bank', 'term_deposit'));
