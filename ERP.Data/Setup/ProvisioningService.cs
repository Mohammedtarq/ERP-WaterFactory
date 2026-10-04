using ERP.Data.ControlDb;
using ERP.Data.ControlDb.Entities;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Security;
using ERP.Data.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Setup;

public record InstallRequest(
    string ControlConnectionString,
    string ProjectName,
    string ProjectDatabaseName,
    string AdminFullName,
    string AdminUsername,
    string AdminPassword,
    bool DemoData,
    ExistingAdminPolicy ExistingAdmin = ExistingAdminPolicy.RequireSamePassword);

/// <summary>ماذا يحدث إن كان اسم دخول المدير موجودًا مسبقًا في قاعدة التحكم (من تثبيت سابق).</summary>
public enum ExistingAdminPolicy
{
    /// <summary>يُربط المشروع بالحساب فقط إن كانت كلمة المرور المدخلة هي كلمته الحالية؛ وإلا يُرفض التثبيت برسالة واضحة.</summary>
    RequireSamePassword,
    /// <summary>طلب صريح من المعالج: تُستبدل كلمة مرور الحساب الموجود بالمدخلة.</summary>
    ResetPassword,
    /// <summary>"مشروع جديد" من داخل البرنامج: المستخدم الحالي داخل أصلًا، يُربط حسابه دون كلمة مرور.</summary>
    LinkWithoutPassword
}

public record InstallResult(bool Success, string? ErrorMessage, string ProjectConnectionString, IReadOnlyList<string> Log);

