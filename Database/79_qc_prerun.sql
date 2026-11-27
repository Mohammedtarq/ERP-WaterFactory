/* ============================================================
   فحص المختبر على مسودة أمر الإنتاج قبل بدء التشغيل
   - QCBatchResults.IsPreRun: فحص المسودة (قبل التشغيل). لا يُغني عن فحص ما بعد التشغيل: التعبئة تشترط فحصًا بعد التشغيل
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */
IF COL_LENGTH('QCBatchResults', 'IsPreRun') IS NULL
    ALTER TABLE QCBatchResults ADD IsPreRun BIT NOT NULL CONSTRAINT DF_QCBatchResults_IsPreRun DEFAULT 0;
GO
