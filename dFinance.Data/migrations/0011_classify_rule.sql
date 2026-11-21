-- 0011：商家→分類 建議子系統（CLASSIFICATION.md）。三件事：規則表、補 catch-all leaf、種子規則。
-- 只能新增：已套用的遷移檔不會重跑，改內容只會讓既有庫跟新庫長得不一樣。
-- 全部 WHERE NOT EXISTS（照 0008 的寫法），重複跑不會塞重。
--
-- 為什麼規則要落庫、還要比對時直接指 leaf id：
--   舊的靜態關鍵字表回的是「頂層名」，而 entry 只能標最底層 ⇒ 八個已拆子的頂層全部認到了也被靜默丟掉
--   （CLASSIFICATION.md B2）。存 id 不存名字：改名/挪層不會無聲失效（B3）。
-- kind 欄（expense/income/any）：沒有它，「薪資 → 薪資收入／本薪」會去命中同名的支出列，
--   而 LedgerStore.EnsureLeaf 只擋父類不擋方向 ⇒ 分類報表的塊小計與「收−支」總計行會互相對不上（B7）。
-- FK 是寫给人看的：本專案沒開 PRAGMA foreign_keys（開了會改變 0001 那批表的行為），
--   所以目標分類被刪掉是靠 MerchantClassifier 回 BrokenRule(CategoryGone) 擋，不是靠它。
CREATE TABLE IF NOT EXISTS classify_rule (
    id          INTEGER PRIMARY KEY,
    pattern     TEXT    NOT NULL,
    match       TEXT    NOT NULL CHECK (match IN ('contains','exact','glob','ignore')),
    category_id INTEGER REFERENCES category(id),
    priority    INTEGER NOT NULL DEFAULT 0,
    enabled     INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0,1)),
    source      TEXT    NOT NULL DEFAULT 'user' CHECK (source IN ('seed','user','advisor')),
    kind        TEXT    NOT NULL DEFAULT 'expense' CHECK (kind IN ('expense','income','any')),
    note        TEXT,
    created_at  TEXT    NOT NULL,
    updated_at  TEXT,
    CHECK ((match = 'ignore') = (category_id IS NULL))                 -- ignore 詞不帶分類
);
CREATE INDEX IF NOT EXISTS ix_classify_rule ON classify_rule(enabled, priority, id);

-- ---------------------------------------------------------------- 補 leaf
-- 八個 catch-all：規則認得出頂層、但認不到子類時要有落點，否則那一筆又變成人要自己想。
-- 「餐飲／其他餐飲」不叫「餐飲／餐飲」：報表展開後父列與子列會印兩個「餐飲」，跟小計列難以區分（決定 1）。
-- 停車費不是 catch-all，是指定項：現有 油費/車票/出租接駁 都不是停車。
-- sort_order 接在 0006 既有段號後面（餐飲 110-130、交通 210-230、⋯、居住 1010-1020）。
WITH s(name, parent_name, sort_order) AS (VALUES
    ('其他餐飲', '餐飲',     140),
    ('停車費',   '交通',     240),
    ('其他',     '購物',     340),
    ('其他',     '水電瓦斯', 440),
    ('其他',     '通訊網路', 530),
    ('其他',     '訂閱軟體', 640),
    ('其他',     '醫療保健', 730),
    ('其他',     '居住',    1030)
)
INSERT INTO category (name, kind, parent_id, sort_order, is_system)
SELECT s.name, 'expense', p.id, s.sort_order, 1
FROM s
JOIN category p ON p.name = s.parent_name AND p.parent_id IS NULL AND p.kind = 'expense'
WHERE NOT EXISTS (SELECT 1 FROM category c WHERE c.name = s.name AND c.kind = 'expense' AND c.parent_id = p.id);