/// <summary>
/// تجهيز نظام جاهز للعمل بخطوة واحدة (من معالج الإعداد أو أداة SeedTool أو "مشروع جديد"):
/// قاعدة التحكم ← قاعدة المشروع بآخر مخطط ← الإعدادات الأساسية (دليل حسابات، قواعد العقل المالي،
/// أدوار، مخازن، شفت، أوزان الحوافز) ← مستخدم المدير ← (اختياري) بيانات تجريبية.
/// كل خطوة آمنة للتكرار: تشغيله على نظام قائم يُكمل الناقص فقط ولا يكرر شيئًا.
/// </summary>
public class ProvisioningService
{
    public async Task<InstallResult> InstallAsync(InstallRequest req, IProgress<string>? progress = null)
    {
        var log = new List<string>();
        void Say(string m) { log.Add(m); progress?.Report(m); }
        string projectCs = new SqlConnectionStringBuilder(req.ControlConnectionString) { InitialCatalog = req.ProjectDatabaseName }.ConnectionString;

        if (string.IsNullOrWhiteSpace(req.ProjectName)) return Fail("أدخل اسم المشروع/الشركة");
        if (string.IsNullOrWhiteSpace(req.AdminUsername)) return Fail("أدخل اسم مستخدم المدير");
        if (req.AdminPassword.Length < 6) return Fail("كلمة مرور المدير يجب أن تكون 6 أحرف على الأقل");

        try
        {
            Say("تجهيز قاعدة التحكم المركزية...");
            await DatabaseInstaller.EnsureControlSchemaAsync(req.ControlConnectionString);

            // حساب بنفس الاسم من تثبيت سابق: يُحسم قبل إنشاء أي شيء، فلا ينتهي التثبيت "بنجاح"
            // بحساب لا تعمل معه كلمة المرور التي أُدخلت للتو
            await using (var check = new ControlDbContext(new DbContextOptionsBuilder<ControlDbContext>().UseSqlServer(req.ControlConnectionString).Options))
            {
                var existing = await check.GlobalUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Username == req.AdminUsername.Trim());
                if (existing is not null && req.ExistingAdmin == ExistingAdminPolicy.RequireSamePassword
                    && !PasswordHasher.Verify(req.AdminPassword, existing.PasswordHash))
                {
                    var controlDb = new SqlConnectionStringBuilder(req.ControlConnectionString).InitialCatalog;
                    return Fail($"اسم الدخول \"{existing.Username}\" موجود مسبقًا في قاعدة التحكم {controlDb} (من تثبيت سابق) بكلمة مرور مختلفة.\n" +
                                "إما أن تُدخل كلمة مروره الحالية ليُربط المشروع الجديد به، أو تفعّل خيار " +
                                "\"إعادة تعيين كلمة مرور الحساب الموجود\"، أو تختار اسم دخول آخر.");
                }
            }

            Say($"إنشاء قاعدة المشروع {req.ProjectDatabaseName}...");
            if (await DatabaseInstaller.EnsureDatabaseAsync(projectCs, req.ProjectDatabaseName)) Say("أُنشئت قاعدة جديدة.");
            var applied = await DatabaseInstaller.UpgradeProjectAsync(projectCs);
            Say(applied.Count == 0 ? "المخطط محدّث مسبقًا." : $"نُفّذت {applied.Count} سكربتات: {string.Join("، ", applied)}");

            await using var db = new ProjectDbContext(new DbContextOptionsBuilder<ProjectDbContext>().UseSqlServer(projectCs).Options);
            Say("الإعدادات الأساسية (الحسابات، العقل المالي، الأدوار، المخازن)...");
            var adminRole = await DefaultConfiguration.SeedAsync(db);

            var localUser = await db.Users.FirstOrDefaultAsync(u => u.Username == req.AdminUsername.Trim());
            if (localUser is not null && req.ExistingAdmin == ExistingAdminPolicy.ResetPassword)
            {
                localUser.PasswordHash = PasswordHasher.Hash(req.AdminPassword);
                await db.SaveChangesAsync();
            }
            if (localUser is null)
            {
                localUser = new User { Username = req.AdminUsername.Trim(), PasswordHash = PasswordHasher.Hash(req.AdminPassword), RoleId = adminRole.Id };
                db.Users.Add(localUser);
                await db.SaveChangesAsync();
                Say($"أُنشئ المستخدم {localUser.Username} بدور \"مدير عام\".");
            }

            if (req.DemoData)
            {
                Say("بيانات تجريبية (عملاء، أصناف، مخزون، مورد، مندوب، وصفة إنتاج)...");
                await DemoData.SeedAsync(db, localUser.Id);
            }

            Say("تسجيل المشروع والمستخدم في قاعدة التحكم...");
            await using var cdb = new ControlDbContext(new DbContextOptionsBuilder<ControlDbContext>().UseSqlServer(req.ControlConnectionString).Options);
            var server = new SqlConnectionStringBuilder(req.ControlConnectionString).DataSource;
            var project = await cdb.Projects.FirstOrDefaultAsync(p => p.DatabaseName == req.ProjectDatabaseName);
            if (project is null)
            {
                project = new Project { ProjectName = req.ProjectName.Trim(), DatabaseName = req.ProjectDatabaseName, ServerAddress = server };
                cdb.Projects.Add(project);
                await cdb.SaveChangesAsync();
            }
            var global = await cdb.GlobalUsers.FirstOrDefaultAsync(u => u.Username == req.AdminUsername.Trim());
            if (global is null)
            {
                global = new GlobalUser { FullName = req.AdminFullName.Trim(), Username = req.AdminUsername.Trim(), PasswordHash = PasswordHasher.Hash(req.AdminPassword) };
                cdb.GlobalUsers.Add(global);
                await cdb.SaveChangesAsync();
            }
            else if (req.ExistingAdmin == ExistingAdminPolicy.ResetPassword)
            {
                global.PasswordHash = PasswordHasher.Hash(req.AdminPassword);
                global.IsActive = true;
                await cdb.SaveChangesAsync();
                Say($"أُعيد تعيين كلمة مرور الحساب الموجود {global.Username}.");
            }
            if (!await cdb.UserProjectAccesses.AnyAsync(a => a.GlobalUserId == global.Id && a.ProjectId == project.Id))
            {
                cdb.UserProjectAccesses.Add(new UserProjectAccess { GlobalUserId = global.Id, ProjectId = project.Id, LocalUserIdInProject = localUser.Id });
                await cdb.SaveChangesAsync();
            }
            // تحقق نهائي بنفس طريقة شاشة الدخول: لا يُعلن النجاح إلا إن كان الدخول سيعمل فعلًا
            if (req.ExistingAdmin != ExistingAdminPolicy.LinkWithoutPassword)
            {
                var login = await new AuthService(req.ControlConnectionString).LoginAsync(req.AdminUsername, req.AdminPassword);
                if (!login.Success || login.Projects.All(p => p.DatabaseName != req.ProjectDatabaseName))
                    return Fail($"اكتمل التثبيت لكن تحقق الدخول النهائي فشل: {login.ErrorMessage ?? "المشروع غير مرتبط بالحساب"}");
                Say($"تحقق الدخول: {global.Username} يدخل إلى \"{project.ProjectName}\" ✓");
            }
            Say("اكتمل الإعداد ✓");
            return new InstallResult(true, null, projectCs, log);
        }
        catch (SqlException ex)
        {
            return Fail($"خطأ من SQL Server: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }

        InstallResult Fail(string m) => new(false, m, projectCs, log);
    }

    /// <summary>فحص الاتصال بالسيرفر قبل التثبيت.</summary>
    public static async Task<(bool ok, string message)> TestConnectionAsync(string connectionString)
    {
        try
        {
            var cs = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master", ConnectTimeout = 8 }.ConnectionString;
            await using var conn = new SqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand("SELECT SERVERPROPERTY('ProductVersion')", conn);
            return (true, $"تم الاتصال بنجاح — SQL Server {await cmd.ExecuteScalarAsync()}");
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or ArgumentException)
        {
            return (false, $"تعذّر الاتصال: {ex.Message}");
        }
    }
}

/// <summary>الإعدادات التي يحتاجها أي مشروع ليعمل من اليوم الأول.</summary>
public static class DefaultConfiguration
{
    public static readonly (string code, string name, AccountType type)[] Accounts =
    {
        ("1101", "الصندوق - النقدية", AccountType.Asset),
        ("1102", "البنك / الدفع الإلكتروني", AccountType.Asset),
        ("1103", "عهدة المندوبين", AccountType.Asset),
        ("1104", "سلف ومسحوبات الموظفين", AccountType.Asset),
        ("1201", "العملاء", AccountType.Asset),
        ("1301", "المخزون", AccountType.Asset),
        ("1302", "دفعات مقدمة للموردين", AccountType.Asset),
        ("2101", "الموردون", AccountType.Liability),
        ("2102", "ضريبة مبيعات مستحقة", AccountType.Liability),
        ("2103", "رواتب مستحقة الدفع", AccountType.Liability),
        ("2104", "تأمينات العملاء (أمانات)", AccountType.Liability),
        ("3101", "رأس المال", AccountType.Equity),
        ("3102", "جاري المالك (إيداعات وسحوبات الصندوق)", AccountType.Equity),
        ("3103", "جاري الشركاء (أرباح مستحقة)", AccountType.Equity),
        ("3104", "أرباح المطابقة الموزعة", AccountType.Equity),
        ("4101", "إيرادات المبيعات", AccountType.Revenue),
        ("4102", "إيراد مستلزمات التحميل", AccountType.Revenue),
        ("4103", "إيراد بيع مواد تالفة", AccountType.Revenue),
        ("4104", "إيرادات أخرى", AccountType.Revenue),
        ("5101", "مصروفات عمومية", AccountType.Expense),
        ("5102", "مصروف الرواتب والأجور", AccountType.Expense),
        ("5103", "مصروفات ميدانية للمندوبين", AccountType.Expense),
        ("5104", "مصروفات غير تشغيلية (توسعة ومكائن)", AccountType.Expense),
    };

