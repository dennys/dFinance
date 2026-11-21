-- 0007：把 0004 那兩個 trigger 重建一次，只為修字。
-- 0004_category_tree.sql 是 Big5 存的，Db.Scripts() 當 UTF-8 讀，RAISE(ABORT, '...') 的訊息就變成
-- 一團取代字元 —— 邏輯沒壞（比較的都是 ASCII），但跨收支方向建子分類時，使用者看到的是亂碼。
-- 0004 已套用的庫不會重跑，所以這裡 DROP + CREATE 同一個 trigger，讓舊庫與新庫收斂到同一份文字。
-- 內容和 0004 的 trigger 邏輯逐字相同，只有訊息文字是正確的 UTF-8 繁體。

DROP TRIGGER IF EXISTS trg_category_tree_insert;
CREATE TRIGGER trg_category_tree_insert BEFORE INSERT ON category
WHEN NEW.parent_id IS NOT NULL
BEGIN
    SELECT RAISE(ABORT, '父分類必須存在且同方向')
    WHERE NOT EXISTS (SELECT 1 FROM category p WHERE p.id = NEW.parent_id AND p.kind = NEW.kind);
END;

DROP TRIGGER IF EXISTS trg_category_tree_update;
CREATE TRIGGER trg_category_tree_update BEFORE UPDATE OF parent_id, kind ON category
BEGIN
    SELECT RAISE(ABORT, '父分類必須存在且同方向，且不能形成循環')
    WHERE (NEW.parent_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM category p WHERE p.id = NEW.parent_id AND p.kind = NEW.kind))
       OR EXISTS (SELECT 1 FROM category ch WHERE ch.parent_id = NEW.id AND ch.kind <> NEW.kind)
       OR (NEW.parent_id IS NOT NULL AND NEW.parent_id IN (
            WITH RECURSIVE sub(id) AS (
                SELECT c.id FROM category c WHERE c.id = NEW.id
                UNION
                SELECT c.id FROM category c JOIN sub s ON c.parent_id = s.id)
            SELECT id FROM sub));
END;
