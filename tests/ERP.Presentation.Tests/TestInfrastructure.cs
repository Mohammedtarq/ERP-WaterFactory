using ERP.Data.ControlDb;
using ERP.Data.ControlDb.Entities;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Security;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>يسجّل الرسائل بدل عرضها؛ التأكيد يُقبل افتراضيًا.</summary>
public class RecordingDialogs : IDialogService
{
    public List<string> Errors { get; } = new();
    public List<string> Infos { get; } = new();
    public bool ConfirmAnswer { get; set; } = true;

    public void Info(string message) => Infos.Add(message);
    public void Error(string message) => Errors.Add(message);
    public bool Confirm(string message) => ConfirmAnswer;
    public List<string> Urls { get; } = new();
    public void OpenUrl(string url) => Urls.Add(url);
    public List<ReportDocument> Reports { get; } = new();
    public void ShowReport(ReportDocument report) => Reports.Add(report);
    public string? ImageToPick { get; set; }
    public string? PickImageFile() => ImageToPick;
    public string? FileToPick { get; set; }
    public string? PickFile(string title, string filter) => FileToPick;
}

public class RecordingNavigator : INavigator
{
    public ProjectSelectionViewModel? ProjectSelection { get; private set; }
    public MainShellViewModel? Shell { get; private set; }
    public int LoginShown { get; private set; }

    public void ShowProjectSelection(ProjectSelectionViewModel vm) => ProjectSelection = vm;
    public void ShowMainShell(MainShellViewModel vm) => Shell = vm;
    public void ShowLogin() => LoginShown++;
    public string? SetupReason { get; private set; }
    public string? UsedControlConnection { get; private set; }
    public void ShowSetup(string? reason) => SetupReason = reason;
    public void UseControlConnection(string controlConnectionString) => UsedControlConnection = controlConnectionString;
}

public class MemoryConfigStore : IConfigStore
{
    public string? Value { get; set; }
    public string ConfigPath => "memory";
    public string? LoadControlConnectionString() => Value;
    public void SaveControlConnectionString(string connectionString) => Value = connectionString;
}

/// <summary>
/// قاعدة تحكم + قاعدة مشروع نظيفتان (يجهّزهما tests/run_tests.sh بالملفات 00 → 10)،
/// تُعبّآن هنا ببيانات مصنع مياه تجريبية.
/// </summary>
public class AppFixture
{
    public const string AdminUser = "admin", AdminPassword = "Admin@123";
    public const string ClerkUser = "clerk", ClerkPassword = "Clerk@123";

    public string ControlConnection { get; } = Env("ERP_TEST_CONTROL_CONNECTION");
    public string ProjectConnection { get; } = Env("ERP_TEST_PROJECT_CONNECTION");

    public int SubCustomerId, AgentId, DirectId, WaterItemId, CartonLevelId, MainWarehouseId, EarlyBatchId, SupplierId;

    private static string Env(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"{name} غير معيّن (يضبطه tests/run_tests.sh)");

    public ProjectDbContext NewDb() => new(new DbContextOptionsBuilder<ProjectDbContext>().UseSqlServer(ProjectConnection).Options);