    /// <summary>قواعد العقل المالي: نوع العملية ← (مدين، دائن).</summary>
    public static readonly (string type, string debit, string credit)[] Rules =
    {
        ("CashReceiptVoucher", "1101", "1201"),
        ("CashPaymentVoucher", "5101", "1101"),
        ("SupplierPaymentVoucher", "2101", "1101"),
        ("SupplierAdvancePayment", "1302", "1101"),
        ("GoodsReceiptOnAccount", "1301", "2101"),
        ("SupplierAdvanceOffset", "2101", "1302"),
        ("SalesInvoiceCash", "1101", "4101"),
        ("SalesInvoiceCredit", "1201", "4101"),
        ("SalesInvoiceElectronic", "1102", "4101"),
        ("SalesInvoiceRepCash", "1103", "4101"),
        ("SalesTax", "1201", "2102"),
        ("LoadingSuppliesCharge", "1201", "4102"),
        (HrRules.PayrollMappingRule, "5102", "2103"),
        (RepsService.FieldExpenseRule, "5103", "1103"),
        (RepsService.CashHandoverRule, "1101", "1103"),
        (RepsService.DebtCollectionRule, "1103", "1201"),
        (CashBoxService.DepositRule, "1101", "3102"),
        (CashBoxService.WithdrawalRule, "5101", "1101"),
        (CustomerDepositService.ReceiptRule, "1101", "2104"),
        (CustomerDepositService.RefundRule, "2104", "1101"),
        (CustomerDepositService.OpeningRule, "3101", "2104"),
        (EmployeeDeductionService.PayoutRule, "1104", "1101"),
        (EmployeeDeductionService.OpeningRule, "1104", "3101"),
        (ReconciliationService.ProfitShareRule, "3104", "3103"),
        (ReconciliationService.WithdrawalRule, "3103", "1101"),
        (ReconciliationService.OpeningRule, "3101", "3103"),
        (DamagedSaleService.CashRule, "1101", "4103"),
        (CashBoxService.OpeningRule, "1101", "3101"),
        (Import.RahmaImporter.CustomerOpeningRule, "1201", "3101"),
        (Import.RahmaImporter.CustomerCreditOpeningRule, "3101", "1201"),
        (Import.RahmaImporter.SupplierOpeningRule, "3101", "2101"),
        (Import.RahmaImporter.SupplierAdvanceOpeningRule, "1302", "3101"),
        (FinanceEntryService.ExpenseRule, "5101", "1101"),
        (FinanceEntryService.NonOperatingRule, "5104", "1101"),
        (FinanceEntryService.OtherIncomeRule, "1101", "4104"),
        (TempWorkersService.WagesRule, "5102", "1101"),
    };

