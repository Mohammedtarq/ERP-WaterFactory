/* سقف تسوية الجرد بلا موافقة ثانية (قابل للتعديل من شاشة الصناديق). 0 = بلا موافقة ثانية. قابل لإعادة التنفيذ بأمان. */
IF OBJECT_ID('FinanceSettings', 'U') IS NULL
CREATE TABLE FinanceSettings (
    Id               INT            NOT NULL PRIMARY KEY CONSTRAINT CK_FinanceSettings_One CHECK (Id = 1),
    CashAdjustLimit  DECIMAL(18,2)  NOT NULL CONSTRAINT DF_FinanceSettings_Limit DEFAULT 5000000 CONSTRAINT CK_FinanceSettings_Limit CHECK (CashAdjustLimit >= 0),
    UpdatedByUserId  INT            NULL FOREIGN KEY REFERENCES Users(Id),
    UpdatedAt        DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
IF NOT EXISTS (SELECT 1 FROM FinanceSettings) INSERT INTO FinanceSettings (Id) VALUES (1);
GO
