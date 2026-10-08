/* ============================================================
   تعديل المشرف لأعداد "تحت التصنيع" (المتبقي / التالف) مع سجل تدقيق كامل — قابل لإعادة التنفيذ
   - المشرف/الأدمن فقط، والسبب إلزامي
   - كل تعديل: القيمة قبل وبعد، من عدّل ومتى، والحركة المخزنية المرتبطة
   ============================================================ */
SET QUOTED_IDENTIFIER ON;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_StockTransactions_Type' AND definition LIKE '%WipAdjust%')
BEGIN
    IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_StockTransactions_Type')
        ALTER TABLE StockTransactions DROP CONSTRAINT CK_StockTransactions_Type;
    ALTER TABLE StockTransactions ADD CONSTRAINT CK_StockTransactions_Type CHECK (TransactionType IN (
        N'Receipt', N'SalesIssue', N'ProductionConsume', N'ProductionOutput',
        N'Packing', N'Transfer', N'Damaged', N'FreeIssue', N'ReturnToWarehouse',
        N'RepLoad', N'RepSale', N'RepFreeSale', N'RepDamaged', N'RepReturn',
        N'SyncConflictAdjustment', N'Issue', N'WipIssue', N'WipReturn', N'WipAdjust'));
END;
GO

IF OBJECT_ID('WipAdjustments', 'U') IS NULL
CREATE TABLE WipAdjustments (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    MachineId           INT             NOT NULL FOREIGN KEY REFERENCES Machines(Id),
    ItemId              INT             NOT NULL FOREIGN KEY REFERENCES Items(Id),
    ProductionOrderId   INT             NULL FOREIGN KEY REFERENCES ProductionOrders(Id),
    Kind                NVARCHAR(20)    NOT NULL CHECK (Kind IN (N'Remaining', N'Damaged')),
    BeforeQuantity      DECIMAL(18,3)   NOT NULL,
    AfterQuantity       DECIMAL(18,3)   NOT NULL,
    Reason              NVARCHAR(300)   NOT NULL,
    ChangedByUserId     INT             NOT NULL FOREIGN KEY REFERENCES Users(Id),
    ChangedAt           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WipAdjustments_Machine')
    CREATE INDEX IX_WipAdjustments_Machine ON WipAdjustments (MachineId, ChangedAt);
GO
