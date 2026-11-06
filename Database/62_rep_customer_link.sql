/* ============================================================
   طلب «ربط زبائن» من هاتف المندوب: يختار زبائنه من زبائن النظام، ولا يتغير شيء حتى توافق الإدارة.
   يوسّع قيد نوع حركة الهاتف (RepRequests.Kind) بالنوع الجديد CustomerLink.
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */
DECLARE @ck SYSNAME, @sql NVARCHAR(400);
DECLARE c CURSOR LOCAL FAST_FORWARD FOR
    SELECT cc.name FROM sys.check_constraints cc
    JOIN sys.columns col ON col.object_id = cc.parent_object_id AND col.column_id = cc.parent_column_id
    WHERE cc.parent_object_id = OBJECT_ID('RepRequests') AND col.name = 'Kind' AND cc.definition NOT LIKE '%CustomerLink%';
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
        CHECK (Kind IN (N'CashSale', N'CreditSale', N'Free', N'Collection', N'NewCustomer', N'Expense', N'Return', N'CustomerLink'));
GO
