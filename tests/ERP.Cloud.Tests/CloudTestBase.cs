using ERP.Cloud.Contracts;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Data.Setup;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Cloud.Tests;

/// <summary>إعداد الخادم يُقرأ من متغيرات البيئة: فئات الاختبار هنا تعمل بالتتابع لا بالتوازي.</summary>
[CollectionDefinition("cloud", DisableParallelization = true)]
public class CloudCollection { }

/// <summary>
/// أساس اختبارات السحابة: قاعدة معمل حقيقية (تثبيت كامل ببيانات تجريبية)، وقاعدة وسيطة، وخادم سحابي في الذاكرة.
/// </summary>
public abstract class CloudTestBase : IAsyncLifetime
{
    protected static string Master => Environment.GetEnvironmentVariable("ERP_TEST_MASTER_CONNECTION")
        ?? throw new InvalidOperationException("ERP_TEST_MASTER_CONNECTION غير معيّن");

    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];
    protected string ControlCs => new SqlConnectionStringBuilder(Master) { InitialCatalog = $"ERP_Ctl_Cloud{_suffix}" }.ConnectionString;
    protected string RelayCs => new SqlConnectionStringBuilder(Master) { InitialCatalog = $"ERP_Relay_{_suffix}" }.ConnectionString;
    private string _projectCs = "";
    protected int AdminId;

    protected ProjectDbContext NewDb() => new(new DbContextOptionsBuilder<ProjectDbContext>().UseSqlServer(_projectCs).Options) { AuditUserId = AdminId };

    public async Task InitializeAsync()
    {
        var install = await new ProvisioningService().InstallAsync(new InstallRequest(ControlCs, "سحابة", $"ERP_Cloud_{_suffix}", "المدير", "boss", "Boss@2026", DemoData: true));
        Assert.True(install.Success, install.ErrorMessage);
        _projectCs = install.ProjectConnectionString;
        await using var db = NewDb();
        AdminId = await db.Users.Where(u => u.Username == "boss").Select(u => u.Id).SingleAsync();
    }

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var conn = new SqlConnection(Master);
        await conn.OpenAsync();
        foreach (var name in new[] { $"ERP_Cloud_{_suffix}", $"ERP_Ctl_Cloud{_suffix}", $"ERP_Relay_{_suffix}" })
        {
            await using var cmd = new SqlCommand($"IF DB_ID('{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END", conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    protected WebApplicationFactory<Program> Cloud(string agentKey)
    {
        // تُقرأ قبل بناء الخادم: متغيرات بيئة كما في إعدادات Azure
        Environment.SetEnvironmentVariable("ConnectionStrings__Relay", RelayCs);
        Environment.SetEnvironmentVariable("Relay__AgentKey", agentKey);
        return new WebApplicationFactory<Program>();
    }

    protected record SeededRep(int RepId, int CustomerId, int WaterId, int ShrinkId, int VanId);

    /// <summary>مندوب بسيارة محمّلة (10 شرنك = 200 قطعة) وزبون مسند إليه بحد دين.</summary>
    protected async Task<SeededRep> SeedRepAsync(string tag, decimal creditLimit = 1_000_000, decimal piecePrice = 250)
    {
        await using var db = NewDb();
        var fg = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.FinishedGoods);
        var water = new Item { ItemCode = $"CL-{tag}", ItemName = $"ماء {tag} شرنك", SalePrice = piecePrice, CostPrice = 100 };
        var customer = new Customer { Name = $"زبون {tag}", CreditLimit = creditLimit };
        var rep = new Employee { FullName = $"مندوب {tag}", IsSalesRep = true, BaseSalary = 500_000 };
        db.AddRange(water, customer, rep);
        await db.SaveChangesAsync();
        var shrink = new ItemPackagingLevel { ItemId = water.Id, LevelName = "شرنك", EquivalentBaseUnits = 20 };
        db.ItemPackagingLevels.AddRange(new ItemPackagingLevel { ItemId = water.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 }, shrink);
        db.StockTransactions.Add(new StockTransaction { ItemId = water.Id, WarehouseId = fg.Id, QuantityBaseUnits = 1_000, UnitCost = 100,
                                                        TransactionType = StockTransactionType.Receipt, CreatedByUserId = AdminId });
        var van = new Warehouse { BranchId = fg.BranchId, Name = $"سيارة {tag}", WarehouseType = WarehouseType.RepVan, OwnerEmployeeId = rep.Id };
        db.Warehouses.Add(van);
        await db.SaveChangesAsync();
        db.RepCustomerAssignments.Add(new RepCustomerAssignment { EmployeeId = rep.Id, CustomerId = customer.Id });
        await db.SaveChangesAsync();
        var ops = new RepOperationsService(db);
        var (_, order) = await ops.CreateLoadOrderAsync(van.Id, fg.Id, DateTime.Today, new[] { new RepLoadLineInput(water.Id, shrink.Id, 10) }, null, AdminId);
        Assert.True((await ops.PrepareLoadOrderAsync(order!.Id, null, AdminId)).result.Success);
        return new(rep.Id, customer.Id, water.Id, shrink.Id, van.Id);
    }

    /// <summary>تفعيل المزامنة في المعمل: مفتاح ومعرّف الخادم، ويعيد مفتاح المعمل.</summary>
    protected async Task<string> EnableSyncAsync(string serverUrl)
    {
        await using var db = NewDb();
        var key = await new CloudSyncService(db).GenerateKeyAsync(AdminId);
        Assert.True((await new CloudSyncService(db).SaveSettingsAsync(serverUrl, true, 20, AdminId)).Success);
        return key;
    }
}
