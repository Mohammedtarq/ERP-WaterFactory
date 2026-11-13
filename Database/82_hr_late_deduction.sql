/* ============================================================
   الموارد البشرية: خصم التأخير بالشرائح + المسامحة + فصل العمل الإضافي (أيام/ساعات) في الرواتب
   - PayrollLines: OvertimeDaysAmount / OvertimeHoursAmount / LateMinutes / LateDeduction
   - AttendanceRecords: ForgivenMinutes / ForgiveReason / ForgivenByUserId (مسامحة يومية)
   - Employees: LateDeductionExempt / LateGraceOverride (استثناء لكل موظف)
   - HrPolicies: LateDeductionEnabled / LateDeductionFrom (معطّل افتراضيًا فلا يتغير أي راتب)
   - LateDeductionTiers: شرائح الخصم (من/إلى بالدقائق، جزء من أجر اليوم أو مبلغ ثابت)
   - LateMonthlyForgiveness: رصيد دقائق مسامحة شهري لكل موظف
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */
IF COL_LENGTH('PayrollLines', 'OvertimeDaysAmount') IS NULL
    ALTER TABLE PayrollLines ADD OvertimeDaysAmount DECIMAL(18,2) NOT NULL CONSTRAINT DF_PL_OtDaysAmt DEFAULT 0;
IF COL_LENGTH('PayrollLines', 'OvertimeHoursAmount') IS NULL
    ALTER TABLE PayrollLines ADD OvertimeHoursAmount DECIMAL(18,2) NOT NULL CONSTRAINT DF_PL_OtHoursAmt DEFAULT 0;
IF COL_LENGTH('PayrollLines', 'LateMinutes') IS NULL
    ALTER TABLE PayrollLines ADD LateMinutes INT NOT NULL CONSTRAINT DF_PL_LateMin DEFAULT 0;
IF COL_LENGTH('PayrollLines', 'LateDeduction') IS NULL
    ALTER TABLE PayrollLines ADD LateDeduction DECIMAL(18,2) NOT NULL CONSTRAINT DF_PL_LateDed DEFAULT 0;
GO
IF COL_LENGTH('AttendanceRecords', 'ForgivenMinutes') IS NULL
    ALTER TABLE AttendanceRecords ADD ForgivenMinutes INT NOT NULL CONSTRAINT DF_AR_ForgivenMin DEFAULT 0;
IF COL_LENGTH('AttendanceRecords', 'ForgiveReason') IS NULL
    ALTER TABLE AttendanceRecords ADD ForgiveReason NVARCHAR(250) NULL;
IF COL_LENGTH('AttendanceRecords', 'ForgivenByUserId') IS NULL
    ALTER TABLE AttendanceRecords ADD ForgivenByUserId INT NULL;
GO
IF COL_LENGTH('Employees', 'LateDeductionExempt') IS NULL
    ALTER TABLE Employees ADD LateDeductionExempt BIT NOT NULL CONSTRAINT DF_Emp_LateExempt DEFAULT 0;
IF COL_LENGTH('Employees', 'LateGraceOverride') IS NULL
    ALTER TABLE Employees ADD LateGraceOverride INT NULL;
GO
IF COL_LENGTH('HrPolicies', 'LateDeductionEnabled') IS NULL
    ALTER TABLE HrPolicies ADD LateDeductionEnabled BIT NOT NULL CONSTRAINT DF_HrPol_LateOn DEFAULT 0;
IF COL_LENGTH('HrPolicies', 'LateDeductionFrom') IS NULL
    ALTER TABLE HrPolicies ADD LateDeductionFrom DATE NULL;
GO
IF OBJECT_ID('LateDeductionTiers', 'U') IS NULL
CREATE TABLE LateDeductionTiers (
    Id          INT IDENTITY(1,1) PRIMARY KEY,
    FromMinutes INT           NOT NULL,
    ToMinutes   INT           NULL,                       -- NULL = بلا حد أعلى
    Kind        NVARCHAR(10)  NOT NULL CHECK (Kind IN (N'Fraction', N'Fixed', N'None')),
    Value       DECIMAL(18,4) NOT NULL DEFAULT 0          -- Fraction: جزء من أجر اليوم (0.25)، Fixed: مبلغ بالدينار
);
GO
IF NOT EXISTS (SELECT 1 FROM LateDeductionTiers)
    INSERT INTO LateDeductionTiers (FromMinutes, ToMinutes, Kind, Value) VALUES (1, 30, N'Fraction', 0.25), (31, 60, N'Fraction', 0.5), (61, NULL, N'Fraction', 1);
GO
IF OBJECT_ID('LateMonthlyForgiveness', 'U') IS NULL
CREATE TABLE LateMonthlyForgiveness (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    EmployeeId      INT           NOT NULL FOREIGN KEY REFERENCES Employees(Id),
    PeriodMonth     INT           NOT NULL,
    PeriodYear      INT           NOT NULL,
    Minutes         INT           NOT NULL,
    Reason          NVARCHAR(250) NOT NULL,
    CreatedByUserId INT           NOT NULL FOREIGN KEY REFERENCES Users(Id),
    CreatedAt       DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_LateMonthlyForgiveness UNIQUE (EmployeeId, PeriodMonth, PeriodYear)
);
GO