    private static readonly string[] AllModules =
    {
        ModuleCode.Dashboard, ModuleCode.Warehouse, ModuleCode.Sales, ModuleCode.Suppliers, ModuleCode.Finance,
        ModuleCode.HR, ModuleCode.Reps, ModuleCode.Production, ModuleCode.SystemSettings
    };

    /// <summary>الأدوار الجاهزة: (الوحدة، عرض، إضافة، تعديل، حذف، ترحيل). قابلة للتعديل من مصفوفة الصلاحيات.</summary>
    private static readonly Dictionary<string, (string module, bool v, bool a, bool e, bool d, bool p)[]> Roles = new()
    {
        ["محاسب"] = new[]
        {
            (ModuleCode.Dashboard, true, false, false, false, false), (ModuleCode.Finance, true, true, true, true, true),
            (ModuleCode.Sales, true, true, true, false, true), (ModuleCode.Suppliers, true, true, true, false, true),
            (ModuleCode.HR, true, false, false, false, true), (ModuleCode.Reps, true, true, true, false, true),
        },
        ["أمين مخزن"] = new[]
        {
            (ModuleCode.Dashboard, true, false, false, false, false), (ModuleCode.Warehouse, true, true, true, false, false),
            (ModuleCode.Suppliers, true, true, false, false, false), (ModuleCode.Production, true, true, true, false, false),
        },
        ["موظف مبيعات"] = new[]
        {
            (ModuleCode.Dashboard, true, false, false, false, false), (ModuleCode.Sales, true, true, true, false, false),
        },
        ["مسؤول الموارد البشرية"] = new[]
        {
            (ModuleCode.Dashboard, true, false, false, false, false), (ModuleCode.HR, true, true, true, true, false),
        },
        ["مسؤول الإنتاج والمختبر"] = new[]
        {
            (ModuleCode.Dashboard, true, false, false, false, false), (ModuleCode.Production, true, true, true, false, true),
            (ModuleCode.Warehouse, true, false, false, false, false),
        },
        ["أمين صندوق"] = new[]
        {
            (ModuleCode.Dashboard, true, false, false, false, false), (ModuleCode.Finance, true, true, false, false, true),
            (ModuleCode.Reps, true, true, false, false, true), (ModuleCode.Sales, true, false, false, false, false),
        },
        // قراءة فقط: الحسابات الختامية والمطابقة وحصته
        ["شريك"] = new[]
        {
            (ModuleCode.Dashboard, true, false, false, false, false), (ModuleCode.Finance, true, false, false, false, false),
            (SpecialPermission.FinalAccounts, true, false, false, false, false), (SpecialPermission.CostAndProfit, true, false, false, false, false),
        },
        // قراءة فقط لكل الوحدات والسجل، للتدقيق الدوري
        ["مراجع"] = AllModules.Where(m => m != ModuleCode.SystemSettings).Select(m => (m, true, false, false, false, false))
            .Concat(new[]
            {
                (SpecialPermission.AuditLog, true, false, false, false, false), (SpecialPermission.AllCashBoxes, true, false, false, false, false),
                (SpecialPermission.FinalAccounts, true, false, false, false, false), (SpecialPermission.CostAndProfit, true, false, false, false, false),
            }).ToArray(),
    };

