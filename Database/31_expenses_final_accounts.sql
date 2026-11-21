/* ============================================================
   المرحلة م4: المصروفات والحسابات الختامية
   - أنواع المصروف بتصنيف ثابت: تشغيلي (يدخل كلفة القنينة) / غير تشغيلي (توسعة، مكائن: يُطرح من ربح الشهر فقط) / إيراد آخر
   - المصروف والإيراد الآخر: من صندوق المستخدم تلقائيًا، مع السيارة أو القسم ورقم الوصل، وقيد في الخلفية
   - رأس المال التشغيلي بتاريخ سريان (يتغير دون أن يمس الأشهر السابقة)
   - "صندوق المنزل": يستقبل الفائض المتحقق خارج الصندوق الرئيسي
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */

IF OBJECT_ID('FinanceCategories', 'U') IS NULL
CREATE TABLE FinanceCategories (
    Id          INT IDENTITY(1,1) PRIMARY KEY,
    Name        NVARCHAR(100)   NOT NULL UNIQUE,
    Kind        NVARCHAR(20)    NOT NULL CHECK (Kind IN (N'Operating', N'NonOperating', N'OtherIncome')),
    AccountId   INT             NULL FOREIGN KEY REFERENCES ChartOfAccounts(Id),   -- NULL = الحساب الافتراضي لنوعه
    IsActive    BIT             NOT NULL DEFAULT 1,
    SortOrder   INT             NOT NULL DEFAULT 0
);
GO

-- الأنواع الشائعة (قابلة للتعديل والإضافة من الشاشة)
INSERT INTO FinanceCategories (Name, Kind, SortOrder)
SELECT v.Name, v.Kind, v.SortOrder
FROM (VALUES
    (N'كهرباء ومولدة',     N'Operating', 1),
    (N'ماء RO',            N'Operating', 2),
    (N'وقود السيارات',      N'Operating', 3),
    (N'صيانة وتصليح',       N'Operating', 4),
    (N'أجور يومية',         N'Operating', 5),
    (N'إيجار',              N'Operating', 6),
    (N'نثرية',              N'Operating', 7),
    (N'توسعة',              N'NonOperating', 20),
    (N'مكائن ومعدات',       N'NonOperating', 21),
    (N'إيراد آخر',          N'OtherIncome', 40)
) v(Name, Kind, SortOrder)
WHERE NOT EXISTS (SELECT 1 FROM FinanceCategories);
GO

IF OBJECT_ID('FinanceEntries', 'U') IS NULL
CREATE TABLE FinanceEntries (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    EntryNumber         NVARCHAR(30)    NOT NULL UNIQUE,
    EntryDate           DATE            NOT NULL,
    CategoryId          INT             NOT NULL FOREIGN KEY REFERENCES FinanceCategories(Id),
    Amount              DECIMAL(18,2)   NOT NULL CHECK (Amount > 0),
    VehicleId           INT             NULL FOREIGN KEY REFERENCES Vehicles(Id),
    DepartmentId        INT             NULL FOREIGN KEY REFERENCES Departments(Id),
    PartyName           NVARCHAR(150)   NULL,          -- الصرف لـ / المستلم منه
    ReceiptNumber       NVARCHAR(50)    NULL,          -- رقم الوصل
    Notes               NVARCHAR(400)   NULL,
    JournalEntryId      INT             NULL FOREIGN KEY REFERENCES JournalEntries(Id),
    IsVoided            BIT             NOT NULL DEFAULT 0,
    VoidReason          NVARCHAR(300)   NULL,
    VoidedByUserId      INT             NULL FOREIGN KEY REFERENCES Users(Id),
    VoidedAt            DATETIME2       NULL,
    CreatedByUserId     INT             NOT NULL FOREIGN KEY REFERENCES Users(Id),
    CreatedAt           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_FinanceEntries_Date')
    CREATE INDEX IX_FinanceEntries_Date ON FinanceEntries (EntryDate) INCLUDE (CategoryId, Amount, IsVoided, VehicleId);
GO

CREATE OR ALTER TRIGGER trg_FinanceEntries_PeriodLock ON FinanceEntries AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @lock DATE = dbo.fn_PeriodLockedThrough();
    IF @lock IS NULL RETURN;
    IF EXISTS (SELECT 1 FROM inserted WHERE EntryDate <= @lock) OR EXISTS (SELECT 1 FROM deleted WHERE EntryDate <= @lock)
        EXEC sp_Period_ThrowLocked @lock;
END;
GO

-- رأس المال التشغيلي: الساري في تاريخ = آخر صف تاريخ سريانه ≤ التاريخ
IF OBJECT_ID('WorkingCapitalSettings', 'U') IS NULL
CREATE TABLE WorkingCapitalSettings (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    EffectiveFrom       DATE            NOT NULL,
    Amount              DECIMAL(18,2)   NOT NULL CHECK (Amount >= 0),
    Notes               NVARCHAR(300)   NULL,
    CreatedByUserId     INT             NOT NULL FOREIGN KEY REFERENCES Users(Id),
    CreatedAt           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

-- ---------- صندوق المنزل ----------
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CashBoxes_BoxType' AND definition LIKE N'%Home%')
BEGIN
    IF OBJECT_ID('CK_CashBoxes_BoxType', 'C') IS NOT NULL ALTER TABLE CashBoxes DROP CONSTRAINT CK_CashBoxes_BoxType;
    DECLARE @oldBox SYSNAME = (SELECT TOP 1 name FROM sys.check_constraints
                               WHERE parent_object_id = OBJECT_ID('CashBoxes') AND definition LIKE N'%BoxType%');
    IF @oldBox IS NOT NULL EXEC (N'ALTER TABLE CashBoxes DROP CONSTRAINT [' + @oldBox + N']');
    ALTER TABLE CashBoxes ADD CONSTRAINT CK_CashBoxes_BoxType CHECK (BoxType IN (N'Main', N'User', N'Bank', N'Home'));
END;
GO

-- ---------- نوعا حركة صندوق: مصروف وإيراد آخر ----------
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CashBoxTransactions_Type' AND definition LIKE '%OtherIncome%')
BEGIN
    IF OBJECT_ID('CK_CashBoxTransactions_Type', 'C') IS NOT NULL ALTER TABLE CashBoxTransactions DROP CONSTRAINT CK_CashBoxTransactions_Type;
    ALTER TABLE CashBoxTransactions ADD CONSTRAINT CK_CashBoxTransactions_Type CHECK (TxType IN (
        N'Opening', N'Deposit', N'Withdrawal', N'TransferIn', N'TransferOut',
        N'SalesReceipt', N'VoucherReceipt', N'VoucherPayment', N'RepHandover',
        N'CustomerDepositIn', N'CustomerDepositOut', N'EmployeeAdvance', N'PartnerWithdrawal',
        N'Expense', N'OtherIncome'));
END;
GO
