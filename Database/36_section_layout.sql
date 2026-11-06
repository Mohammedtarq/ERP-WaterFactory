/* ============================================================
   توزيع الأقسام بقرار الإدارة:
   - نقل قسم (شاشة) من وحدة إلى أخرى: يعمل عليه من يملك صلاحية الوحدة المختارة
   - إخفاء أقسام بعينها عن دور (مثل موظف مبيعات يرى الفاتورة وكشف الحساب فقط)
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */

IF OBJECT_ID('SectionPlacements', 'U') IS NULL
CREATE TABLE SectionPlacements (
    SectionKey          NVARCHAR(100)   NOT NULL PRIMARY KEY,
    TargetModule        NVARCHAR(50)    NOT NULL,
    ChangedByUserId     INT             NOT NULL FOREIGN KEY REFERENCES Users(Id),
    ChangedAt           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

IF OBJECT_ID('RoleHiddenSections', 'U') IS NULL
CREATE TABLE RoleHiddenSections (
    RoleId              INT             NOT NULL FOREIGN KEY REFERENCES Roles(Id) ON DELETE CASCADE,
    SectionKey          NVARCHAR(100)   NOT NULL,
    CONSTRAINT PK_RoleHiddenSections PRIMARY KEY (RoleId, SectionKey)
);
GO
