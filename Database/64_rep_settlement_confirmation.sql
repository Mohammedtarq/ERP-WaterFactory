/* ============================================================
   تأكيد المخازن لتسوية المندوب: النقد يُرحَّل فورًا عند أمين الصندوق، والجزء المخزني (المرتجع، المجاني، فاتورة المتبقي)
   يبقى «بانتظار تأكيد المخازن» حتى يعدّه أمين المخزن. التسويات السابقة كلها مؤكَّدة.
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */
IF COL_LENGTH('RepSettlements', 'Status') IS NULL
    ALTER TABLE RepSettlements ADD Status NVARCHAR(20) NOT NULL CONSTRAINT DF_RepSettlements_Status DEFAULT N'Confirmed';
GO
IF COL_LENGTH('RepSettlements', 'PendingStockJson') IS NULL
    ALTER TABLE RepSettlements ADD PendingStockJson NVARCHAR(MAX) NULL;
GO
IF COL_LENGTH('RepSettlements', 'ConfirmedByUserId') IS NULL
    ALTER TABLE RepSettlements ADD ConfirmedByUserId INT NULL CONSTRAINT FK_RepSettlements_ConfirmedBy REFERENCES Users(Id);
GO
IF COL_LENGTH('RepSettlements', 'ConfirmedAt') IS NULL
    ALTER TABLE RepSettlements ADD ConfirmedAt DATETIME2 NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_RepSettlements_Status')
    ALTER TABLE RepSettlements ADD CONSTRAINT CK_RepSettlements_Status CHECK (Status IN (N'Confirmed', N'PendingWarehouse'));
GO
