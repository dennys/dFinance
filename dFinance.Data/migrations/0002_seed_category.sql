-- 0002：內建分類種子。
-- 0001 只塞了 currency，結果「改分類」是改一個空下拉（全 repo 沒有任何地方寫 category）。
-- 使用者自己新增的分類是 is_system=0，這批是 1（以後做「重設分類」時只動 is_system=1 的）。
INSERT INTO category (name, kind, sort_order, is_system) VALUES
    ('餐飲', 'expense',  10, 1),
    ('交通', 'expense',  20, 1),
    ('購物', 'expense',  30, 1),
    ('水電瓦斯', 'expense', 40, 1),
    ('通訊網路', 'expense', 50, 1),
    ('訂閱軟體', 'expense', 60, 1),
    ('醫療保健', 'expense', 70, 1),
    ('保險', 'expense',  80, 1),
    ('稅金', 'expense',  90, 1),
    ('居住', 'expense', 100, 1),
    ('其他', 'expense', 990, 1),
    ('利息收入', 'income', 10, 1),
    ('業外收入', 'income', 990, 1);
