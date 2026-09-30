/* ============================================================
   قاعدة التحكم المركزية (Control DB)
   قاعدة بيانات واحدة صغيرة ومشتركة بين كل المشاريع.
   دورها الوحيد: معرفة أي مستخدم له صلاحية الدخول إلى أي مشروع،
   وأين تقع قاعدة بيانات كل مشروع فعليًا (Connection String).
   لا تحتوي أي بيانات تشغيلية (لا فواتير، لا أرصدة، لا رواتب).
   ============================================================ */

CREATE DATABASE ERP_ControlDB;
GO
USE ERP_ControlDB;
GO

-- كل مشروع/شركة مستقلة تمامًا (كل واحد له قاعدة بيانات SQL Server خاصة به)
CREATE TABLE Projects (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    ProjectName     NVARCHAR(200)   NOT NULL,
    DatabaseName    NVARCHAR(128)   NOT NULL,      -- اسم قاعدة بيانات المشروع الفعلية
    ServerAddress   NVARCHAR(200)   NOT NULL,       -- عنوان سيرفر SQL Server المستضيف لهذا المشروع
    IsActive        BIT             NOT NULL DEFAULT 1,
    CreatedAt       DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

-- حساب المستخدم على مستوى النظام كله (تسجيل الدخول الموحد)
CREATE TABLE GlobalUsers (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    FullName        NVARCHAR(200)   NOT NULL,
    Username        NVARCHAR(100)   NOT NULL UNIQUE,
    PasswordHash    NVARCHAR(256)   NOT NULL,
    IsActive        BIT             NOT NULL DEFAULT 1,
    CreatedAt       DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

-- ربط: أي مستخدم له صلاحية الدخول لأي مشروع، وبأي معرّف مستخدم محلي داخل قاعدة بيانات ذلك المشروع
-- (Users الفعلي بالأدوار التفصيلية موجود داخل قاعدة كل مشروع، هنا فقط بوابة الدخول)
CREATE TABLE UserProjectAccess (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    GlobalUserId        INT NOT NULL FOREIGN KEY REFERENCES GlobalUsers(Id),
    ProjectId            INT NOT NULL FOREIGN KEY REFERENCES Projects(Id),
    LocalUserIdInProject INT NOT NULL,   -- يطابق Users.Id داخل قاعدة بيانات ذلك المشروع
    GrantedAt            DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_UserProject UNIQUE (GlobalUserId, ProjectId)
);
GO