    /// <summary>
    /// يُكمل الناقص فقط. يعيد دور "مدير عام". عند الترقية التلقائية لقاعدة قائمة (includeWarehouses = false)
    /// لا تُنشأ مخازن جديدة، فقط الحسابات والقواعد والأدوار الناقصة.
    /// </summary>
    public static async Task<Role> SeedAsync(ProjectDbContext db, bool includeWarehouses = true)
    {
        // ---- الأدوار ----
        var admin = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Name == "مدير عام");
        if (admin is null) db.Roles.Add(admin = new Role { Name = "مدير عام" });
        foreach (var m in AllModules.Where(m => admin.Permissions.All(p => p.ModuleCode != m)))
            admin.Permissions.Add(new RolePermission { ModuleCode = m, CanView = true, CanAdd = true, CanEdit = true, CanDelete = true, CanPost = true });
        // المدير يملك كل الصلاحيات الخاصة (الكلفة والأرباح، إغلاق الشهر، الإلغاء...)
        foreach (var (code, _, _) in SpecialPermission.All.Where(sp => admin.Permissions.All(p => p.ModuleCode != sp.Code)))
            admin.Permissions.Add(new RolePermission { ModuleCode = code, CanView = true });
        foreach (var (name, perms) in Roles)
        {
            if (await db.Roles.AnyAsync(r => r.Name == name)) continue;
            var role = new Role { Name = name };
            foreach (var (m, v, a, e, d, p) in perms)
                role.Permissions.Add(new RolePermission { ModuleCode = m, CanView = v, CanAdd = a, CanEdit = e, CanDelete = d, CanPost = p });
            db.Roles.Add(role);
        }
        await db.SaveChangesAsync();

        // ---- دليل الحسابات وقواعد العقل المالي ----
        var existing = await db.ChartOfAccounts.ToDictionaryAsync(a => a.AccountCode);
        foreach (var (code, name, type) in Accounts.Where(a => !existing.ContainsKey(a.code)))
            db.ChartOfAccounts.Add(existing[code] = new ChartOfAccount { AccountCode = code, AccountName = name, AccountType = type });
        await db.SaveChangesAsync();
        var ruleTypes = await db.AccountMappingRules.Select(r => r.TransactionType).ToListAsync();
        foreach (var (type, dr, cr) in Rules.Where(r => !ruleTypes.Contains(r.type)))
            db.AccountMappingRules.Add(new AccountMappingRule { TransactionType = type, DebitAccountId = existing[dr].Id, CreditAccountId = existing[cr].Id });

        await db.SaveChangesAsync();

        // ---- الفرع والمخازن الأساسية (عند التثبيت فقط، أو إن لم يوجد أي فرع) ----
        var branch = await db.Branches.OrderBy(b => b.Id).FirstOrDefaultAsync();
        if (includeWarehouses || branch is null)
        {
            if (branch is null)
            {
                db.Branches.Add(branch = new Branch { Name = "الفرع الرئيسي" });
                await db.SaveChangesAsync();
            }
            foreach (var (type, name, sellable) in new[]
                     {
                         (WarehouseType.FinishedGoods, "مخزن المنتج التام", true),
                         (WarehouseType.RawMaterial, "مخزن المواد الأولية", false),
                         (WarehouseType.Damaged, "مخزن التالف", false),
                     })
                if (!await db.Warehouses.AnyAsync(w => w.WarehouseType == type))
                    db.Warehouses.Add(new Warehouse { BranchId = branch.Id, Name = name, WarehouseType = type, IsSellableStock = sellable });
        }

        // ---- الموارد البشرية ----
        // الصندوق الرئيسي الافتراضي (يستقبل المبيعات النقدية إن لم يكن للمستخدم صندوق خاص)
        if (!await db.CashBoxes.AnyAsync())
            db.CashBoxes.Add(new CashBox { Name = "الصندوق الرئيسي", BoxType = CashBoxType.Main, IsDefault = true });

