/* ============================================================
   المندوبون والإنتاج — إضافات على المخطط الأساسي (قابل لإعادة التنفيذ بأمان)
   ============================================================ */
SET QUOTED_IDENTIFIER ON;
GO

-- مكوّن الوصفة المخصصة يستبدل مادة محددة من الوصفة الأساسية (NULL = مكوّن إضافي)
-- مثال: لاصق "مطعم الحسون" يستبدل "اللاصق الأمامي" العادي في قائمة المواد
IF COL_LENGTH('CustomRecipeLines', 'ReplacesRawMaterialItemId') IS NULL
    ALTER TABLE CustomRecipeLines ADD ReplacesRawMaterialItemId INT NULL
        CONSTRAINT FK_CustomRecipeLines_Replaces FOREIGN KEY REFERENCES Items(Id);
GO

-- البحث عن حركات مستند (فاتورة، أمر إنتاج، تحميل سيارة...) يتكرر في كل الشاشات
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_StockTransactions_Reference')
    CREATE INDEX IX_StockTransactions_Reference ON StockTransactions (ReferenceTable, ReferenceId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_RepWalletTransactions_Employee')
    CREATE INDEX IX_RepWalletTransactions_Employee ON RepWalletTransactions (EmployeeId, TransactionDate);
GO
