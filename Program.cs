using ERP.Data.ControlDb;
using ERP.Data.ControlDb.Entities;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Security;
using Microsoft.EntityFrameworkCore;

// ============================================================
// أداة تزويد بيانات تجريبية — تُشغَّل مرة واحدة فقط من سطر الأوامر
// (dotnet run --project ERP.SeedTool) لإنشاء مستخدم تجريبي كامل
// يمكن به تجربة تدفق: تسجيل الدخول ← اختيار المشروع ← الشريط الجانبي.
//
// عدّل سلسلتي الاتصال أدناه لتطابقا قواعد بياناتك المحلية التجريبية
// (ERP_ControlDB وقاعدة مشروع واحدة نفّذتَ عليها ملفات 01 حتى 08).
// ============================================================

const string controlDbConnectionString =
    "Server=localhost;Database=ERP_ControlDB;Trusted_Connection=True;TrustServerCertificate=True;";

const string projectDbConnectionString =
    "Server=localhost;Database=ERP_Project_WaterFactory;Trusted_Connection=True;TrustServerCertificate=True;";

const string testUsername = "admin";
const string testPassword = "Admin@123";

var controlOptions = new DbContextOptionsBuilder<ControlDbContext>()
    .UseSqlServer(controlDbConnectionString).Options;

var projectOptions = new DbContextOptionsBuilder<ProjectDbContext>()
    .UseSqlServer(projectDbConnectionString).Options;

