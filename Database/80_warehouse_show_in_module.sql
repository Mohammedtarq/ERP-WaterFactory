/* ============================================================
   مساحة المخزن تظهر في وحدة أخرى (مثل مخزن الإنتاج أو التالف في قسم الإنتاج) بدل المخازن
   - Warehouses.ShowInModule: '' = وحدة المخازن (الافتراضي)، أو رمز وحدة (Production)
   لا تغيير في المخزون ولا الصلاحيات الأصلية للمخزن.
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */
IF COL_LENGTH('Warehouses', 'ShowInModule') IS NULL
    ALTER TABLE Warehouses ADD ShowInModule NVARCHAR(30) NOT NULL CONSTRAINT DF_Warehouses_ShowInModule DEFAULT N'';
GO
