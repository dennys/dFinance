-- 0003：USD 對 TWD 匯率先寫死 32.9（使用者 2026-11-18 給的數字）。
-- 匯率自動更新（Yahoo，預設關閉、明確開啟才連）之後再做；那時候這列只是初始值 ——
-- OR IGNORE 是為了讓人在 UI 改過之後，重跑遷移不會被蓋回去。
-- 其它幣別（JPY/CNY/EUR/GBP）不預塞：現在沒有資料會用到，塞了只是假數字。
INSERT OR IGNORE INTO exchange_rate (currency, rate_date, rate, source)
    VALUES ('USD', date('now', 'localtime'), 32.9, 'manual');
