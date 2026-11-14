/* ============================================================
   تعديل بنود المطابقة قبل الاعتماد (للأدمن)
   - AssetReconciliationLines: IsManual / IsExcluded / OriginalValue / AdjustReason / AdjustedByUserId
   - AssetReconciliations: ManualAssets / ManualLiabilities / AdjustmentsDelta
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */
IF COL_LENGTH('AssetReconciliationLines', 'IsManual') IS NULL
    ALTER TABLE AssetReconciliationLines ADD IsManual BIT NOT NULL CONSTRAINT DF_ARL_IsManual DEFAULT 0;
IF COL_LENGTH('AssetReconciliationLines', 'IsExcluded') IS NULL
    ALTER TABLE AssetReconciliationLines ADD IsExcluded BIT NOT NULL CONSTRAINT DF_ARL_IsExcluded DEFAULT 0;
IF COL_LENGTH('AssetReconciliationLines', 'OriginalValue') IS NULL
    ALTER TABLE AssetReconciliationLines ADD OriginalValue DECIMAL(18,2) NULL;
IF COL_LENGTH('AssetReconciliationLines', 'AdjustReason') IS NULL
    ALTER TABLE AssetReconciliationLines ADD AdjustReason NVARCHAR(250) NULL;
IF COL_LENGTH('AssetReconciliationLines', 'AdjustedByUserId') IS NULL
    ALTER TABLE AssetReconciliationLines ADD AdjustedByUserId INT NULL;
IF COL_LENGTH('AssetReconciliations', 'ManualAssets') IS NULL
    ALTER TABLE AssetReconciliations ADD ManualAssets DECIMAL(18,2) NOT NULL CONSTRAINT DF_AR_ManualAssets DEFAULT 0;
IF COL_LENGTH('AssetReconciliations', 'ManualLiabilities') IS NULL
    ALTER TABLE AssetReconciliations ADD ManualLiabilities DECIMAL(18,2) NOT NULL CONSTRAINT DF_AR_ManualLiabilities DEFAULT 0;
IF COL_LENGTH('AssetReconciliations', 'AdjustmentsDelta') IS NULL
    ALTER TABLE AssetReconciliations ADD AdjustmentsDelta DECIMAL(18,2) NOT NULL CONSTRAINT DF_AR_AdjustmentsDelta DEFAULT 0;
GO