// ---------- الخطوة 1: إنشاء دور "مدير عام" بكل الصلاحيات داخل قاعدة المشروع ----------
await using (var projectDb = new ProjectDbContext(projectOptions))
{
    var role = await projectDb.Roles.FirstOrDefaultAsync(r => r.Name == "مدير عام");
    if (role is null)
    {
        role = new Role { Name = "مدير عام" };
        projectDb.Roles.Add(role);
        await projectDb.SaveChangesAsync();

        string[] allModules =
        {
            ModuleCode.Dashboard, ModuleCode.Warehouse, ModuleCode.Sales, ModuleCode.Suppliers,
            ModuleCode.Finance, ModuleCode.HR, ModuleCode.Reps, ModuleCode.Production, ModuleCode.SystemSettings
        };

        foreach (var module in allModules)
        {
            projectDb.RolePermissions.Add(new RolePermission
            {
                RoleId = role.Id,
                ModuleCode = module,
                CanView = true,
                CanAdd = true,
                CanEdit = true,
                CanDelete = true,
                CanPost = true
            });
        }

        await projectDb.SaveChangesAsync();
        Console.WriteLine("تم إنشاء دور \"مدير عام\" بكل الصلاحيات.");
    }

    // ---------- الخطوة 1-ب: دليل حسابات أولي بسيط + قاعدة ربط تجريبية للسندات ----------
    if (!await projectDb.ChartOfAccounts.AnyAsync())
    {
        var cash = new ChartOfAccount { AccountCode = "1101", AccountName = "الصندوق - النقدية", AccountType = AccountType.Asset };
        var customers = new ChartOfAccount { AccountCode = "1201", AccountName = "العملاء", AccountType = AccountType.Asset };
        var inventory = new ChartOfAccount { AccountCode = "1301", AccountName = "المخزون", AccountType = AccountType.Asset };
        var advanceToSuppliers = new ChartOfAccount { AccountCode = "1302", AccountName = "دفعات مقدمة للموردين", AccountType = AccountType.Asset };
        var suppliers = new ChartOfAccount { AccountCode = "2101", AccountName = "الموردون", AccountType = AccountType.Liability };
        var salesRevenue = new ChartOfAccount { AccountCode = "4101", AccountName = "إيرادات المبيعات", AccountType = AccountType.Revenue };
        var generalExpense = new ChartOfAccount { AccountCode = "5101", AccountName = "مصروفات عمومية", AccountType = AccountType.Expense };

        projectDb.ChartOfAccounts.AddRange(cash, customers, inventory, advanceToSuppliers, suppliers, salesRevenue, generalExpense);
        await projectDb.SaveChangesAsync();

        // سند قبض نقدي: مدين الصندوق / دائن العملاء (تحصيل دين مثلاً)
        projectDb.AccountMappingRules.Add(new AccountMappingRule
        {
            TransactionType = "CashReceiptVoucher",
            DebitAccountId = cash.Id,
            CreditAccountId = customers.Id
        });
        // سند صرف نقدي: مدين مصروفات عمومية / دائن الصندوق
        projectDb.AccountMappingRules.Add(new AccountMappingRule
        {
            TransactionType = "CashPaymentVoucher",
            DebitAccountId = generalExpense.Id,
            CreditAccountId = cash.Id
        });
        // دفعة مقدمة لمورد: مدين دفعات مقدمة للموردين / دائن الصندوق
        projectDb.AccountMappingRules.Add(new AccountMappingRule
        {
            TransactionType = "SupplierAdvancePayment",
            DebitAccountId = advanceToSuppliers.Id,
            CreditAccountId = cash.Id
        });
        // استلام بضاعة على الحساب: مدين المخزون / دائن الموردون
        projectDb.AccountMappingRules.Add(new AccountMappingRule
        {
            TransactionType = "GoodsReceiptOnAccount",
            DebitAccountId = inventory.Id,
            CreditAccountId = suppliers.Id
        });
        // تسوية الدفعة المقدمة عند اكتمال الاستلام: مدين الموردون / دائن دفعات مقدمة للموردين
        projectDb.AccountMappingRules.Add(new AccountMappingRule
        {
            TransactionType = "SupplierAdvanceOffset",
            DebitAccountId = suppliers.Id,
            CreditAccountId = advanceToSuppliers.Id
        });

        await projectDb.SaveChangesAsync();
        Console.WriteLine("تم إنشاء دليل حسابات أولي (7 حسابات) وقواعد الربط الخمس (سندات + استلام بضاعة + دفعة مقدمة).");
    }

    // ---------- الخطوة 1-د: مورد تجريبي لاختبار وحدة الموردين فورًا ----------
    if (!await projectDb.Suppliers.AnyAsync())
    {
        projectDb.Suppliers.Add(new Supplier
        {
            Name = "شركة الأهرام للتوريدات",
            Phone = "07701234567",
            DefaultPaymentTerms = SupplierPaymentTerms.Credit
        });
        await projectDb.SaveChangesAsync();
        Console.WriteLine("تم إنشاء مورد تجريبي: شركة الأهرام للتوريدات.");
    }

    var projectUser = await projectDb.Users.FirstOrDefaultAsync(u => u.Username == testUsername);
    if (projectUser is null)
    {
        projectUser = new User
        {
            Username = testUsername,
            PasswordHash = PasswordHasher.Hash(testPassword),
            RoleId = role.Id,
            PreferredLanguage = "ar"
        };
        projectDb.Users.Add(projectUser);
        await projectDb.SaveChangesAsync();
        Console.WriteLine($"تم إنشاء مستخدم محلي داخل المشروع، معرّفه: {projectUser.Id}");
    }

    // ---------- الخطوة 1-ج: فرع ومخزن وصنف ورصيد افتتاحي، لعرض بيانات حقيقية في شاشة المخازن ----------
    if (!await projectDb.Items.AnyAsync())
    {
        var branch = await projectDb.Branches.FirstOrDefaultAsync() ?? new Branch { Name = "الفرع الرئيسي - البصرة" };
        if (branch.Id == 0) projectDb.Branches.Add(branch);
        await projectDb.SaveChangesAsync();

        var warehouse = new Warehouse
        {
            BranchId = branch.Id,
            Name = "المخزن الرئيسي",
            WarehouseType = WarehouseType.Main,
            IsSellableStock = true
        };
        projectDb.Warehouses.Add(warehouse);

        var item = new Item
        {
            ItemCode = "A-1042",
            ItemName = "دهان بلاستيك أبيض 20ل",
            BaseUnitName = "قطعة",
            SourcingMethod = SourcingMethod.Purchased,
            SalePrice = 210
        };
        projectDb.Items.Add(item);
        await projectDb.SaveChangesAsync();

        var batch = new ItemBatch { ItemId = item.Id, BatchNumber = "B-2312", ExpiryDate = DateTime.Today.AddMonths(8) };
        projectDb.ItemBatches.Add(batch);
        await projectDb.SaveChangesAsync();

        projectDb.StockTransactions.Add(new StockTransaction
        {
            ItemId = item.Id,
            WarehouseId = warehouse.Id,
            BatchId = batch.Id,
            QuantityBaseUnits = 50,
            TransactionType = StockTransactionType.Receipt,
            CreatedByUserId = projectUser!.Id
        });
        await projectDb.SaveChangesAsync();

        Console.WriteLine("تم إنشاء فرع ومخزن رئيسي وصنف تجريبي برصيد افتتاحي 50 قطعة.");
    }

    // ---------- الخطوة 2: إنشاء المشروع والمستخدم العام وربطهما في قاعدة التحكم ----------
    await using var controlDb = new ControlDbContext(controlOptions);

    var project = await controlDb.Projects.FirstOrDefaultAsync(p => p.DatabaseName == "ERP_Project_WaterFactory");
    if (project is null)
    {
        project = new Project
        {
            ProjectName = "مصنع المياه - البصرة",
            DatabaseName = "ERP_Project_WaterFactory",
            ServerAddress = "localhost"
        };
        controlDb.Projects.Add(project);
        await controlDb.SaveChangesAsync();
        Console.WriteLine("تم إنشاء سجل المشروع في قاعدة التحكم.");
    }

    var globalUser = await controlDb.GlobalUsers.FirstOrDefaultAsync(u => u.Username == testUsername);
    if (globalUser is null)
    {
        globalUser = new GlobalUser
        {
            FullName = "أحمد (تجريبي)",
            Username = testUsername,
            PasswordHash = PasswordHasher.Hash(testPassword)
        };
        controlDb.GlobalUsers.Add(globalUser);
        await controlDb.SaveChangesAsync();
        Console.WriteLine("تم إنشاء المستخدم العام في قاعدة التحكم.");
    }

    var access = await controlDb.UserProjectAccesses
        .FirstOrDefaultAsync(a => a.GlobalUserId == globalUser.Id && a.ProjectId == project.Id);
    if (access is null)
    {
        controlDb.UserProjectAccesses.Add(new UserProjectAccess
        {
            GlobalUserId = globalUser.Id,
            ProjectId = project.Id,
            LocalUserIdInProject = projectUser.Id
        });
        await controlDb.SaveChangesAsync();
        Console.WriteLine("تم ربط المستخدم بالمشروع.");
    }
}

Console.WriteLine();
Console.WriteLine("=== جاهز للتجربة ===");
Console.WriteLine($"اسم المستخدم: {testUsername}");
Console.WriteLine($"كلمة المرور:  {testPassword}");
