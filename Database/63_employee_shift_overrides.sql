/* ============================================================
   نقل مؤقت لشفت ووقت دخول خاص لموظف لمدة محددة (الأصل يعود بعد النهاية).
   الحضور واستيراد البصمة يأخذان شفت اليوم من هنا إن وُجد.
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */
IF OBJECT_ID('EmployeeShiftOverrides', 'U') IS NULL
CREATE TABLE EmployeeShiftOverrides (
    Id          INT             NOT NULL IDENTITY PRIMARY KEY,
    EmployeeId  INT             NOT NULL CONSTRAINT FK_ShiftOverrides_Employee REFERENCES Employees(Id),
    FromDate    DATE            NOT NULL,
    ToDate      DATE            NOT NULL,
    ShiftId     INT             NULL CONSTRAINT FK_ShiftOverrides_Shift REFERENCES Shifts(Id),
    CheckInTime TIME(0)         NULL,
    Reason      NVARCHAR(200)   NOT NULL,
    CreatedAt   DATETIME2       NOT NULL CONSTRAINT DF_ShiftOverrides_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_ShiftOverrides_Range CHECK (ToDate >= FromDate),
    CONSTRAINT CK_ShiftOverrides_Something CHECK (ShiftId IS NOT NULL OR CheckInTime IS NOT NULL)
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ShiftOverrides_Employee')
    CREATE INDEX IX_ShiftOverrides_Employee ON EmployeeShiftOverrides (EmployeeId, FromDate, ToDate);
GO
