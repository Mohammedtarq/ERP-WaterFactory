/* ============================================================
   تقسيم تسوية المندوب: أمين المخزن يسوّي المنتجات (تُرحَّل فورًا) والمالية تسوّي النقد لاحقًا —
   حالة جديدة PendingCash = «منتجات سُوّيت، بانتظار النقد». قابل لإعادة التنفيذ بأمان.
   ============================================================ */
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_RepSettlements_Status' AND definition NOT LIKE N'%PendingCash%')
    ALTER TABLE RepSettlements DROP CONSTRAINT CK_RepSettlements_Status;
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_RepSettlements_Status')
    ALTER TABLE RepSettlements ADD CONSTRAINT CK_RepSettlements_Status CHECK (Status IN (N'Confirmed', N'PendingWarehouse', N'PendingCash'));
GO