-- ---------------------------------------------------------------- 種子規則
-- priority 小的先贏（取代舊代碼「寫在陣列第幾行」）。兩個不是 coincidence 的數字：
--   100 = 瓦斯：「代繳瓦斯費－中油 ⋯」同時含「瓦斯」跟「中油」，那是代繳瓦斯費，不是去加油站（使用者拍板）。
--   150 = 外送：「Uber Eats」含「Uber」，不排前面會被「出租接駁」搶走。
-- 「瓦斯費」不再另外寫一條給居住：它照舊被「瓦斯」判到水電瓦斯，那才是對的（舊表把它放居住是錯的）。
-- 「?險」這條先刪：舊表拿它當 contains 關鍵字，「?」是實心字標點 ⇒ 永不中（B1）。要留到種子補漏那輪用 glob 重寫。
WITH RECURSIVE cp(id, path, kind) AS (
    SELECT id, CAST(name AS TEXT), kind FROM category WHERE parent_id IS NULL
    UNION ALL
    SELECT c.id, cp.path || '／' || c.name, c.kind FROM category c JOIN cp ON c.parent_id = cp.id),
s(pattern, match, target, kind, priority, note) AS (VALUES
    ('瓦斯',       'contains', '水電瓦斯／瓦斯',       'expense', 100, '要先贏過「中油」：代繳瓦斯費單上也會印中油'),
    ('天然氣',     'contains', '水電瓦斯／瓦斯',       'expense', 100, NULL),
    ('UberEats',   'contains', '餐飲／外送',           'expense', 150, '要先贏過「Uber」'),
    ('Uber Eats',  'contains', '餐飲／外送',           'expense', 150, NULL),
    ('Foodpanda',  'contains', '餐飲／外送',           'expense', 150, '舊表那條「Foodpanda外送交通」是不會存在的商家名（B4）'),
    ('中油',       'contains', '交通／油費',           'expense', 200, NULL),
    ('台灣中油',   'contains', '交通／油費',           'expense', 200, NULL),
    ('加油',       'contains', '交通／油費',           'expense', 200, NULL),
    ('停車',       'contains', '交通／停車費',         'expense', 200, '🆕'),
    ('悠遊卡',     'contains', '交通／車票',           'expense', 200, NULL),
    ('一卡通',     'contains', '交通／車票',           'expense', 200, '也是支付渠道名，但 keyword 贏 ignore'),
    ('高鐵',       'contains', '交通／車票',           'expense', 200, NULL),
    ('台鐵',       'contains', '交通／車票',           'expense', 200, NULL),
    ('臺鐵',       'contains', '交通／車票',           'expense', 200, NULL),
    ('捷運',       'contains', '交通／車票',           'expense', 200, NULL),
    ('Uber',       'contains', '交通／出租接駁',       'expense', 200, NULL),
    ('Taxi',       'contains', '交通／出租接駁',       'expense', 200, NULL),
    ('計程車',     'contains', '交通／出租接駁',       'expense', 200, NULL),
    ('麥當勞',     'contains', '餐飲／外食',           'expense', 200, NULL),
    ('肯德基',     'contains', '餐飲／外食',           'expense', 200, NULL),
    ('星巴克',     'contains', '餐飲／外食',           'expense', 200, NULL),
    ('咖啡',       'contains', '餐飲／外食',           'expense', 200, NULL),
    ('CAFE',       'contains', '餐飲／外食',           'expense', 200, '正規化後 CAFÉ 也是 cafe（B6）'),
    ('SUKIYA',     'contains', '餐飲／外食',           'expense', 200, '🆕 全形「ＳＵＫＩＹＡ」正規化後才比得到（本機 3 筆）'),
    ('食其家',     'contains', '餐飲／外食',           'expense', 200, '🆕'),
    ('餐廳',       'contains', '餐飲／外食',           'expense', 200, NULL),
    ('小吃',       'contains', '餐飲／外食',           'expense', 200, NULL),
    ('便當',       'contains', '餐飲／外食',           'expense', 200, NULL),
    ('麵',         'contains', '餐飲／外食',           'expense', 200, NULL),
    ('飯',         'contains', '餐飲／外食',           'expense', 200, NULL),
    ('火鍋',       'contains', '餐飲／外食',           'expense', 200, NULL),
    ('7-ELEVEN',   'contains', '餐飲／超商飲料',       'expense', 200, '7-11 是另一條：正規化把標點抹掉後兩者同鍵'),
    ('7-11',       'contains', '餐飲／超商飲料',       'expense', 200, NULL),
    ('統一超商',   'contains', '餐飲／超商飲料',       'expense', 200, '🆕 舊表完全不中（本機 16 筆）'),
    ('全家',       'contains', '餐飲／超商飲料',       'expense', 200, NULL),
    ('萊爾富',     'contains', '餐飲／超商飲料',       'expense', 200, NULL),
    ('OK超商',     'contains', '餐飲／超商飲料',       'expense', 200, NULL),
    ('超商',       'contains', '餐飲／超商飲料',       'expense', 200, NULL),
    ('路易莎',     'contains', '餐飲／超商飲料',       'expense', 200, NULL),
    ('台電',       'contains', '水電瓦斯／電費',       'expense', 200, NULL),
    ('臺灣電力',   'contains', '水電瓦斯／電費',       'expense', 200, NULL),
    ('台水',       'contains', '水電瓦斯／水費',       'expense', 200, NULL),
    ('臺灣自來水', 'contains', '水電瓦斯／水費',       'expense', 200, NULL),
    ('中華電信',   'contains', '通訊網路／行動門號',   'expense', 200, NULL),
    ('CHT',        'contains', '通訊網路／行動門號',   'expense', 200, '🆕 單上只印縮寫（本機 2 筆）'),
    ('遠傳',       'contains', '通訊網路／行動門號',   'expense', 200, NULL),
    ('台灣大哥大', 'contains', '通訊網路／行動門號',   'expense', 200, NULL),
    ('亞太電信',   'contains', '通訊網路／行動門號',   'expense', 200, NULL),
    ('Hinet',      'contains', '通訊網路／固寬網路',   'expense', 200, NULL),
    ('Netflix',    'contains', '訂閱軟體／影音訂閱',   'expense', 200, '舊表那條「Netflix網路」放在通訊網路（B4）'),
    ('Spotify',    'contains', '訂閱軟體／影音訂閱',   'expense', 200, NULL),
    ('YouTube',    'contains', '訂閱軟體／影音訂閱',   'expense', 200, NULL),
    ('Disney',     'contains', '訂閱軟體／影音訂閱',   'expense', 200, NULL),
    ('Apple.com/bill', 'contains', '訂閱軟體／生產工具', 'expense', 200, NULL),
    ('APPLE.COM',  'contains', '訂閱軟體／生產工具',   'expense', 200, NULL),
    ('Google',     'contains', '訂閱軟體／生產工具',   'expense', 200, NULL),
    ('Microsoft',  'contains', '訂閱軟體／生產工具',   'expense', 200, NULL),
    ('OpenAI',     'contains', '訂閱軟體／生產工具',   'expense', 200, NULL),
    ('ChatGPT',    'contains', '訂閱軟體／生產工具',   'expense', 200, NULL),
    ('Adobe',      'contains', '訂閱軟體／生產工具',   'expense', 200, NULL),
    ('Notion',     'contains', '訂閱軟體／生產工具',   'expense', 200, NULL),
    ('iCloud',     'contains', '訂閱軟體／雲端儲存',   'expense', 200, NULL),
    ('Dropbox',    'contains', '訂閱軟體／雲端儲存',   'expense', 200, NULL),
    ('診所',       'contains', '醫療保健／門診掛號',   'expense', 200, NULL),
    ('醫院',       'contains', '醫療保健／門診掛號',   'expense', 200, NULL),
    ('掛號',       'contains', '醫療保健／門診掛號',   'expense', 200, NULL),
    ('藥局',       'contains', '醫療保健／藥局藥妝',   'expense', 200, '屈臣氏/康是美 是藥妝不是服飾（使用者拍板）'),
    ('藥妝',       'contains', '醫療保健／藥局藥妝',   'expense', 200, NULL),
    ('康是美',     'contains', '醫療保健／藥局藥妝',   'expense', 200, NULL),
    ('屈臣氏',     'contains', '醫療保健／藥局藥妝',   'expense', 200, NULL),
    ('丁丁藥局',   'contains', '醫療保健／藥局藥妝',   'expense', 200, NULL),
    ('momo',       'contains', '購物／網購',           'expense', 200, NULL),
    ('蝦皮',       'contains', '購物／網購',           'expense', 200, NULL),
    ('Shopee',     'contains', '購物／網購',           'expense', 200, NULL),
    ('PChome',     'contains', '購物／網購',           'expense', 200, NULL),
    ('博客來',     'contains', '購物／網購',           'expense', 200, NULL),
    ('Amazon',     'contains', '購物／網購',           'expense', 200, 'ignore 詞「A-」不能把開頭的 a 剝掉（amazon 會變 mazon）'),
    ('Yahoo購物',  'contains', '購物／網購',           'expense', 200, NULL),
    ('Costco',     'contains', '購物／採買',           'expense', 200, NULL),
    ('好市多',     'contains', '購物／採買',           'expense', 200, NULL),
    ('全聯',       'contains', '購物／採買',           'expense', 200, NULL),
    ('家樂福',     'contains', '購物／採買',           'expense', 200, NULL),
    ('大潤發',     'contains', '購物／採買',           'expense', 200, NULL),
    ('愛買',       'contains', '購物／採買',           'expense', 200, NULL),
    ('IKEA',       'contains', '購物／服飾家居',       'expense', 200, NULL),
    ('宜家',       'contains', '購物／服飾家居',       'expense', 200, NULL),
    ('Uniqlo',     'contains', '購物／服飾家居',       'expense', 200, NULL),
    ('房租',       'contains', '居住／房租',           'expense', 200, NULL),
    ('物業',       'contains', '居住／房租',           'expense', 200, NULL),
    ('管理費',     'contains', '居住／管理費',         'expense', 200, NULL),
    ('保險',       'contains', '保險',                 'expense', 200, '頂層就是 leaf'),
    ('人壽',       'contains', '保險',                 'expense', 200, NULL),
    ('產險',       'contains', '保險',                 'expense', 200, NULL),
    ('稅',         'contains', '稅金',                 'expense', 200, '頂層就是 leaf；income 側的「退稅」不會被它搶走（kind）'),
    ('罰鍰',       'contains', '稅金',                 'expense', 200, NULL),
    ('規費',       'contains', '稅金',                 'expense', 200, NULL),
    ('薪資',       'contains', '薪資收入／本薪',       'income',  200, '🆕 income 那側舊表零規則（B8，本機 2 筆）'),
    ('利息',       'contains', '利息收入',             'income',  200, '🆕 本機 2 筆')
)
INSERT INTO classify_rule (pattern, match, category_id, priority, enabled, source, kind, note, created_at)
SELECT s.pattern, s.match, cp.id, s.priority, 1, 'seed', s.kind, s.note, strftime('%Y-%m-%d %H:%M:%S','now','localtime')
FROM s
JOIN cp ON cp.path = s.target AND cp.kind = s.kind
-- 比對用的鍵由來是「比較的那兩邊過同一把尺」：這裡也照正規化後的鍵查，才不會有人把「7-ELEVEN」改過又插一條重複的。
WHERE NOT EXISTS (SELECT 1 FROM classify_rule r
                  WHERE r.pattern = s.pattern AND r.match = s.match AND r.category_id = cp.id);

