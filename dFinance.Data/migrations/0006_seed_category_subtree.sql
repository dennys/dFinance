-- 0006：把常用分類拆出第三層（parent_id 指向 0002 那批種子），讓分類樹有三層可看。
-- 只新增，不改 0002：0002 已經套用的庫不會重跑，改內容只會讓新庫跟舊庫長得不一樣。
-- kind 一定要跟父類一樣，否則 trg_category_tree_insert 會直接擋。
-- 故意留一些不拆（稅金、保險、其他、兩個收入類）：它們本身就是 leaf，照樣能被標，
-- 報表上也能測「同一層有可展開的、也有不能展開的」。
WITH s(name, kind, parent_name, sort_order) AS (VALUES
    ('外食',       'expense', '餐飲',     110),
    ('超商飲料',   'expense', '餐飲',     120),
    ('外送',       'expense', '餐飲',     130),
    ('油費',       'expense', '交通',     210),
    ('車票',       'expense', '交通',     220),
    ('出租接駁',   'expense', '交通',     230),
    ('網購',       'expense', '購物',     310),
    ('採買',       'expense', '購物',     320),
    ('服飾家居',   'expense', '購物',     330),
    ('電費',       'expense', '水電瓦斯', 410),
    ('水費',       'expense', '水電瓦斯', 420),
    ('瓦斯',       'expense', '水電瓦斯', 430),
    ('行動門號',   'expense', '通訊網路', 510),
    ('固寬網路',   'expense', '通訊網路', 520),
    ('影音訂閱',   'expense', '訂閱軟體', 610),
    ('生產工具',   'expense', '訂閱軟體', 620),
    ('雲端儲存',   'expense', '訂閱軟體', 630),
    ('門診掛號',   'expense', '醫療保健', 710),
    ('藥局藥妝',   'expense', '醫療保健', 720),
    ('房租',       'expense', '居住',    1010),
    ('管理費',     'expense', '居住',    1020)
)
INSERT INTO category (name, kind, parent_id, sort_order, is_system)
SELECT s.name, s.kind, p.id, s.sort_order, 1
FROM s
JOIN category p ON p.name = s.parent_name AND p.parent_id IS NULL AND p.kind = s.kind
WHERE NOT EXISTS (SELECT 1 FROM category c WHERE c.name = s.name AND c.kind = s.kind AND c.parent_id = p.id);
