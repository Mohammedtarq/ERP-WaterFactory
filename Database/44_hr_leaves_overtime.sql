/* ============================================================
   الإجازات والعطل الرسمية والعمل الإضافي (قرارات المدير)
   - أنواع الإجازات: لكل نوع أيام مسموحة في الشهر أو في السنة (أو بلا حد)، مدفوعة أو لا، قابلة للتعديل.
     الافتراضي: اعتيادية 3 أيام في الشهر (لا تُرحَّل، تسقط بنهاية الشهر)، مرضية 30 في السنة، وفاة قريب 4،
     زواج 7، بدون راتب بلا حد.
   - الإجازة يومية (من تاريخ إلى تاريخ، تُحسب أيام العمل فقط) أو زمنية (من ساعة إلى ساعة في يوم واحد):
     زمنيتان في الشهر مسموحتان، ومن الثالثة تُحسب كل زمنية يومًا من رصيد الاعتيادية، وإن لم يتوفر رصيد فيوم بدون راتب.
     ما يتجاوز رصيد النوع المدفوع يُسجَّل بدون راتب (مع تنبيه) ويُخصم في الرواتب.
   - العطل الرسمية: لا يُسجَّل فيها غياب من البصمة.
   - العمل الإضافي (إدخال يدوي): أيام (بالأجر اليومي = الراتب ÷ 30) وساعات (سعر عام تضبطه الإدارة، ويمكن تخصيصه لموظف).
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */
IF OBJECT_ID('LeaveTypes', 'U') IS NULL
CREATE TABLE LeaveTypes (
    Id                  INT             NOT NULL IDENTITY PRIMARY KEY,
    Name                NVARCHAR(100)   NOT NULL CONSTRAINT UQ_LeaveTypes_Name UNIQUE,
    AllowedDays         DECIMAL(6,2)    NULL CHECK (AllowedDays IS NULL OR AllowedDays >= 0),   -- NULL = بلا حد
    AllowancePeriod     NVARCHAR(10)    NOT NULL DEFAULT N'Year' CHECK (AllowancePeriod IN (N'Month', N'Year')),
    IsPaid              BIT             NOT NULL DEFAULT 1,
    IsRegular           BIT             NOT NULL DEFAULT 0,   -- الاعتيادية: تُخصم منها الزمنية الزائدة
    IsActive            BIT             NOT NULL DEFAULT 1,
    SortOrder           INT             NOT NULL DEFAULT 0
);
GO
IF NOT EXISTS (SELECT 1 FROM LeaveTypes)
INSERT INTO LeaveTypes (Name, AllowedDays, AllowancePeriod, IsPaid, IsRegular, SortOrder) VALUES
    (N'اعتيادية', 3, N'Month', 1, 1, 1),
    (N'مرضية', 30, N'Year', 1, 0, 2),
    (N'وفاة قريب', 4, N'Year', 1, 0, 3),
    (N'زواج', 7, N'Year', 1, 0, 4),
    (N'بدون راتب', NULL, N'Year', 0, 0, 5);
GO

