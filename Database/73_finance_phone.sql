-- هاتف المدير المالي: واجهة الصندوق على تطبيق المندوبين (عرض كل الصناديق، والصرف من صندوقه وحده).
-- الإعداد: موظف المدير المالي وصندوقه. وحركة هاتف جديدة: مصروف من الصندوق (BoxExpense). آمن للإعادة.
IF COL_LENGTH('RepAppSettings', 'FinanceEmployeeId') IS NULL
    ALTER TABLE RepAppSettings ADD FinanceEmployeeId INT NULL CONSTRAINT FK_RepAppSettings_FinanceEmployee REFERENCES Employees(Id);
GO
IF COL_LENGTH('RepAppSettings', 'FinanceCashBoxId') IS NULL
    ALTER TABLE RepAppSettings ADD FinanceCashBoxId INT NULL CONSTRAINT FK_RepAppSettings_FinanceCashBox REFERENCES CashBoxes(Id);
GO

-- يوسَّع قيد نوع الحركة في RepRequests بالنوع الجديد
DECLARE @ck SYSNAME, @sql NVARCHAR(400);
DECLARE c CURSOR LOCAL FAST_FORWARD FOR
    SELECT cc.name FROM sys.check_constraints cc
    JOIN sys.columns col ON col.object_id = cc.parent_object_id AND col.column_id = cc.parent_column_id
    WHERE cc.parent_object_id = OBJECT_ID('RepRequests') AND col.name = 'Kind' AND cc.definition NOT LIKE '%BoxExpense%';
OPEN c;
FETCH NEXT FROM c INTO @ck;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql = N'ALTER TABLE RepRequests DROP CONSTRAINT ' + QUOTENAME(@ck);
    EXEC sp_executesql @sql;
    FETCH NEXT FROM c INTO @ck;
END
CLOSE c; DEALLOCATE c;
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_RepRequests_Kind')
    ALTER TABLE RepRequests ADD CONSTRAINT CK_RepRequests_Kind
        CHECK (Kind IN (N'CashSale', N'CreditSale', N'Free', N'Collection', N'NewCustomer', N'Expense', N'Return', N'CustomerLink', N'CustodyExpense', N'BoxExpense'));
GO
