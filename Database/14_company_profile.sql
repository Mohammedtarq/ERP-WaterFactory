/* ============================================================
   هوية الطباعة المركزية: شعار الشركة واسمها ومعلومات الاتصال ولون العلامة ونص التذييل
   تُستخدم تلقائيًا في رأس وتذييل كل مستند وتقرير مطبوع (قابل لإعادة التنفيذ بأمان)
   ============================================================ */
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID('CompanyProfile', 'U') IS NULL
CREATE TABLE CompanyProfile (
    Id                  INT             NOT NULL PRIMARY KEY CHECK (Id = 1),   -- سجل واحد فقط
    NameAr              NVARCHAR(200)   NOT NULL,
    NameEn              NVARCHAR(200)   NULL,
    Phones              NVARCHAR(400)   NULL,       -- رقم في كل سطر
    Address             NVARCHAR(400)   NULL,
    TaxNumber           NVARCHAR(100)   NULL,
    CommercialRegister  NVARCHAR(100)   NULL,
    BrandColor          NVARCHAR(9)     NOT NULL DEFAULT N'#0F766E',   -- تركواز الواجهة
    FooterText          NVARCHAR(400)   NULL,
    Logo                VARBINARY(MAX)  NULL,       -- PNG/JPG
    UpdatedAt           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
