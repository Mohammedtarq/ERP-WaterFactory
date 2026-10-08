/* ============================================================
   الموارد البشرية — الحضور، الرواتب، الترقيات، الحوافز
   ============================================================ */

-- ============ الحضور والانصراف اليومي ============
CREATE TABLE AttendanceRecords (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    EmployeeId      INT             NOT NULL FOREIGN KEY REFERENCES Employees(Id),
    AttendanceDate  DATE            NOT NULL,
    CheckInTime     TIME            NULL,
    CheckOutTime    TIME            NULL,
    LateMinutes     INT             NOT NULL DEFAULT 0,
    Status          NVARCHAR(20)    NOT NULL
                        CHECK (Status IN (N'Present', N'Late', N'Absent', N'ApprovedLeave')),
    CONSTRAINT UQ_EmployeeDate UNIQUE (EmployeeId, AttendanceDate)
);
GO

-- ============ تشغيل الرواتب (رأس الدورة الشهرية) ============
CREATE TABLE PayrollRuns (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    PeriodMonth     TINYINT         NOT NULL,
    PeriodYear      SMALLINT        NOT NULL,
    Status          NVARCHAR(20)    NOT NULL DEFAULT N'Draft' CHECK (Status IN (N'Draft', N'Approved')),
    JournalEntryId  INT             NULL FOREIGN KEY REFERENCES JournalEntries(Id),
    ApprovedByUserId INT            NULL FOREIGN KEY REFERENCES Users(Id),
    CONSTRAINT UQ_Period UNIQUE (PeriodMonth, PeriodYear)
);
GO

-- ============ سطور الرواتب (لكل موظف داخل الدورة) ============
CREATE TABLE PayrollLines (
    Id                              INT IDENTITY(1,1) PRIMARY KEY,
    PayrollRunId                    INT             NOT NULL FOREIGN KEY REFERENCES PayrollRuns(Id),
    EmployeeId                      INT             NOT NULL FOREIGN KEY REFERENCES Employees(Id),
    Currency                        NVARCHAR(3)     NOT NULL,
    BaseSalary                      DECIMAL(18,2)   NOT NULL,
    AbsenceDeduction                DECIMAL(18,2)   NOT NULL DEFAULT 0,
    Allowances                      DECIMAL(18,2)   NOT NULL DEFAULT 0,
    RepIncentiveAmount              DECIMAL(18,2)   NOT NULL DEFAULT 0,
    SalesManagerIncentiveAmount     DECIMAL(18,2)   NOT NULL DEFAULT 0,
    MonthlyIncentiveAmount          DECIMAL(18,2)   NOT NULL DEFAULT 0,
    NetSalary                       DECIMAL(18,2)   NOT NULL
);
GO

-- ============ الترقيات والعلاوات السنوية (إدخال يدوي بالكامل) ============
CREATE TABLE PromotionsAndRaises (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    EmployeeId      INT             NOT NULL FOREIGN KEY REFERENCES Employees(Id),
    MovementType    NVARCHAR(20)    NOT NULL
                        CHECK (MovementType IN (N'Promotion', N'AnnualRaise', N'AnnualBonus')),
    NewJobTitle     NVARCHAR(150)   NULL,
    Amount          DECIMAL(18,2)   NOT NULL,
    EffectiveDate   DATE            NOT NULL,
    ApplicationType NVARCHAR(20)    NOT NULL
                        CHECK (ApplicationType IN (N'PermanentAddition', N'OneTime')),
    Notes           NVARCHAR(400)   NULL,
    CreatedByUserId INT             NOT NULL FOREIGN KEY REFERENCES Users(Id)
);
GO

-- ============ إعداد أوزان الحافز الشهري (صف واحد قابل للتعديل) ============
CREATE TABLE IncentiveScoreWeights (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    AttendanceWeight    DECIMAL(5,2)    NOT NULL DEFAULT 40,
    PerformanceWeight   DECIMAL(5,2)    NOT NULL DEFAULT 30,
    SkillsWeight        DECIMAL(5,2)    NOT NULL DEFAULT 30
);
GO

-- ============ مقياس تحويل النقاط لمبلغ مالي ============
CREATE TABLE IncentiveScoreToAmountScale (
    Id          INT IDENTITY(1,1) PRIMARY KEY,
    MinScore    DECIMAL(5,2)    NOT NULL,
    MaxScore    DECIMAL(5,2)    NOT NULL,
    Amount      DECIMAL(18,2)   NOT NULL
);
GO

-- ============ تقييم الحافز الشهري الفعلي لكل موظف ============
CREATE TABLE MonthlyIncentiveEvaluations (
    Id                      INT IDENTITY(1,1) PRIMARY KEY,
    EmployeeId              INT             NOT NULL FOREIGN KEY REFERENCES Employees(Id),
    PeriodMonth             TINYINT         NOT NULL,
    PeriodYear              SMALLINT        NOT NULL,
    AttendanceScoreAuto     DECIMAL(5,2)    NOT NULL,           -- محسوبة تلقائيًا من AttendanceRecords
    PerformanceScoreManual  DECIMAL(5,2)    NOT NULL,           -- يُدخلها المدير
    SkillsScoreManual       DECIMAL(5,2)    NOT NULL,           -- يُدخلها المدير
    TotalScore              DECIMAL(5,2)    NOT NULL,
    IncentiveAmount         DECIMAL(18,2)   NOT NULL,
    CONSTRAINT UQ_EmployeePeriod UNIQUE (EmployeeId, PeriodMonth, PeriodYear)
);
GO

-- ============ حافز المندوب: سعر حافز يدوي منفصل لكل صنف ============
CREATE TABLE RepItemIncentiveRates (
    Id                      INT IDENTITY(1,1) PRIMARY KEY,
    ItemId                  INT             NOT NULL FOREIGN KEY REFERENCES Items(Id) UNIQUE,
    IncentiveRatePerUnit    DECIMAL(18,2)   NOT NULL
);
GO

-- ============ حافز مدير المبيعات: شرائح تصاعدية على إجمالي الكمية المباعة ============
CREATE TABLE SalesManagerIncentiveTiers (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    EmployeeId      INT             NOT NULL FOREIGN KEY REFERENCES Employees(Id),  -- IsSalesManager = 1
    FromQuantity    DECIMAL(18,3)   NOT NULL,
    ToQuantity      DECIMAL(18,3)   NULL,               -- NULL = بلا حد أعلى
    RatePerUnit     DECIMAL(18,2)   NOT NULL
    -- المعادلة النهائية (تُحسب في التطبيق): SUM(كمية الشريحة × معدلها) × أيام الدوام الفعلية للمدير
);
GO
