/* ============================================================
   الماكينات ورصيد "تحت التصنيع" لكل ماكينة (قابل لإعادة التنفيذ)
   - لكل ماكينة مخزن داخلي من نوع WorkInProcess يحمل رصيدها تحت التصنيع
   - صرف المواد لأمر إنتاج = نقل من مخازن المواد الأولية إلى تحت تصنيع الماكينة (WipIssue)
   - الاستهلاك الفعلي = قائمة المواد × المُنتَج فعلًا (ProductionConsume)، والتالف من تحت التصنيع (Damaged/Production)
   - المتبقي يبقى رصيدًا مرحّلًا على الماكينة، ويمكن إرجاعه لمخزن المواد (WipReturn)
   ============================================================ */
SET QUOTED_IDENTIFIER ON;
GO

-- نوع مخزن جديد: تحت التصنيع (قيد CHECK الأصلي بلا اسم؛ يُستبدل بقيد مسمّى)
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Warehouses_Type')
BEGIN
    DECLARE @old SYSNAME = (SELECT TOP 1 cc.name FROM sys.check_constraints cc
                            WHERE cc.parent_object_id = OBJECT_ID('Warehouses') AND cc.definition LIKE '%RepVan%');
    IF @old IS NOT NULL EXEC (N'ALTER TABLE Warehouses DROP CONSTRAINT [' + @old + N']');
    ALTER TABLE Warehouses ADD CONSTRAINT CK_Warehouses_Type CHECK (WarehouseType IN (
        N'Main', N'Sub', N'Returns', N'Damaged', N'UnderInspection',
        N'Transit', N'RepVan', N'RawMaterial', N'FinishedGoods', N'WorkInProcess'));
END;
GO

-- نوعا حركة جديدان: صرف لتحت التصنيع، وإرجاع منه
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_StockTransactions_Type' AND definition LIKE '%WipIssue%')
BEGIN
    IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_StockTransactions_Type')
        ALTER TABLE StockTransactions DROP CONSTRAINT CK_StockTransactions_Type;
    ALTER TABLE StockTransactions ADD CONSTRAINT CK_StockTransactions_Type CHECK (TransactionType IN (
        N'Receipt', N'SalesIssue', N'ProductionConsume', N'ProductionOutput',
        N'Packing', N'Transfer', N'Damaged', N'FreeIssue', N'ReturnToWarehouse',
        N'RepLoad', N'RepSale', N'RepFreeSale', N'RepDamaged', N'RepReturn',
        N'SyncConflictAdjustment', N'Issue', N'WipIssue', N'WipReturn'));
END;
GO

IF OBJECT_ID('Machines', 'U') IS NULL
CREATE TABLE Machines (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    Name            NVARCHAR(100)   NOT NULL UNIQUE,
    MachineType     NVARCHAR(100)   NOT NULL,            -- نفخ، تعبئة، تغليف...
    ProductionLine  NVARCHAR(100)   NULL,                -- الخط
    WipWarehouseId  INT             NOT NULL UNIQUE FOREIGN KEY REFERENCES Warehouses(Id),
    Notes           NVARCHAR(300)   NULL,
    IsActive        BIT             NOT NULL DEFAULT 1
);
GO

IF COL_LENGTH('ProductionOrders', 'MachineId') IS NULL
    ALTER TABLE ProductionOrders ADD MachineId INT NULL CONSTRAINT FK_ProductionOrders_Machine FOREIGN KEY REFERENCES Machines(Id);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ProductionOrders_Machine')
    CREATE INDEX IX_ProductionOrders_Machine ON ProductionOrders (MachineId) WHERE MachineId IS NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_StockTransactions_Reference')
    CREATE INDEX IX_StockTransactions_Reference ON StockTransactions (ReferenceTable, ReferenceId) INCLUDE (ItemId, WarehouseId, QuantityBaseUnits, TransactionType);
GO