        if (!await db.Shifts.AnyAsync())
            db.Shifts.Add(new Shift { Name = "الشفت الصباحي", CheckInTime = new TimeSpan(8, 0, 0), CheckInGraceMinutes = 10,
                                      CheckOutTime = new TimeSpan(16, 0, 0), CheckOutGraceMinutes = 10 });
        if (!await db.IncentiveScoreWeights.AnyAsync()) db.IncentiveScoreWeights.Add(new IncentiveScoreWeights());
        await db.SaveChangesAsync();

        // ---- أي صنف بلا وحدة بيع يحصل على "قطعة" حتى يظهر في الفواتير ----
        var noLevels = await db.Items.Where(i => !db.ItemPackagingLevels.Any(p => p.ItemId == i.Id)).ToListAsync();
        foreach (var i in noLevels)
            db.ItemPackagingLevels.Add(new ItemPackagingLevel { ItemId = i.Id, LevelName = i.BaseUnitName, ContainsQuantity = 1, EquivalentBaseUnits = 1 });
        await db.SaveChangesAsync();
        return admin;
    }
}

/// <summary>بيانات تجريبية لمصنع مياه — تُضاف فقط إن لم توجد أصناف أو عملاء.</summary>
public static class DemoData
{
    public static async Task SeedAsync(ProjectDbContext db, int userId)
    {
        var fg = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.FinishedGoods);
        var raw = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.RawMaterial);

        if (!await db.Customers.AnyAsync())
        {
            var agent = new Customer { Name = "وكيل الزبير", CustomerType = CustomerType.Agent, Province = "البصرة" };
            db.Customers.AddRange(agent, new Customer { Name = "زبون مباشر", CustomerType = CustomerType.Direct, Province = "البصرة" });
            await db.SaveChangesAsync();
            db.Customers.Add(new Customer { Name = "محل أبو حيدر", CustomerType = CustomerType.SubCustomer, ParentAgentId = agent.Id, Province = "البصرة" });
        }
        if (!await db.Suppliers.AnyAsync())
            db.Suppliers.Add(new Supplier { Name = "شركة الأهرام للتوريدات", Phone = "07701234567", DefaultPaymentTerms = SupplierPaymentTerms.Credit });
        if (!await db.LoadingSuppliesSettings.AnyAsync())
            db.LoadingSuppliesSettings.Add(new LoadingSuppliesSetting { RatePerPiece = 10, EffectiveDate = new DateTime(DateTime.Today.Year, 1, 1) });
        await db.SaveChangesAsync();

        if (await db.Items.AnyAsync(i => i.ItemCode == "W-500")) return;

        async Task<Item> AddItem(string code, string name, decimal price, SourcingMethod src, decimal? alert,
                                 Data.ProjectDb.Entities.Warehouse wh, decimal qty, string? level = null, decimal per = 0)
        {
            var item = new Item { ItemCode = code, ItemName = name, SalePrice = price, SourcingMethod = src, MinStockAlertLevel = alert };
            db.Items.Add(item);
            await db.SaveChangesAsync();
            var piece = new ItemPackagingLevel { ItemId = item.Id, LevelName = "قطعة", ContainsQuantity = 1, EquivalentBaseUnits = 1, IsSellableUnit = src != SourcingMethod.Purchased };
            db.ItemPackagingLevels.Add(piece);
            await db.SaveChangesAsync();
            if (level is not null)
                db.ItemPackagingLevels.Add(new ItemPackagingLevel { ItemId = item.Id, LevelName = level, ParentLevelId = piece.Id, ContainsQuantity = per, EquivalentBaseUnits = per });
            var batch = new ItemBatch { ItemId = item.Id, BatchNumber = $"{code}-OPEN", ManufactureDate = DateTime.Today, ExpiryDate = DateTime.Today.AddMonths(12) };
            db.ItemBatches.Add(batch);
            await db.SaveChangesAsync();
            db.StockTransactions.Add(new StockTransaction { ItemId = item.Id, WarehouseId = wh.Id, BatchId = batch.Id, QuantityBaseUnits = qty,
                                                            TransactionType = StockTransactionType.Receipt, ReferenceTable = "OpeningBalance", CreatedByUserId = userId });
            await db.SaveChangesAsync();
            return item;
        }

        var w500 = await AddItem("W-500", "ماء 500 مل", 250, SourcingMethod.Manufactured, 240, fg, 2400, "كارتون", 12);
        var w1500 = await AddItem("W-1500", "ماء 1.5 لتر", 500, SourcingMethod.Manufactured, 120, fg, 1200, "شرنك", 6);
        var preform = await AddItem("RM-PRE", "قالب بلاستيك (بريفورم) 500 مل", 0, SourcingMethod.Purchased, 2000, raw, 20000);
        var cap = await AddItem("RM-CAP", "غطاء قنينة", 0, SourcingMethod.Purchased, 2000, raw, 20000);
        var label = await AddItem("RM-LBL", "لاصق أمامي", 0, SourcingMethod.Purchased, 2000, raw, 20000);

        var agentId = await db.Customers.Where(c => c.CustomerType == CustomerType.Agent).Select(c => c.Id).FirstOrDefaultAsync();
        if (agentId != 0)
            db.AgentItemPrices.AddRange(new AgentItemPrice { CustomerId = agentId, ItemId = w500.Id, AgentPrice = 200 },
                                        new AgentItemPrice { CustomerId = agentId, ItemId = w1500.Id, AgentPrice = 400 });

        var bom = new BillOfMaterials { FinishedItemId = w500.Id };
        bom.Lines.Add(new BOMLine { RawMaterialItemId = preform.Id, QuantityPerUnit = 1 });
        bom.Lines.Add(new BOMLine { RawMaterialItemId = cap.Id, QuantityPerUnit = 1 });
        bom.Lines.Add(new BOMLine { RawMaterialItemId = label.Id, QuantityPerUnit = 1 });
        db.BillOfMaterials.Add(bom);
        db.QualityTests.AddRange(
            new QualityTest { TestName = "درجة الحموضة pH", StandardMin = 6.5m, StandardMax = 8.5m },
            new QualityTest { TestName = "الأملاح الذائبة TDS (ppm)", StandardMin = 50, StandardMax = 250 },
            new QualityTest { TestName = "إحكام الغطاء", StandardText = "سليم" });

        // مندوب بكاش فان وحافز لكل قطعة
        var shift = await db.Shifts.FirstAsync();
        var rep = new Employee { FullName = "علي المندوب", JobTitle = "مندوب مبيعات", IsSalesRep = true, BaseSalary = 600_000, ShiftId = shift.Id, HireDate = DateTime.Today.AddYears(-1) };
        db.Employees.Add(rep);
        await db.SaveChangesAsync();
        db.Warehouses.Add(new Data.ProjectDb.Entities.Warehouse { BranchId = fg.BranchId, Name = "كاش فان علي", WarehouseType = WarehouseType.RepVan, OwnerEmployeeId = rep.Id });
        db.RepItemIncentiveRates.AddRange(new RepItemIncentiveRate { ItemId = w500.Id, IncentiveRatePerUnit = 5 },
                                          new RepItemIncentiveRate { ItemId = w1500.Id, IncentiveRatePerUnit = 10 });
        db.RepTerritories.Add(new RepTerritory { EmployeeId = rep.Id, TerritoryName = "الزبير" });
        await db.SaveChangesAsync();

        // ماكينات الخط الأول (لكل ماكينة رصيد تحت تصنيع خاص بها)
        var machines = new MachineService(db);
        await machines.SaveAsync(null, "نافخة 1", "نفخ", "الخط الأول", null, true);
        await machines.SaveAsync(null, "تعبئة 1", "تعبئة", "الخط الأول", null, true);

        // قوالب التعبئة (الكارتون/الشرنك بلا مادة افتراضية: تُختار عند التطبيق)
        var templates = new PackagingTemplateService(db);
        await templates.SaveTemplateAsync(null, "330×40 كارتون", "كارتون يحوي 40 قنينة", new[]
        {
            new TemplateLineInput("كارتون", null, 1, 40), new TemplateLineInput("امبولة", preform.Id, 1, 1),
            new TemplateLineInput("غطاء", cap.Id, 1, 1), new TemplateLineInput("لاصق", label.Id, 2, 1)
        });
        await templates.SaveTemplateAsync(null, "330×20 شرنك", "شرنك نايلون يحوي 20 قنينة", new[]
        {
            new TemplateLineInput("شرنك", null, 1, 20), new TemplateLineInput("امبولة", preform.Id, 1, 1),
            new TemplateLineInput("غطاء", cap.Id, 1, 1), new TemplateLineInput("لاصق", label.Id, 1, 1)
        });
    }
}
