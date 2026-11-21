-- 0008：收入那一側補一層。0002 只塞了「利息收入」「業外收入」兩個頂層，薪資沒地方放，
-- 分類報表的 income 那一側就只有兩個類可以看（expense 那邊 0006 已經拆過第三層）。
-- 只能新增：0002 已套用的庫不會重跑，這裡也用 WHERE NOT EXISTS，重複跑不會塞重。
-- 分兩段寫：同一條 INSERT 的 JOIN 看不到自己剛插進去的那筆，所以頂層先插、子類下一條才接得上。
INSERT INTO category (name, kind, parent_id, sort_order, is_system)
SELECT '薪資收入', 'income', NULL, 5, 1
WHERE NOT EXISTS (SELECT 1 FROM category c WHERE c.name = '薪資收入' AND c.kind = 'income' AND c.parent_id IS NULL);

WITH s(name, sort_order) AS (VALUES
    ('本薪', 510),
    ('獎金', 520)
)
INSERT INTO category (name, kind, parent_id, sort_order, is_system)
SELECT s.name, 'income', p.id, s.sort_order, 1
FROM s
JOIN category p ON p.name = '薪資收入' AND p.kind = 'income' AND p.parent_id IS NULL
WHERE NOT EXISTS (SELECT 1 FROM category c WHERE c.name = s.name AND c.kind = 'income' AND c.parent_id = p.id);
