/* ============================================================
   دخول تطبيق المندوب: اسم دخول وكلمة مرور قصيرة (حتى 4 حروف أو أرقام) يضعها المعمل لكل مندوب.
   تُخزَّن كلمة المرور مجزّأة بملح (PBKDF2) لا نصًّا، وتصل الهاتف مجزّأة فيتحقق منها بلا إنترنت.
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */
IF COL_LENGTH('Employees', 'RepLoginName') IS NULL
    ALTER TABLE Employees ADD RepLoginName NVARCHAR(10) NULL, RepLoginSalt NVARCHAR(40) NULL, RepLoginHash NVARCHAR(100) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Employees_RepLoginName' AND object_id = OBJECT_ID('Employees'))
    EXEC('CREATE UNIQUE INDEX UX_Employees_RepLoginName ON Employees (RepLoginName) WHERE RepLoginName IS NOT NULL');
GO
