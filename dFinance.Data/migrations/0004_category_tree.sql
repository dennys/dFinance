-- 0004：分類改成任意深度的樹（parent_id 自引用）。
-- SQLite 的 UNIQUE 不擋 NULL，頂層（parent_id IS NULL）同名會重複，所以另建 expression index。
CREATE UNIQUE INDEX ux_category_name ON category(COALESCE(parent_id, 0), kind, name);

-- 父子必須同方向；不准指向自己或自己的後代（循環）。
CREATE TRIGGER trg_category_tree_insert BEFORE INSERT ON category
WHEN NEW.parent_id IS NOT NULL
BEGIN
    SELECT RAISE(ABORT, '父分類必須存在且同方向')
    WHERE NOT EXISTS (SELECT 1 FROM category p WHERE p.id = NEW.parent_id AND p.kind = NEW.kind);
END;

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