-- 種子漏了目標分類的（分類被砍過、或名字打錯）照樣進不去，但要看得見：測試 ClassificationDbTests 會查全部種子。

-- ---------------------------------------------------------------- 種子 ignore 詞
-- 這些是「怎麼付的」，不是「跟誰買」：既不該產生建議，也不該讓同一間店拆成好幾列（本機實測筆數）。
-- 只在開頭或分隔符之後剝（它只負責剝前綴）；跟 keyword 撞字時 keyword 贏 —— 「一卡通」同時是交通關鍵字與渠道名。
-- 「～」「＊」不進種子：比對前標點就被正規化抹掉了，寫進來是一條永遠不動的死規則。
WITH s(pattern) AS (VALUES
    ('代收'), ('全盈支付'), ('連加'), ('網際轉'), ('信用卡'), ('現金'),
    ('街口電支'), ('街口支付'), ('電支'), ('全支付'), ('ＴＷＱＲ'), ('ＡＴＭ'), ('A-')
)
INSERT INTO classify_rule (pattern, match, category_id, priority, enabled, source, kind, note, created_at)
SELECT s.pattern, 'ignore', NULL, 900, 1, 'seed', 'any', NULL, strftime('%Y-%m-%d %H:%M:%S','now','localtime')
FROM s
WHERE NOT EXISTS (SELECT 1 FROM classify_rule r WHERE r.pattern = s.pattern AND r.match = 'ignore');
