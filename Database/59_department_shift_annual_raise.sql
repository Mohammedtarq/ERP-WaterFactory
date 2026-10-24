/* ============================================================
   شفت افتراضي لكل قسم + تنبيه العلاوة السنوية
   - Departments.DefaultShiftId: يأخذه كل موظفي القسم ما لم يكن للموظف «شفت خاص به» (Employees.HasOwnShift).
   - العلاوة السنوية: مبلغ لكل قسم (Departments.AnnualRaiseAmount) ويعلو عليه مبلغ الموظف (Employees.AnnualRaiseAmount).
   - Employees.LastRaiseDate: تاريخ آخر علاوة قبل النظام (للقدامى)؛ وإلا آخر علاوة/ترقية مسجّلة، وإلا تاريخ التعيين.
   - التأجيل: Employees.RaiseSnoozedUntil + السبب.
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */
IF COL_LENGTH('Departments', 'DefaultShiftId') IS NULL
    ALTER TABLE Departments ADD DefaultShiftId INT NULL CONSTRAINT FK_Departments_DefaultShift REFERENCES Shifts(Id);
GO
IF COL_LENGTH('Departments', 'AnnualRaiseAmount') IS NULL
    ALTER TABLE Departments ADD AnnualRaiseAmount DECIMAL(18,2) NULL CONSTRAINT CK_Departments_AnnualRaise CHECK (AnnualRaiseAmount >= 0);
GO
IF COL_LENGTH('Employees', 'HasOwnShift') IS NULL
    ALTER TABLE Employees ADD HasOwnShift BIT NOT NULL CONSTRAINT DF_Employees_HasOwnShift DEFAULT 0;
GO
IF COL_LENGTH('Employees', 'AnnualRaiseAmount') IS NULL
    ALTER TABLE Employees ADD AnnualRaiseAmount DECIMAL(18,2) NULL CONSTRAINT CK_Employees_AnnualRaise CHECK (AnnualRaiseAmount >= 0);
GO
IF COL_LENGTH('Employees', 'LastRaiseDate') IS NULL
    ALTER TABLE Employees ADD LastRaiseDate DATE NULL;
GO
IF COL_LENGTH('Employees', 'RaiseSnoozedUntil') IS NULL
    ALTER TABLE Employees ADD RaiseSnoozedUntil DATE NULL;
GO
IF COL_LENGTH('Employees', 'RaiseSnoozeReason') IS NULL
    ALTER TABLE Employees ADD RaiseSnoozeReason NVARCHAR(300) NULL;
GO
