/* ============================================================
   رقم الدفعة (التشغيلة) لأوامر الإنتاج: يُولَّد تلقائيًا ويمكن تعديله، مع حفظ الرقم الأصلي
   وسجل كامل بكل تعديل (من غيّره ومتى)، وتفرّد أرقام دفعات الإنتاج — قابل لإعادة التنفيذ
   ============================================================ */
SET QUOTED_IDENTIFIER ON;
GO

IF COL_LENGTH('ItemBatches', 'OriginalBatchNumber') IS NULL
    ALTER TABLE ItemBatches ADD OriginalBatchNumber NVARCHAR(50) NULL;   -- الرقم المولَّد أول مرة (يُملأ عند أول تعديل)
GO

IF OBJECT_ID('BatchNumberChanges', 'U') IS NULL
CREATE TABLE BatchNumberChanges (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    BatchId             INT             NOT NULL FOREIGN KEY REFERENCES ItemBatches(Id),
    OldNumber           NVARCHAR(50)    NOT NULL,
    NewNumber           NVARCHAR(50)    NOT NULL,
    Reason              NVARCHAR(300)   NULL,
    ChangedByUserId     INT             NOT NULL FOREIGN KEY REFERENCES Users(Id),
    ChangedAt           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_BatchNumberChanges_Batch')
    CREATE INDEX IX_BatchNumberChanges_Batch ON BatchNumberChanges (BatchId);
GO

-- رقم دفعة الإنتاج فريد على مستوى النظام كله (المختبر يربط النتائج بالرقم وحده)
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_ItemBatches_ProductionNumber')
    CREATE UNIQUE INDEX UX_ItemBatches_ProductionNumber ON ItemBatches (BatchNumber) WHERE ProductionOrderId IS NOT NULL;
GO
