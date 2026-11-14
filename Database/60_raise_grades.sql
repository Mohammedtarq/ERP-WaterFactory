/* ============================================================
   درجات العلاوة السنوية (A / B / C …) تحددها الإدارة، ودرجة لكل موظف.
   المبلغ المقترح = مبلغ استثنائي في بطاقة الموظف (Employees.AnnualRaiseAmount) وإلا مبلغ درجته.
   مبلغ القسم (Departments.AnnualRaiseAmount) لم يعد مستعملًا ويبقى محفوظًا بلا حذف.
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */
IF OBJECT_ID('RaiseGrades', 'U') IS NULL
CREATE TABLE RaiseGrades (
    Id          INT             NOT NULL IDENTITY PRIMARY KEY,
    Name        NVARCHAR(50)    NOT NULL CONSTRAINT UQ_RaiseGrades_Name UNIQUE,
    Amount      DECIMAL(18,2)   NOT NULL CHECK (Amount >= 0),
    IsActive    BIT             NOT NULL DEFAULT 1
);
GO
IF COL_LENGTH('Employees', 'RaiseGradeId') IS NULL
    ALTER TABLE Employees ADD RaiseGradeId INT NULL CONSTRAINT FK_Employees_RaiseGrade REFERENCES RaiseGrades(Id);
GO
