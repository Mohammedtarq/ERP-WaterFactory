/* ============================================================
   الإنتاج الإضافي على أمر/دفعة قائمة (يسجَّل من أوامر الإنتاج، ويوافق عليه أمين المخزن)
   - ExtraProductionRequests: طلب زيادة بعدد العبوات على سطر أمر (صنف + متغير) بسبب إلزامي
   - الموافقة تصرف المواد الأولية الزائدة وتُدخل الناتج مخزن المنتج التام على تشغيلة الأمر نفسها دون تغيير كمية الأمر ولا فحصه
   - الموافقة كاملة أو جزئية، والرفض بسبب
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */

IF OBJECT_ID('ExtraProductionRequests', 'U') IS NULL
CREATE TABLE ExtraProductionRequests (
    Id                    INT IDENTITY(1,1) PRIMARY KEY,
    ProductionOrderId     INT            NOT NULL FOREIGN KEY REFERENCES ProductionOrders(Id),
    ProductionOrderLineId INT            NOT NULL FOREIGN KEY REFERENCES ProductionOrderLines(Id),
    PackagingLevelId      INT            NOT NULL FOREIGN KEY REFERENCES ItemPackagingLevels(Id),
    Packs                 DECIMAL(18,3)  NOT NULL CONSTRAINT CK_ExtraProd_Packs CHECK (Packs > 0),
    Pieces                DECIMAL(18,3)  NOT NULL CONSTRAINT CK_ExtraProd_Pieces CHECK (Pieces > 0),
    Reason                NVARCHAR(300)  NOT NULL,
    Status                NVARCHAR(10)   NOT NULL CONSTRAINT CK_ExtraProd_Status CHECK (Status IN (N'Pending', N'Approved', N'Partial', N'Rejected')),
    ApprovedPacks         DECIMAL(18,3)  NULL,
    RequestedByUserId     INT            NOT NULL FOREIGN KEY REFERENCES Users(Id),
    RequestedAt           DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME(),
    DecidedByUserId       INT            NULL FOREIGN KEY REFERENCES Users(Id),
    DecidedAt             DATETIME2      NULL,
    DecisionNote          NVARCHAR(300)  NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ExtraProd_Status')
    CREATE INDEX IX_ExtraProd_Status ON ExtraProductionRequests (Status, ProductionOrderId);
GO