IF OBJECT_ID('EmployeeLeaves', 'U') IS NULL
CREATE TABLE EmployeeLeaves (
    Id                  INT             NOT NULL IDENTITY PRIMARY KEY,
    LeaveNumber         NVARCHAR(30)    NOT NULL CONSTRAINT UQ_EmployeeLeaves_Number UNIQUE,
    EmployeeId          INT             NOT NULL REFERENCES Employees(Id),
    LeaveTypeId         INT             NOT NULL REFERENCES LeaveTypes(Id),
    Kind                NVARCHAR(10)    NOT NULL CHECK (Kind IN (N'Day', N'Hourly')),
    FromDate            DATE            NOT NULL,
    ToDate              DATE            NOT NULL,
    FromTime            TIME(0)         NULL,
    ToTime              TIME(0)         NULL,
    WorkDays            DECIMAL(6,2)    NOT NULL DEFAULT 0,   -- أيام العمل التي تغطيها (اليومية)
    PaidDays            DECIMAL(6,2)    NOT NULL DEFAULT 0,   -- من رصيد النوع (أو الاعتيادية للزمنية الزائدة)
    UnpaidDays          DECIMAL(6,2)    NOT NULL DEFAULT 0,   -- تُخصم في الرواتب
    Note                NVARCHAR(300)   NULL,                 -- رقم التقرير الطبي أو ملاحظة
    IsVoided            BIT             NOT NULL DEFAULT 0,
    VoidReason          NVARCHAR(200)   NULL,
    CreatedByUserId     INT             NOT NULL REFERENCES Users(Id),
    CreatedAt           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_EmployeeLeaves_Range CHECK (ToDate >= FromDate),
    CONSTRAINT CK_EmployeeLeaves_Hourly CHECK (Kind = N'Day' OR (FromTime IS NOT NULL AND ToTime IS NOT NULL AND ToTime > FromTime AND ToDate = FromDate))
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_EmployeeLeaves_Employee_Date')
    CREATE INDEX IX_EmployeeLeaves_Employee_Date ON EmployeeLeaves (EmployeeId, FromDate, ToDate);
GO

IF OBJECT_ID('PublicHolidays', 'U') IS NULL
CREATE TABLE PublicHolidays (
    Id                  INT             NOT NULL IDENTITY PRIMARY KEY,
    HolidayDate         DATE            NOT NULL CONSTRAINT UQ_PublicHolidays_Date UNIQUE,
    Name                NVARCHAR(100)   NOT NULL
);
GO

IF OBJECT_ID('OvertimeEntries', 'U') IS NULL
CREATE TABLE OvertimeEntries (
    Id                  INT             NOT NULL IDENTITY PRIMARY KEY,
    EmployeeId          INT             NOT NULL REFERENCES Employees(Id),
    WorkDate            DATE            NOT NULL,
    Kind                NVARCHAR(10)    NOT NULL CHECK (Kind IN (N'Day', N'Hours')),
    Quantity            DECIMAL(6,2)    NOT NULL CHECK (Quantity > 0),
    Note                NVARCHAR(300)   NULL,
    IsVoided            BIT             NOT NULL DEFAULT 0,
    VoidReason          NVARCHAR(200)   NULL,
    CreatedByUserId     INT             NOT NULL REFERENCES Users(Id),
    CreatedAt           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

-- سياسة الموارد البشرية: صف واحد (سعر الساعة الإضافية العام، والزمنيات المسموحة شهريًا)
IF OBJECT_ID('HrPolicies', 'U') IS NULL
CREATE TABLE HrPolicies (
    Id                          INT             NOT NULL PRIMARY KEY CHECK (Id = 1),
    OvertimeHourlyRate          DECIMAL(18,2)   NOT NULL DEFAULT 0 CHECK (OvertimeHourlyRate >= 0),
    FreeHourlyLeavesPerMonth    INT             NOT NULL DEFAULT 2 CHECK (FreeHourlyLeavesPerMonth >= 0)
);
GO
IF NOT EXISTS (SELECT 1 FROM HrPolicies) INSERT INTO HrPolicies (Id, OvertimeHourlyRate, FreeHourlyLeavesPerMonth) VALUES (1, 0, 2);
GO

-- سعر ساعة إضافية خاص بموظف (يعلو على السعر العام)
IF COL_LENGTH('Employees', 'OvertimeHourlyRate') IS NULL
    ALTER TABLE Employees ADD OvertimeHourlyRate DECIMAL(18,2) NULL;
GO

IF COL_LENGTH('PayrollLines', 'OvertimeDays') IS NULL
    ALTER TABLE PayrollLines ADD
        OvertimeDays            DECIMAL(6,2)    NOT NULL CONSTRAINT DF_PayrollLines_OvertimeDays DEFAULT 0,
        OvertimeHours           DECIMAL(6,2)    NOT NULL CONSTRAINT DF_PayrollLines_OvertimeHours DEFAULT 0,
        OvertimeAmount          DECIMAL(18,2)   NOT NULL CONSTRAINT DF_PayrollLines_OvertimeAmount DEFAULT 0,
        UnpaidLeaveDays         DECIMAL(6,2)    NOT NULL CONSTRAINT DF_PayrollLines_UnpaidLeaveDays DEFAULT 0,
        UnpaidLeaveDeduction    DECIMAL(18,2)   NOT NULL CONSTRAINT DF_PayrollLines_UnpaidLeaveDeduction DEFAULT 0;
GO
