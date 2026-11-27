/* ============================================================
   قاعدة بيانات مشروع واحد — الجزء الأول: البنية الأساسية والأمان
   تُنفَّذ نسخة كاملة من كل ملفات هذا المجلد (01 حتى 08) على كل
   قاعدة بيانات مشروع جديد، بشكل مستقل تمامًا عن المشاريع الأخرى.
   ============================================================ */

-- مثال: CREATE DATABASE ERP_Project_WaterFactory;  USE ERP_Project_WaterFactory;

-- ============ الفروع ============
CREATE TABLE Branches (
    Id          INT IDENTITY(1,1) PRIMARY KEY,
    Name        NVARCHAR(150)   NOT NULL,
    Address     NVARCHAR(300)   NULL,
    IsActive    BIT             NOT NULL DEFAULT 1
);
GO

-- ============ الأقسام والشفتات (مرجع للموظفين) ============
CREATE TABLE Departments (
    Id      INT IDENTITY(1,1) PRIMARY KEY,
    Name    NVARCHAR(150) NOT NULL
);
GO

CREATE TABLE Shifts (
    Id                      INT IDENTITY(1,1) PRIMARY KEY,
    Name                    NVARCHAR(100)   NOT NULL,          -- مثال: الشفت الصباحي
    CheckInTime             TIME            NOT NULL,
    CheckInGraceMinutes     INT             NOT NULL DEFAULT 0,
    CheckOutTime            TIME            NOT NULL,
    CheckOutGraceMinutes    INT             NOT NULL DEFAULT 0,
    OvertimeStartsAfter     TIME            NULL                -- بعد هذا الوقت تُحسب ساعات إضافية
);
GO

-- ============ استثناء يوم الجمعة كيوم عمل إضافي ============
CREATE TABLE WorkDayExceptions (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    DepartmentId    INT NULL FOREIGN KEY REFERENCES Departments(Id),  -- NULL = كل الأقسام
    ExceptionDay    NVARCHAR(20) NOT NULL DEFAULT N'Friday',
    StartDate       DATE NOT NULL,
    EndDate         DATE NULL,                                        -- NULL = بلا نهاية
    IsActive        BIT NOT NULL DEFAULT 1
);
GO

-- ============ الموظفون ============
CREATE TABLE Employees (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    FullName        NVARCHAR(200)   NOT NULL,
    Phone           NVARCHAR(30)    NULL,
    JobTitle        NVARCHAR(150)   NULL,
    DepartmentId    INT             NULL FOREIGN KEY REFERENCES Departments(Id),
    ShiftId         INT             NULL FOREIGN KEY REFERENCES Shifts(Id),
    BranchId        INT             NULL FOREIGN KEY REFERENCES Branches(Id),
    SalaryCurrency  NVARCHAR(3)     NOT NULL DEFAULT N'IQD'
                        CHECK (SalaryCurrency IN (N'IQD', N'USD')),
    BaseSalary      DECIMAL(18,2)   NOT NULL DEFAULT 0,
    IsSalesRep      BIT             NOT NULL DEFAULT 0,        -- علامة: هل هذا الموظف مندوب مبيعات (كاش فان)
    IsSalesManager  BIT             NOT NULL DEFAULT 0,
    HireDate        DATE            NULL,
    IsActive        BIT             NOT NULL DEFAULT 1
);
GO

-- ============ أسطول النقل ============
CREATE TABLE Vehicles (
    Id                          INT IDENTITY(1,1) PRIMARY KEY,
    VehicleName                 NVARCHAR(150)   NOT NULL,
    PlateNumber                 NVARCHAR(50)    NULL,
    AssignedEmployeeId          INT             NULL FOREIGN KEY REFERENCES Employees(Id),
    DrivingLicenseExpiry        DATE            NULL,
    VehicleRegistrationExpiry   DATE            NULL,
    IsActive                    BIT             NOT NULL DEFAULT 1
);
GO

-- ============ الأدوار والصلاحيات ============
CREATE TABLE Roles (
    Id      INT IDENTITY(1,1) PRIMARY KEY,
    Name    NVARCHAR(100) NOT NULL      -- مثال: أمين مخزن، محاسب، مدير عام
);
GO

-- مصفوفة صلاحيات: دور × وحدة، بخمس صلاحيات منفصلة
CREATE TABLE RolePermissions (
    Id          INT IDENTITY(1,1) PRIMARY KEY,
    RoleId      INT             NOT NULL FOREIGN KEY REFERENCES Roles(Id),
    ModuleCode  NVARCHAR(50)    NOT NULL,   -- مثال: Warehouse, Sales, Finance, HR, Production...
    CanView     BIT NOT NULL DEFAULT 0,
    CanAdd      BIT NOT NULL DEFAULT 0,
    CanEdit     BIT NOT NULL DEFAULT 0,
    CanDelete   BIT NOT NULL DEFAULT 0,
    CanPost     BIT NOT NULL DEFAULT 0,     -- ترحيل / اعتماد
    CONSTRAINT UQ_RoleModule UNIQUE (RoleId, ModuleCode)
);
GO

-- ============ مستخدمو النظام (محليون داخل هذا المشروع) ============
CREATE TABLE Users (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    Username            NVARCHAR(100)   NOT NULL UNIQUE,
    PasswordHash        NVARCHAR(256)   NOT NULL,
    EmployeeId          INT             NULL FOREIGN KEY REFERENCES Employees(Id),
    RoleId              INT             NOT NULL FOREIGN KEY REFERENCES Roles(Id),
    PreferredLanguage   NVARCHAR(5)     NOT NULL DEFAULT N'ar',
    IsActive            BIT             NOT NULL DEFAULT 1,
    CreatedAt           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
