/* ============================================================
   المزامنة تُفعَّل وحدها على حاسبة السيرفر: خدمة المزامنة (التي ركّبها المثبّت) تفعّل الخادم المحلي لكل مشروع مرة واحدة،
   ثم تحترم ما يختاره المستخدم بعدها. AutoSetupDone = تمّ هذا التفعيل التلقائي (أو حسم المستخدم الأمر بنفسه).
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */
IF COL_LENGTH('CloudSyncSettings', 'AutoSetupDone') IS NULL
    ALTER TABLE CloudSyncSettings ADD AutoSetupDone BIT NOT NULL CONSTRAINT DF_CloudSync_AutoSetupDone DEFAULT 0;
GO
