/* ============================================================
   حساب لوحة المدير (قراءة فقط) على الإنترنت: اسم مستخدم وكلمة مرور مجزّأة بملح — لا تُخزَّن كلمة المرور نصًّا.
   يصل الخادم السحابي بصمته فقط. قابل لإعادة التنفيذ بأمان.
   ============================================================ */
IF OBJECT_ID('ManagerAccounts', 'U') IS NULL
CREATE TABLE ManagerAccounts (
    Id           INT           NOT NULL IDENTITY PRIMARY KEY,
    Username     NVARCHAR(30)  NOT NULL CONSTRAINT UQ_ManagerAccounts_Username UNIQUE,
    Salt         VARCHAR(40)   NOT NULL,
    PasswordHash VARCHAR(100)  NOT NULL,
    IsActive     BIT           NOT NULL CONSTRAINT DF_ManagerAccounts_Active DEFAULT 1,
    CreatedAt    DATETIME2     NOT NULL CONSTRAINT DF_ManagerAccounts_Created DEFAULT SYSUTCDATETIME(),
    ChangedAt    DATETIME2     NULL
);
GO
