/* ============================================================
   صوت الإشعارات (نغمة خفيفة عند وصول تنبيه جديد في الجرس): تفضيل لكل مستخدم، مفعّل افتراضيًا.
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */
IF COL_LENGTH('Users', 'AlertSound') IS NULL
    ALTER TABLE Users ADD AlertSound BIT NOT NULL CONSTRAINT DF_Users_AlertSound DEFAULT 1;
GO
