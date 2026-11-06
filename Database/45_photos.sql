/* ============================================================
   صور الموظفين والمستخدمين
   - جدولان منفصلان (لا عمود في Employees/Users) حتى لا تُحمَّل الصور مع كل قراءة للموظفين في الشاشات الأخرى.
   - الصورة تُصغَّر عند الاختيار إلى 256×256 (JPEG) فتبقى صغيرة وتنتقل مع النسخة الاحتياطية.
   - صورة اليوزر اختيارية: إن لم تُحدَّد يأخذ صورة الموظف المربوط به.
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */
IF OBJECT_ID('EmployeePhotos', 'U') IS NULL
CREATE TABLE EmployeePhotos (
    EmployeeId          INT             NOT NULL PRIMARY KEY REFERENCES Employees(Id) ON DELETE CASCADE,
    Photo               VARBINARY(MAX)  NOT NULL,
    UpdatedAt           DATETIME2       NOT NULL CONSTRAINT DF_EmployeePhotos_UpdatedAt DEFAULT SYSUTCDATETIME()
);
GO

IF OBJECT_ID('UserPhotos', 'U') IS NULL
CREATE TABLE UserPhotos (
    UserId              INT             NOT NULL PRIMARY KEY REFERENCES Users(Id) ON DELETE CASCADE,
    Photo               VARBINARY(MAX)  NOT NULL,
    UpdatedAt           DATETIME2       NOT NULL CONSTRAINT DF_UserPhotos_UpdatedAt DEFAULT SYSUTCDATETIME()
);
GO
