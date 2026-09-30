# نظام ERP — مصنع المياه (البصرة)

تطبيق سطح مكتب WPF (.NET 9) + SQL Server، بتصميم متعدد المشاريع: قاعدة تحكم مركزية واحدة (`ERP_ControlDB`) + قاعدة مستقلة لكل مشروع/عميل.

## هيكل المستودع

```
ERP.Solution.sln
ERP.Data/                  مكتبة الوصول للبيانات (EF Core) — الكيانات، السياقات، الخدمات، التشفير
  ControlDb/  ProjectDb/  Services/  Security/
ERP.Desktop/               التطبيق الفعلي (WPF)
ERP.SeedTool/              أداة تُشغَّل مرة واحدة لإنشاء مستخدم وبيانات تجريبية
Database/                  سكربتات SQL (تُنفَّذ بالترتيب)
tests/                     اختبارات SQL + اختبارات تكامل C# على SQL Server حقيقي
```

## المتطلبات قبل التشغيل
- Visual Studio 2022 (أو أحدث) مع حمل عمل ".NET Desktop Development"
- .NET 9 SDK
- SQL Server (محلي يكفي للتجربة) + SQL Server Management Studio

## خطوات التشغيل بالترتيب

### 1) إنشاء قواعد البيانات
نفّذ في SSMS:
1. `Database/00_control_db.sql` — ينشئ **`ERP_ControlDB`** (مرة واحدة فقط).
2. أنشئ قاعدة بيانات فارغة باسم **`ERP_Project_WaterFactory`**، ثم نفّذ عليها الملفات من `01` حتى `10` بالترتيب.

| # | الملف | المحتوى |
|---|---|---|
| 0 | `00_control_db.sql` | قاعدة التحكم المركزية (مشتركة بين كل المشاريع) |
| 1 | `01_core_and_security.sql` | فروع، أقسام، شفتات، موظفون، سيارات، أدوار وصلاحيات، مستخدمون |
| 2 | `02_finance.sql` | دليل الحسابات، قواعد الربط التلقائي، القيود، السندات، أسعار الصرف |
| 3 | `03_items_warehouses.sql` | الأصناف، التعبيئة، المخازن، المواقع، التشغيلات، سجل حركة المخزون |
| 4 | `04_suppliers_purchasing.sql` | الموردون، أمر الشراء، الاستلام |
| 5 | `05_sales_customers.sql` | العملاء/الوكلاء، تسعير الوكلاء، فاتورة المبيعات |
| 6 | `06_hr_payroll.sql` | الحضور، الرواتب، الترقيات، الحوافز |
| 7 | `07_reps.sql` | محفظة المندوب، المناطق، الزبائن المخصصون |
| 8 | `08_production.sql` | BOM، الوصفات المخصصة، أمر الإنتاج، المختبر، أمر التعبيئة |
| 9 | `09_sales_logic.sql` | **منطق المبيعات**: التسعير الهرمي، الترحيل الذري (مخزون + قيد + محفظة مندوب)، كشف حساب العميل |
| 10 | `10_items_barcode_alert.sql` | عمودا `BarCode` و `MinStockAlertLevel` في `Items` (يطابق `Item.cs`) — آمن للتنفيذ على قاعدة قائمة |

> **على قاعدة موجودة مسبقًا**: يكفي تنفيذ `09` ثم `10` فقط.

### 2) فتح الحل
افتح `ERP.Solution.sln` في Visual Studio.

### 3) تعبئة بيانات تجريبية
عدّل سلسلتي الاتصال أعلى `ERP.SeedTool/Program.cs` إن لزم، ثم: **زر يمين على ERP.SeedTool ← Set as Startup Project ← F5**. الأداة آمنة للتشغيل أكثر من مرة، وتنشئ:
- دور "مدير عام" بكل الصلاحيات + مستخدم `admin` / `Admin@123`
- دليل حسابات + قواعد الربط (السندات، المشتريات، **والمبيعات الست**)
- مورد، فرع، مخزن، صنف برصيد افتتاحي، و**3 عملاء** (وكيل + عميل فرعي تابع له + زبون مباشر)

### 4) تشغيل التطبيق
عدّل `ERP.Desktop/appsettings.json` إن احتجت، ثم **Set as Startup Project ← ERP.Desktop ← F5**.

## وحدة المبيعات

كل قواعد العمل مطبّقة داخل قاعدة البيانات (`09_sales_logic.sql`)، والتطبيق يستدعيها عبر `ERP.Data/Services/SalesService.cs` — فلا يمكن لأي واجهة تجاوزها.

