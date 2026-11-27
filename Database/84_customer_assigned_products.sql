/* ============================================================
   «المنتجات الظاهرة عند البيع» لكل زبون
   - Customers.AssignedProductsOnly: 0 = كل المنتجات (السلوك السابق، الافتراضي)،
     1 = المخصص له فقط: في قائمة التشغيلة بفاتورة البيع يظهر متغيره الخاص (ليبله) والمتغيرات العامة (مناسبات بلا عميل)
     والتشغيلات العادية، ولا تظهر متغيرات الزبائن الآخرين.
   لا يغيّر المخزون ولا الأسعار ولا الصلاحيات. قابل لإعادة التنفيذ بأمان.
   ============================================================ */
IF COL_LENGTH('Customers', 'AssignedProductsOnly') IS NULL
    ALTER TABLE Customers ADD AssignedProductsOnly BIT NOT NULL CONSTRAINT DF_Customers_AssignedProductsOnly DEFAULT 0;
GO
