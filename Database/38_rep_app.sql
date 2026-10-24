/* ============================================================
   تطبيق المندوبين — المرحلة 0 (داخل النظام، قبل السحابة)
   - أجهزة المندوبين: هاتف مسجّل لكل مندوب بمفتاح سري، يمكن إيقافه فورًا
   - طلبات المندوبين: كل حركة من الهاتف برقم فريد (لا تتكرر عند إعادة الإرسال)
     تلقائي: بيع نقدي، بيع آجل، مجاني، تحصيل، زبون جديد — والآجل والمجاني يظهران «للاطلاع»
     بانتظار الاعتماد: المصروف الميداني (بصورة الوصل)، ومرتجع الزبون (المرحلة 0-ب)
   - إعدادات التطبيق: السماح بتجاوز حد الدين، ومدة تنبيه بقاء النقد مع المندوب
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */

IF OBJECT_ID('RepDevices', 'U') IS NULL
CREATE TABLE RepDevices (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    RepEmployeeId       INT             NOT NULL FOREIGN KEY REFERENCES Employees(Id),
    DeviceName          NVARCHAR(100)   NOT NULL,
    DeviceKey           NVARCHAR(64)    NOT NULL CONSTRAINT UQ_RepDevices_Key UNIQUE,
    IsActive            BIT             NOT NULL DEFAULT 1,
    -- الترحيل التلقائي يجري بصلاحيات من سجّل الجهاز (ويظهر اسمه في سجل الحركات)
    RegisteredByUserId  INT             NOT NULL FOREIGN KEY REFERENCES Users(Id),
    RegisteredAt        DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    LastSeenAt          DATETIME2       NULL,
    DeactivatedAt       DATETIME2       NULL
);
GO

IF OBJECT_ID('RepRequests', 'U') IS NULL
CREATE TABLE RepRequests (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    ClientId            UNIQUEIDENTIFIER NOT NULL CONSTRAINT UQ_RepRequests_Client UNIQUE,
    DeviceId            INT             NOT NULL FOREIGN KEY REFERENCES RepDevices(Id),
    RepEmployeeId       INT             NOT NULL FOREIGN KEY REFERENCES Employees(Id),
    Kind                NVARCHAR(20)    NOT NULL CHECK (Kind IN (N'CashSale', N'CreditSale', N'Free', N'Collection', N'NewCustomer', N'Expense', N'Return')),
    OccurredAt          DATETIME2       NOT NULL,
    ReceivedAt          DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    Payload             NVARCHAR(MAX)   NOT NULL,
    Photo               VARBINARY(MAX)  NULL,
    Status              NVARCHAR(20)    NOT NULL CHECK (Status IN (N'Posted', N'Pending', N'Rejected', N'Failed')),
    Summary             NVARCHAR(300)   NOT NULL DEFAULT N'',
    Amount              DECIMAL(18,2)   NOT NULL DEFAULT 0,
    -- تنبيه للمراجعة (مثل تجاوز حد الدين) — لا يمنع الترحيل
    Warning             NVARCHAR(300)   NULL,
    ResultTable         NVARCHAR(40)    NULL,
    ResultId            INT             NULL,
    ErrorMessage        NVARCHAR(500)   NULL,
    -- الآجل والمجاني «للاطلاع»: تُعلَّم مراجَعة دون رفض
    NeedsReview         BIT             NOT NULL DEFAULT 0,
    ReviewedByUserId    INT             NULL FOREIGN KEY REFERENCES Users(Id),
    ReviewedAt          DATETIME2       NULL,
    RejectReason        NVARCHAR(300)   NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_RepRequests_Status')
    CREATE INDEX IX_RepRequests_Status ON RepRequests (Status, RepEmployeeId) INCLUDE (Kind, NeedsReview, ReviewedAt);
GO

IF OBJECT_ID('RepAppSettings', 'U') IS NULL
CREATE TABLE RepAppSettings (
    Id                      INT             NOT NULL PRIMARY KEY CHECK (Id = 1),
    AllowCreditOverLimit    BIT             NOT NULL DEFAULT 1,
    CashAlertDays           INT             NOT NULL DEFAULT 2 CHECK (CashAlertDays BETWEEN 1 AND 30)
);
GO
IF NOT EXISTS (SELECT 1 FROM RepAppSettings) INSERT INTO RepAppSettings (Id) VALUES (1);
GO

-- ترحيل البيع من التطبيق قد يتجاوز حد الدين بقرار الإدارة (يُسجَّل تنبيهًا للمراجعة)
-- (يُضاف المعامل في 09_sales_logic.sql: @AllowOverLimit)
