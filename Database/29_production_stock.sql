/* ============================================================
   المرحلة م2: الإنتاج والمخازن
   - الجرد السريع: عدّ فعلي بالوحدات الكبيرة (باليت + كرتون + قطع) ← فرق الجرد بالكلفة، منفصلًا عن التلف
   - قائمة جهات المسحوب المجاني بتصنيف ثابت (لتقرير شهري لكل جهة)
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */

IF OBJECT_ID('StockCounts', 'U') IS NULL
CREATE TABLE StockCounts (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    CountNumber     NVARCHAR(30)    NOT NULL UNIQUE,
    WarehouseId     INT             NOT NULL FOREIGN KEY REFERENCES Warehouses(Id),
    CountDate       DATE            NOT NULL,
    Notes           NVARCHAR(400)   NULL,
    ShortageValue   DECIMAL(18,2)   NOT NULL DEFAULT 0,      -- قيمة النقص بالكلفة
    SurplusValue    DECIMAL(18,2)   NOT NULL DEFAULT 0,      -- قيمة الزيادة بالكلفة
    CreatedByUserId INT             NOT NULL FOREIGN KEY REFERENCES Users(Id),
    CreatedAt       DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

IF OBJECT_ID('StockCountLines', 'U') IS NULL
CREATE TABLE StockCountLines (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    StockCountId    INT             NOT NULL FOREIGN KEY REFERENCES StockCounts(Id),
    ItemId          INT             NOT NULL FOREIGN KEY REFERENCES Items(Id),
    SystemQuantity  DECIMAL(18,3)   NOT NULL,
    CountedQuantity DECIMAL(18,3)   NOT NULL,
    CountDetail     NVARCHAR(200)   NULL,                    -- مثال: 3 باليت + 12 كرتون + 150 قطعة
    UnitCost        DECIMAL(18,4)   NULL,
    VarianceValue   DECIMAL(18,2)   NOT NULL DEFAULT 0,
    Notes           NVARCHAR(200)   NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_StockCountLines_Count')
    CREATE INDEX IX_StockCountLines_Count ON StockCountLines (StockCountId);
GO

IF OBJECT_ID('FreeIssueBeneficiaries', 'U') IS NULL
CREATE TABLE FreeIssueBeneficiaries (
    Id          INT IDENTITY(1,1) PRIMARY KEY,
    Name        NVARCHAR(150)   NOT NULL UNIQUE,
    Category    NVARCHAR(30)    NOT NULL CHECK (Category IN (N'Government', N'Drivers', N'Partners', N'Staff', N'Reps', N'Other')),
    IsActive    BIT             NOT NULL DEFAULT 1
);
GO

IF COL_LENGTH('StockDocuments', 'BeneficiaryCategory') IS NULL
    ALTER TABLE StockDocuments ADD BeneficiaryCategory NVARCHAR(30) NULL;
GO

-- الجرد بتاريخ داخل شهر مقفل مرفوض كبقية المستندات
CREATE OR ALTER TRIGGER trg_StockCounts_PeriodLock ON StockCounts AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @lock DATE = dbo.fn_PeriodLockedThrough();
    IF @lock IS NULL RETURN;
    IF EXISTS (SELECT 1 FROM inserted WHERE CountDate <= @lock) OR EXISTS (SELECT 1 FROM deleted WHERE CountDate <= @lock)
        EXEC sp_Period_ThrowLocked @lock;
END;
GO