| في C# (`SalesService`) | في SQL | الغرض |
|---|---|---|
| `CreateInvoiceAsync` | `sp_Sales_CreateInvoice` | فاتورة مسودة؛ يتحقق من العميل، المخزن القابل للبيع، الجهة المستفيدة للمجانية، ويحدد المندوب تلقائيًا من الكاش فان |
| `AddLineAsync` / `DeleteLineAsync` | `sp_Sales_AddInvoiceLine` / `sp_Sales_DeleteInvoiceLine` | السعر يُحسب تلقائيًا إن لم يُمرَّر (سعر القطعة × عدد القطع في وحدة البيع) |
| `PostInvoiceAsync` | `sp_Sales_PostInvoice` | **ترحيل ذري**: مجاميع + ضريبة + مستلزمات تحميل ← خصم مخزون FIFO حسب الصلاحية ← قيد متوازن مرحّل ← محفظة المندوب |
| `DeleteDraftInvoiceAsync` | `sp_Sales_DeleteDraftInvoice` | المسودات فقط؛ المرحّل لا يُحذف |
| `GetSuggestedUnitPriceAsync` | `fn_Sales_BaseUnitPrice` | وكيل ← سعره الخاص؛ عميل فرعي ← سعر وكيله الأب؛ غير ذلك ← سعر البيع العادي |
| `GetInvoiceListAsync` | `vw_SalesInvoiceList` | قائمة الفواتير مع المجاميع والمتبقي |
| `GetCustomerBalancesAsync` / `GetCustomerStatementAsync` | `vw_CustomerBalances` / `vw_CustomerStatement` | رصيد وكشف حساب مستقل لكل عميل (مع رصيد تراكمي) |

الأخطاء تعود كـ `FinanceOperationResult.Fail` برسائل عربية جاهزة للعرض (رصيد غير كافٍ مع الكميات، صلاحية ناقصة، قاعدة ربط ناقصة...) ولا يبقى أي أثر جزئي.

**قواعد الربط المحاسبي المطلوبة** (`AccountMappingRules.TransactionType`): `SalesInvoiceCash`، `SalesInvoiceCredit`، `SalesInvoiceElectronic`، `SalesInvoiceRepCash`، `SalesTax` (الدائن فقط)، `LoadingSuppliesCharge` (الدائن فقط) — تنشئها أداة التزويد تلقائيًا.

**الصلاحيات**: وحدة `Sales` في `RolePermissions` (Add / Edit / Delete / Post) — موظف بلا `CanPost` ينشئ المسودة ولا يرحّلها.

**المبيعات المجانية**: حركة `FreeIssue` (أو `RepFreeSale` من الكاش فان) باسم الجهة المستفيدة، بلا قيد مالي وبلا أثر على رصيد العميل.

## الاختبارات

```bash
./tests/run_tests.sh      # يتطلب Docker فقط
```
يشغّل SQL Server 2022 و .NET 9 في Docker، ثم:
1. ينفّذ `Database/00 → 10` على قاعدة نظيفة و`tests/test_sales.sql` (58 فحصًا: التسعير، الضريبة، التحميل، FIFO، الذرّية عند الخطأ، المجانية، الكاش فان، الصلاحيات، توازن القيود).
2. يبني `ERP.Data` و`ERP.SeedTool` ويشغّل `tests/ERP.Data.IntegrationTests` (xUnit) — يعبّئ البيانات عبر EF فيثبت أيضًا تطابق الكيانات مع الجداول.

## ملاحظات تصميمية

- **الأرصدة كسجل حركة (Ledger) لا كرقم مخزَّن**: `StockTransactions` مصدر الحقيقة الوحيد لأي رصيد مخزون، و`RepWalletTransactions` لمحفظة المندوب.
- **مفتاح مؤجل بين 03 و 08** (`ItemBatches.ProductionOrderId`) رُبط عبر `ALTER TABLE` في نهاية 08.
- **الحقول من نوع Enum** نُفِّذت كـ `NVARCHAR` + `CHECK CONSTRAINT`، وتُحوَّل في EF عبر `HasConversion<string>()`.
- **ترقيم المبيعات** بتسلسلات SQL (`SI-2026-000001`، وقيودها `SJ-2026-000001`) — آمن مع المستخدمين المتزامنين.

## عند ظهور أي خطأ
انسخ رسالة الخطأ **كاملة كما ظهرت** وأرسلها.
