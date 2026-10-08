/* ============================================================
   حد التنبيه لكل مخزن (ملاحظة التجربة 2 — قرار المدير: من داخل المخزن)
   - لكل (مخزن، صنف) حدّه الخاص بالقطعة، يُضبط من تبويب «الأرصدة الحالية» في ذلك المخزن،
     ويُقارن برصيد ذلك المخزن وحده.
   - إن لم يُضبط للمخزن حدّ: يبقى «حد التنبيه» في بطاقة الصنف حدًا افتراضيًا في مخزنه الطبيعي
     الرئيسي وحده (أول مخزن مواد أولية للمشترى، وأول مخزن منتج تام للمصنّع) — فلا يضيع ما ضُبط سابقًا.
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */
IF OBJECT_ID('WarehouseItemAlerts', 'U') IS NULL
CREATE TABLE WarehouseItemAlerts (
    Id                  INT             NOT NULL IDENTITY PRIMARY KEY,
    WarehouseId         INT             NOT NULL REFERENCES Warehouses(Id),
    ItemId              INT             NOT NULL REFERENCES Items(Id),
    MinQuantity         DECIMAL(18,3)   NOT NULL CHECK (MinQuantity >= 0),   -- بالقطعة
    UpdatedAt           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedByUserId     INT             NULL REFERENCES Users(Id),
    CONSTRAINT UQ_WarehouseItemAlerts UNIQUE (WarehouseId, ItemId)
);
GO