    public AppFixture()
    {
        // أي خطأ غير متوقع في الخلفية يُفشل الاختبار بدل أن يضيع
        AsyncRelayCommand.UnhandledErrorHandler = ex => Unhandled.Add(ex);
        // الاختبارات تكتب أحيانًا مباشرة في القاعدة (خارج الجلسة): شاشات الأرصدة تُحدَّث عند كل فتح بلا مهلة
        SectionViewModel.IdleRefreshInterval = TimeSpan.Zero;

        using var db = NewDb();
        var admin = new Role { Name = "مدير عام" };
        var clerk = new Role { Name = "موظف مبيعات" };
        db.Roles.AddRange(admin, clerk);
        db.SaveChanges();
        foreach (var m in new[] { ModuleCode.Dashboard, ModuleCode.Warehouse, ModuleCode.Sales, ModuleCode.Suppliers, ModuleCode.Finance,
                                  ModuleCode.HR, ModuleCode.Reps, ModuleCode.Production, ModuleCode.SystemSettings })
            db.RolePermissions.Add(new RolePermission { RoleId = admin.Id, ModuleCode = m, CanView = true, CanAdd = true, CanEdit = true, CanDelete = true, CanPost = true });
        db.RolePermissions.Add(new RolePermission { RoleId = clerk.Id, ModuleCode = ModuleCode.Sales, CanView = true, CanAdd = true, CanEdit = true });

        var adminUser = new User { Username = AdminUser, PasswordHash = PasswordHasher.Hash(AdminPassword), RoleId = admin.Id };
        var clerkUser = new User { Username = ClerkUser, PasswordHash = PasswordHasher.Hash(ClerkPassword), RoleId = clerk.Id };
        db.Users.AddRange(adminUser, clerkUser);

        var acc = new Dictionary<string, ChartOfAccount>
        {
            ["1101"] = new() { AccountCode = "1101", AccountName = "الصندوق", AccountType = AccountType.Asset },
            ["1103"] = new() { AccountCode = "1103", AccountName = "عهدة المندوبين", AccountType = AccountType.Asset },
            ["1201"] = new() { AccountCode = "1201", AccountName = "العملاء", AccountType = AccountType.Asset },
            ["1301"] = new() { AccountCode = "1301", AccountName = "المخزون", AccountType = AccountType.Asset },
            ["2101"] = new() { AccountCode = "2101", AccountName = "الموردون", AccountType = AccountType.Liability },
            ["2102"] = new() { AccountCode = "2102", AccountName = "ضريبة مستحقة", AccountType = AccountType.Liability },
            ["4101"] = new() { AccountCode = "4101", AccountName = "إيرادات المبيعات", AccountType = AccountType.Revenue },
            ["4102"] = new() { AccountCode = "4102", AccountName = "إيراد مستلزمات التحميل", AccountType = AccountType.Revenue },
            ["5101"] = new() { AccountCode = "5101", AccountName = "مصروفات عمومية", AccountType = AccountType.Expense },
        };
        db.ChartOfAccounts.AddRange(acc.Values);
        db.SaveChanges();
        void Rule(string t, string dr, string cr) => db.AccountMappingRules.Add(new AccountMappingRule { TransactionType = t, DebitAccountId = acc[dr].Id, CreditAccountId = acc[cr].Id });
        Rule("CashReceiptVoucher", "1101", "1201");
        Rule("CashPaymentVoucher", "5101", "1101");
        Rule("GoodsReceiptOnAccount", "1301", "2101");
        Rule("SalesInvoiceCash", "1101", "4101");
        Rule("SalesInvoiceCredit", "1201", "4101");
        Rule("SalesInvoiceRepCash", "1103", "4101");
        Rule("SalesTax", "1201", "2102");
        Rule("LoadingSuppliesCharge", "1201", "4102");
        db.LoadingSuppliesSettings.Add(new LoadingSuppliesSetting { RatePerPiece = 10, EffectiveDate = new DateTime(2020, 1, 1) });

        var branch = new Branch { Name = "الفرع الرئيسي - البصرة" };
        db.Branches.Add(branch);
        db.SaveChanges();
        var main = new Warehouse { BranchId = branch.Id, Name = "مخزن المنتج التام", WarehouseType = WarehouseType.FinishedGoods };
        db.Warehouses.Add(main);
        var water = new Item { ItemCode = "W500", ItemName = "ماء 500 مل", SalePrice = 250, MinStockAlertLevel = 50 };
        db.Items.Add(water);
        db.SaveChanges();
        var piece = new ItemPackagingLevel { ItemId = water.Id, LevelName = "قطعة", ContainsQuantity = 1, EquivalentBaseUnits = 1 };
        db.ItemPackagingLevels.Add(piece);
        db.SaveChanges();
        var carton = new ItemPackagingLevel { ItemId = water.Id, LevelName = "كارتون", ParentLevelId = piece.Id, ContainsQuantity = 12, EquivalentBaseUnits = 12 };
        var early = new ItemBatch { ItemId = water.Id, BatchNumber = "EARLY", ExpiryDate = DateTime.Today.AddMonths(3) };
        var late = new ItemBatch { ItemId = water.Id, BatchNumber = "LATE", ExpiryDate = DateTime.Today.AddMonths(9) };
        db.ItemPackagingLevels.Add(carton);
        db.ItemBatches.AddRange(early, late);
        var agent = new Customer { Name = "وكيل الزبير", CustomerType = CustomerType.Agent };
        var direct = new Customer { Name = "زبون مباشر" };
        db.Customers.AddRange(agent, direct);
        var supplier = new Supplier { Name = "شركة الأهرام للتوريدات", DefaultPaymentTerms = SupplierPaymentTerms.Credit };
        db.Suppliers.Add(supplier);
        db.SaveChanges();
        var sub = new Customer { Name = "محل أبو حيدر", CustomerType = CustomerType.SubCustomer, ParentAgentId = agent.Id };
        db.Customers.Add(sub);
        db.AgentItemPrices.Add(new AgentItemPrice { CustomerId = agent.Id, ItemId = water.Id, AgentPrice = 200 });
        db.StockTransactions.AddRange(
            new StockTransaction { ItemId = water.Id, WarehouseId = main.Id, BatchId = early.Id, QuantityBaseUnits = 100, TransactionType = StockTransactionType.Receipt, CreatedByUserId = adminUser.Id },
            new StockTransaction { ItemId = water.Id, WarehouseId = main.Id, BatchId = late.Id, QuantityBaseUnits = 1000, TransactionType = StockTransactionType.Receipt, CreatedByUserId = adminUser.Id });
        db.SaveChanges();

        (SubCustomerId, AgentId, DirectId, WaterItemId, CartonLevelId, MainWarehouseId, EarlyBatchId, SupplierId) =
            (sub.Id, agent.Id, direct.Id, water.Id, carton.Id, main.Id, early.Id, supplier.Id);

        // ---- قاعدة التحكم: مشروع واحد ومستخدمان عامان مرتبطان به ----
        using var cdb = new ControlDbContext(new DbContextOptionsBuilder<ControlDbContext>().UseSqlServer(ControlConnection).Options);
        var project = new Project
        {
            ProjectName = "مصنع المياه - البصرة",
            DatabaseName = new SqlConnectionStringBuilder(ProjectConnection).InitialCatalog,
            ServerAddress = "localhost"
        };
        cdb.Projects.Add(project);
        var gAdmin = new GlobalUser { FullName = "أحمد المدير", Username = AdminUser, PasswordHash = PasswordHasher.Hash(AdminPassword) };
        var gClerk = new GlobalUser { FullName = "سارة المبيعات", Username = ClerkUser, PasswordHash = PasswordHasher.Hash(ClerkPassword) };
        cdb.GlobalUsers.AddRange(gAdmin, gClerk);
        cdb.SaveChanges();
        cdb.UserProjectAccesses.AddRange(
            new UserProjectAccess { GlobalUserId = gAdmin.Id, ProjectId = project.Id, LocalUserIdInProject = adminUser.Id },
            new UserProjectAccess { GlobalUserId = gClerk.Id, ProjectId = project.Id, LocalUserIdInProject = clerkUser.Id });
        cdb.SaveChanges();
    }

    public List<Exception> Unhandled { get; } = new();

    /// <summary>التدفق الكامل كما يفعله المستخدم: شاشة الدخول ← اختيار المشروع ← الواجهة الرئيسية.</summary>
    public async Task<(MainShellViewModel shell, RecordingDialogs dialogs)> LoginAsync(string user, string password)
    {
        var dialogs = new RecordingDialogs();
        var nav = new RecordingNavigator();
        var login = new LoginViewModel(new AuthService(ControlConnection), dialogs, nav) { Username = user };
        await login.LoginCommand.ExecuteAsync(password);
        Assert.Null(login.ErrorMessage);
        Assert.NotNull(nav.ProjectSelection);
        await nav.ProjectSelection!.OpenCommand.ExecuteAsync(null);
        Assert.Null(nav.ProjectSelection.ErrorMessage);
        Assert.NotNull(nav.Shell);
        return (nav.Shell!, dialogs);
    }
}

[CollectionDefinition("app")] public class AppCollection : ICollectionFixture<AppFixture> { }
