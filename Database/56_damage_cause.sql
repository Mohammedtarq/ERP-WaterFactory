/* ============================================================
   سبب التلف بجانب مكانه (قرار الفحص 1.13 — docs/agreed-pricing-and-names.md قسم «ي»)
   - DamageReason يبقى «المكان» (نقل / مخزن / إنتاج / ميداني).
   - DamageCause «السبب»: PoorStorage (سوء خزن)، Leakage (تسرب)، Expired (انتهاء صلاحية)،
     ManufacturingDefect (خطأ تصنيع)، Misprint (طباعة خاطئة)، Other (أخرى).
   - إلزامي لمستند التالف اليدوي الجديد؛ الحركات القديمة تبقى بلا سبب.
   قابل لإعادة التنفيذ بأمان.
   ============================================================ */
IF COL_LENGTH('StockDocuments', 'DamageCause') IS NULL
    ALTER TABLE StockDocuments ADD DamageCause NVARCHAR(30) NULL;
GO
IF COL_LENGTH('StockTransactions', 'DamageCause') IS NULL
    ALTER TABLE StockTransactions ADD DamageCause NVARCHAR(30) NULL;
GO
