/* ============================================================
   تعديل وحذف في الموارد البشرية: إلغاء صرف أجر عامل وقتي بسبب (يبقى ظاهرًا مشطوبًا، وتعود أيامه غير مصروفة).
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */
IF COL_LENGTH('TempWorkerPayments', 'IsVoided') IS NULL
    ALTER TABLE TempWorkerPayments ADD IsVoided BIT NOT NULL CONSTRAINT DF_TempWorkerPayments_IsVoided DEFAULT 0;
IF COL_LENGTH('TempWorkerPayments', 'VoidReason') IS NULL
    ALTER TABLE TempWorkerPayments ADD VoidReason NVARCHAR(300) NULL;
GO
