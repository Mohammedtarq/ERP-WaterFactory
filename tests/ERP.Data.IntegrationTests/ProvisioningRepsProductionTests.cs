using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Data.Setup;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>
/// تثبيت كامل من الصفر على السيرفر (كما يفعل معالج الإعداد): قاعدة تحكم + مشروع + بيانات تجريبية.
/// اختبارات المندوبين والإنتاج تعمل على هذا النظام المثبّت نفسه — فتثبت أن التثبيت ينتج نظامًا صالحًا للعمل.
/// </summary>
public class ProvisionedFixture : IAsyncLifetime
{
    private static string Master => Environment.GetEnvironmentVariable("ERP_TEST_MASTER_CONNECTION")
        ?? throw new InvalidOperationException("ERP_TEST_MASTER_CONNECTION غير معيّن");

    public readonly string Suffix = Guid.NewGuid().ToString("N")[..8];
    public string ControlCs => new SqlConnectionStringBuilder(Master) { InitialCatalog = $"ERP_Ctl_{Suffix}" }.ConnectionString;
    public string ProjectDbName => $"ERP_Inst_{Suffix}";
    public string ProjectCs = "";
    public InstallResult Install = null!;
    public int AdminLocalId;

    public InstallRequest Request => new(ControlCs, "مصنع مياه التجربة", ProjectDbName, "المدير العام", "boss", "Boss@2026", DemoData: true);

    public ProjectDbContext NewDb() => new(new DbContextOptionsBuilder<ProjectDbContext>().UseSqlServer(ProjectCs).Options);

    public async Task InitializeAsync()
    {
        Install = await new ProvisioningService().InstallAsync(Request);
        ProjectCs = Install.ProjectConnectionString;
        await using var db = NewDb();
        if (Install.Success) AdminLocalId = await db.Users.Where(u => u.Username == "boss").Select(u => u.Id).SingleAsync();
    }

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var conn = new SqlConnection(Master);
        await conn.OpenAsync();
        foreach (var db in new[] { ProjectDbName, $"ERP_Ctl_{Suffix}", $"ERP_Legacy_{Suffix}" })
        {
            await using var cmd = new SqlCommand($"IF DB_ID('{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END", conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}

[CollectionDefinition("provisioned")] public class ProvisionedCollection : ICollectionFixture<ProvisionedFixture> { }

[Collection("provisioned")]
public class ProvisioningTests
{
    private readonly ProvisionedFixture _f;
    public ProvisioningTests(ProvisionedFixture f) => _f = f;

    [Fact]
    public async Task Fresh_install_produces_a_working_system_and_login()
    {
        Assert.True(_f.Install.Success, _f.Install.ErrorMessage + "\n" + string.Join("\n", _f.Install.Log));

        // الدخول الموحّد يعمل مباشرة بعد التثبيت
        var auth = new AuthService(_f.ControlCs);
        var login = await auth.LoginAsync("boss", "Boss@2026");
        Assert.True(login.Success, login.ErrorMessage);
        var project = Assert.Single(login.Projects);
        Assert.Equal("مصنع مياه التجربة", project.ProjectName);
        var (session, error) = await auth.OpenProjectAsync(project);
        Assert.Null(error);
        Assert.Equal("مدير عام", session!.RoleName);
        foreach (var m in new[] { ModuleCode.Sales, ModuleCode.HR, ModuleCode.Reps, ModuleCode.Production, ModuleCode.SystemSettings })
            Assert.True(session.Permissions.CanPost(m), m);

        await using var db = _f.NewDb();
        Assert.Equal(DefaultConfiguration.Rules.Length, await db.AccountMappingRules.CountAsync());
        Assert.Equal(DefaultConfiguration.Accounts.Length, await db.ChartOfAccounts.CountAsync());
        Assert.True(await db.Roles.CountAsync() >= 6);
        Assert.Equal(5, await db.Items.CountAsync());
        Assert.True(await db.BillOfMaterials.AnyAsync());
        Assert.Contains(await db.Warehouses.Select(w => w.WarehouseType).ToListAsync(), t => t == WarehouseType.RepVan);

        // الفاتورة تعمل على النظام المثبّت (الإجراءات المخزنة + قواعد الربط)
        var sales = new SalesService(db);
        var customer = await db.Customers.FirstAsync(c => c.CustomerType == CustomerType.SubCustomer);
        var fg = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.FinishedGoods);
        var item = await db.Items.FirstAsync(i => i.ItemCode == "W-500");
        var carton = await db.ItemPackagingLevels.FirstAsync(l => l.ItemId == item.Id && l.LevelName == "كارتون");
        var (r, id) = await sales.CreateInvoiceAsync(new(customer.Id, fg.Id, DateTime.Today, InvoicePaymentMethod.Credit, LoadingSuppliesEnabled: true), _f.AdminLocalId);
        Assert.True(r.Success, r.ErrorMessage);
        await sales.AddLineAsync(id!.Value, new(item.Id, carton.Id, 2), _f.AdminLocalId);
        var (pr, summary) = await sales.PostInvoiceAsync(id.Value, _f.AdminLocalId);
        Assert.True(pr.Success, pr.ErrorMessage);
        Assert.Equal(4800m + 240m, summary!.TotalAmount);          // 24 × 200 سعر الوكيل + 24 × 10 تحميل
    }

    [Fact]
    public async Task Reinstall_and_upgrade_are_idempotent()
    {
        await using var db = _f.NewDb();
        var before = (items: await db.Items.CountAsync(), rules: await db.AccountMappingRules.CountAsync(),
                      roles: await db.Roles.CountAsync(), customers: await db.Customers.CountAsync(), users: await db.Users.CountAsync());

        var again = await new ProvisioningService().InstallAsync(_f.Request);
        Assert.True(again.Success, again.ErrorMessage);
        Assert.Contains(again.Log, l => l.Contains("المخطط محدّث مسبقًا"));
        Assert.Empty(await DatabaseInstaller.UpgradeProjectAsync(_f.ProjectCs));

        Assert.Equal(before, (await db.Items.CountAsync(), await db.AccountMappingRules.CountAsync(),
                              await db.Roles.CountAsync(), await db.Customers.CountAsync(), await db.Users.CountAsync()));
    }

    /// <summary>قاعدة أُنشئت يدويًا من SSMS (01 → 08 فقط، بلا سجل نسخ) تُرقّى تلقائيًا دون إعادة المخطط الأساسي.</summary>
    [Fact]
    public async Task Legacy_manual_database_is_upgraded_in_place()
    {
        var legacy = $"ERP_Legacy_{_f.Suffix}";
        var cs = new SqlConnectionStringBuilder(_f.ControlCs) { InitialCatalog = legacy }.ConnectionString;
        await DatabaseInstaller.EnsureDatabaseAsync(cs, legacy);
        await using (var conn = new SqlConnection(cs))
        {
            await conn.OpenAsync();
            foreach (var s in DatabaseInstaller.ProjectScripts.Where(n => string.CompareOrdinal(n, "09") < 0))
                await DatabaseInstaller.ExecuteScriptAsync(conn, DatabaseInstaller.ReadScript(s));
        }

        var applied = await DatabaseInstaller.UpgradeProjectAsync(cs);
        Assert.DoesNotContain(applied, a => string.CompareOrdinal(a, "09") < 0);    // الأساسي لم يُعد تنفيذه
        Assert.Contains("09_sales_logic.sql", applied);
        Assert.Contains("11_reps_production.sql", applied);
        Assert.Empty(await DatabaseInstaller.UpgradeProjectAsync(cs));

        await using var db = new ProjectDbContext(new DbContextOptionsBuilder<ProjectDbContext>().UseSqlServer(cs).Options);
        await DefaultConfiguration.SeedAsync(db, includeWarehouses: false);
        Assert.Equal(DefaultConfiguration.Rules.Length, await db.AccountMappingRules.CountAsync());
        Assert.Empty(await new SalesService(db).GetCustomerBalancesAsync());       // Views المبيعات موجودة
    }

    [Fact]
    public async Task Invalid_database_name_is_rejected()
    {
        var r = await new ProvisioningService().InstallAsync(_f.Request with { ProjectDatabaseName = "مصنع; DROP" });
        Assert.False(r.Success);
        Assert.Contains("أحرف إنجليزية", r.ErrorMessage);
    }
}

[Collection("provisioned")]
public class RepsServiceTests
{
    private readonly ProvisionedFixture _f;
    public RepsServiceTests(ProvisionedFixture f) => _f = f;

    [Fact]
    public async Task Van_load_sale_wallet_collection_return_and_conflict()
    {
        await using var db = _f.NewDb();
        var reps = new RepsService(db);
        var user = _f.AdminLocalId;
        var van = await db.Warehouses.Include(w => w.OwnerEmployee).FirstAsync(w => w.WarehouseType == WarehouseType.RepVan);
        var repId = van.OwnerEmployeeId!.Value;
        var fg = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.FinishedGoods);
        var item = await db.Items.FirstAsync(i => i.ItemCode == "W-1500");
        var fgBefore = await LedgerBalance(db, item.Id, fg.Id);

        // تحميل 120 قطعة للسيارة
        Assert.True((await reps.LoadVanAsync(van.Id, fg.Id, new[] { new StockLineInput(item.Id, null, 120) }, user)).Success);
        Assert.Equal(120m, await LedgerBalance(db, item.Id, van.Id));
        Assert.Equal(fgBefore - 120, await LedgerBalance(db, item.Id, fg.Id));
        Assert.False((await reps.LoadVanAsync(van.Id, fg.Id, new[] { new StockLineInput(item.Id, null, 1_000_000) }, user)).Success);

        // بيع نقدي من السيارة ← النقد يدخل المحفظة تلقائيًا (من ترحيل الفاتورة)
        var sales = new SalesService(db);
        var direct = await db.Customers.FirstAsync(c => c.CustomerType == CustomerType.Direct);
        var piece = await db.ItemPackagingLevels.FirstAsync(l => l.ItemId == item.Id && l.EquivalentBaseUnits == 1);
        var (_, invId) = await sales.CreateInvoiceAsync(new(direct.Id, van.Id, DateTime.Today, InvoicePaymentMethod.Cash), user);
        await sales.AddLineAsync(invId!.Value, new(item.Id, piece.Id, 30), user);
        Assert.True((await sales.PostInvoiceAsync(invId.Value, user)).result.Success);
        Assert.Equal(15_000m, await reps.GetWalletBalanceAsync(repId));             // 30 × 500

        // مصروف ميداني، ثم تسليم للخزينة يفوق الرصيد يُرفض، ثم تسليم صحيح
        Assert.True((await reps.RecordFieldExpenseAsync(repId, 2_000, "وقود", DateTime.Today, user)).Success);
        Assert.Contains("رصيد المحفظة", (await reps.RecordCashHandoverAsync(repId, 50_000, DateTime.Today, user)).ErrorMessage);
        Assert.True((await reps.RecordCashHandoverAsync(repId, 10_000, DateTime.Today, user)).Success);
        Assert.Equal(3_000m, await reps.GetWalletBalanceAsync(repId));

        // تحصيل دين عميل: يدخل المحفظة ويظهر سند قبض في كشف العميل
        var sub = await db.Customers.FirstAsync(c => c.CustomerType == CustomerType.SubCustomer);
        Assert.True((await reps.RecordDebtCollectionAsync(repId, sub.Id, 4_000, DateTime.Today, user)).Success);
        Assert.Equal(7_000m, await reps.GetWalletBalanceAsync(repId));
        Assert.Contains(await sales.GetCustomerStatementAsync(sub.Id), s => s.TxType == "ReceiptVoucher" && s.Credit == 4_000);

        var wallet = await reps.GetWalletStatementAsync(repId);
        Assert.Equal(7_000m, wallet.Last().RunningBalance);
        Assert.All(wallet, w => Assert.NotNull(w.EntryNumber));                      // كل حركة لها قيد
        var entryIds = await db.RepWalletTransactions.Where(w => w.EmployeeId == repId).Select(w => w.JournalEntryId!.Value).ToListAsync();
        foreach (var je in await db.JournalEntries.Include(j => j.Lines).Where(j => entryIds.Contains(j.Id)).ToListAsync())
            Assert.True(je.IsBalanced);

        // إرجاع 20 وتالف 10 ← يبقى في السيارة 120 − 30 − 20 − 10 = 60
        Assert.True((await reps.ReturnFromVanAsync(van.Id, fg.Id, new[] { new StockLineInput(item.Id, null, 20) }, user)).Success);
        Assert.True((await reps.RecordVanDamageAsync(van.Id, new StockLineInput(item.Id, null, 10), "كسر أثناء التوزيع", user)).Success);
        Assert.Equal(60m, await LedgerBalance(db, item.Id, van.Id));

        // تعارض مزامنة: رصيد سالب ← التسوية تعيده صفرًا
        var tx = new StockTransaction { ItemId = item.Id, WarehouseId = van.Id, QuantityBaseUnits = -70, TransactionType = StockTransactionType.RepSale, CreatedByUserId = user };
        db.StockTransactions.Add(tx);
        await db.SaveChangesAsync();
        var conflict = new SyncConflict { StockTransactionId = tx.Id, EmployeeId = repId, ItemId = item.Id, RequestedQuantity = 70, ResultingBalance = -10 };
        db.SyncConflicts.Add(conflict);
        await db.SaveChangesAsync();
        Assert.False((await reps.ResolveConflictAsync(conflict.Id, " ", true, user)).Success);
        Assert.True((await reps.ResolveConflictAsync(conflict.Id, "بيع أوفلاين مؤكد من الفواتير الورقية", true, user)).Success);
        Assert.Equal(0m, await LedgerBalance(db, item.Id, van.Id));
        Assert.False((await reps.ResolveConflictAsync(conflict.Id, "مرة ثانية", true, user)).Success);
    }

    private static async Task<decimal> LedgerBalance(ProjectDbContext db, int itemId, int whId) =>
        await db.StockTransactions.Where(t => t.ItemId == itemId && t.WarehouseId == whId).SumAsync(t => (decimal?)t.QuantityBaseUnits) ?? 0;
}

[Collection("provisioned")]
public class ProductionServiceTests
{
    private readonly ProvisionedFixture _f;
    public ProductionServiceTests(ProvisionedFixture f) => _f = f;

    private static async Task<decimal> Balance(ProjectDbContext db, int itemId, int whId) =>
        await db.StockTransactions.Where(t => t.ItemId == itemId && t.WarehouseId == whId).SumAsync(t => (decimal?)t.QuantityBaseUnits) ?? 0;

    [Fact]
    public async Task Production_order_consume_qc_pack_complete_and_rejection()
    {
        await using var db = _f.NewDb();
        var prod = new ProductionService(db);
        var user = _f.AdminLocalId;
        var w500 = await db.Items.FirstAsync(i => i.ItemCode == "W-500");
        var cap = await db.Items.FirstAsync(i => i.ItemCode == "RM-CAP");
        var raw = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.RawMaterial);
        var fg = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.FinishedGoods);
        var carton = await db.ItemPackagingLevels.FirstAsync(l => l.ItemId == w500.Id && l.LevelName == "كارتون");
        var piece = await db.ItemPackagingLevels.FirstAsync(l => l.ItemId == w500.Id && l.EquivalentBaseUnits == 1);
        var machineId = (int?)(await db.Machines.FirstAsync(m => m.Name == "نافخة 1")).Id;
        var capBefore = await Balance(db, cap.Id, raw.Id);
        var fgBefore = await Balance(db, w500.Id, fg.Id);

        var (cr, orderId) = await prod.CreateOrderAsync(w500.Id, 1000, null, raw.Id, machineId, user);
        Assert.True(cr.Success, cr.ErrorMessage);
        Assert.Equal(3, await db.ProductionOrderConsumptions.CountAsync(c => c.ProductionOrderId == orderId));

        // لا فحص ولا تعبئة قبل بدء التشغيل
        Assert.False((await prod.PackAsync(orderId!.Value, carton.Id, 1, fg.Id, user)).Success);
        Assert.True((await prod.StartAsync(orderId.Value, user)).Success);
        Assert.Equal(capBefore - 1000, await Balance(db, cap.Id, raw.Id));
        Assert.False((await prod.StartAsync(orderId.Value, user)).Success);
        Assert.Contains("قبل فحص المختبر", (await prod.PackAsync(orderId.Value, carton.Id, 1, fg.Id, user)).ErrorMessage);

        var tests = await prod.GetApplicableTestsAsync(w500.Id);
        QcInput For(string name, string value) => new(tests.Single(t => t.TestName.StartsWith(name)).Id, value);
        var (qr, overall) = await prod.RecordQcAsync(orderId.Value, new[] { For("درجة", "7.2"), For("الأملاح", "120"), For("إحكام", "سليم") }, user);
        Assert.True(qr.Success, qr.ErrorMessage);
        Assert.Equal(QCOverallResult.Passed, overall);

        // 80 كارتون = 960 قطعة، ثم تجاوز الكمية يُرفض، ثم 40 قطعة تكمل الأمر
        Assert.True((await prod.PackAsync(orderId.Value, carton.Id, 80, fg.Id, user)).Success);
        Assert.Contains("المتبقي 40", (await prod.PackAsync(orderId.Value, carton.Id, 4, fg.Id, user)).ErrorMessage);
        Assert.True((await prod.PackAsync(orderId.Value, piece.Id, 40, fg.Id, user)).Success);
        Assert.Equal(fgBefore + 1000, await Balance(db, w500.Id, fg.Id));
        var order = await db.ProductionOrders.AsNoTracking().Include(o => o.OutputBatch).SingleAsync(o => o.Id == orderId);
        Assert.Equal(ProductionOrderStatus.Completed, order.Status);
        Assert.Equal(order.MONumber, order.OutputBatch!.BatchNumber);
        Assert.Equal(order.Id, order.OutputBatch.ProductionOrderId);

        // أمر ثانٍ: pH خارج الحدود ← الدفعة مرفوضة ← لا تعبئة ← الإلغاء مسموح
        var (_, second) = await prod.CreateOrderAsync(w500.Id, 100, null, raw.Id, machineId, user);
        await prod.StartAsync(second!.Value, user);
        var (_, bad) = await prod.RecordQcAsync(second.Value, new[] { For("درجة", "9.1"), For("الأملاح", "120"), For("إحكام", "سليم") }, user);
        Assert.Equal(QCOverallResult.Rejected, bad);
        Assert.Contains("مرفوضة", (await prod.PackAsync(second.Value, piece.Id, 1, fg.Id, user)).ErrorMessage);
        Assert.True((await prod.CancelAsync(second.Value)).Success);
        Assert.Contains((await prod.GetOrdersAsync()), o => o.Id == second && o.StageText == "ملغى");

        // نقص مواد أولية ← البدء يُرفض دون أي صرف جزئي
        var (_, huge) = await prod.CreateOrderAsync(w500.Id, 1_000_000, null, raw.Id, machineId, user);
        var capNow = await Balance(db, cap.Id, raw.Id);
        Assert.Contains("الرصيد غير كافٍ", (await prod.StartAsync(huge!.Value, user)).ErrorMessage);
        await using var fresh = _f.NewDb();
        Assert.Equal(capNow, await Balance(fresh, cap.Id, raw.Id));
    }

    /// <summary>
    /// الخلل المبلّغ عنه (MO-00002): الأمر مرتبط بمخزن مواد أولية فارغ بينما الرصيد في مخزن مواد أولية آخر —
    /// شاشة الاحتياجات تقول "متوفر" وبدء التشغيل يقول "المتاح 0". الآن المحرك واحد للثلاثة.
    /// </summary>
    [Fact]
    public async Task Start_uses_the_same_availability_as_requirements_across_raw_warehouses()
    {
        await using var db = _f.NewDb();
        var user = _f.AdminLocalId;
        var branchId = (await db.Warehouses.FirstAsync()).BranchId;
        var fg = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.FinishedGoods);
        var machineId = (int?)(await db.Machines.FirstAsync(m => m.Name == "تعبئة 1")).Id;
        var preform = new Item { ItemCode = "RM-PRE-T", ItemName = "امبولة 14 غم (اختبار)", SourcingMethod = SourcingMethod.Purchased };
        var bottle = new Item { ItemCode = "W-330-T", ItemName = "ماء 330 مل (اختبار)", SourcingMethod = SourcingMethod.Manufactured };
        var empty = new Warehouse { BranchId = branchId, Name = "أ- مخزن الأغطية (فارغ)", WarehouseType = WarehouseType.RawMaterial };
        var stocked = new Warehouse { BranchId = branchId, Name = "مخزن المواد الأولية 2", WarehouseType = WarehouseType.RawMaterial };
        db.AddRange(preform, bottle, empty, stocked);
        await db.SaveChangesAsync();
        var bom = new BillOfMaterials { FinishedItemId = bottle.Id };
        bom.Lines.Add(new BOMLine { RawMaterialItemId = preform.Id, QuantityPerUnit = 1 });
        db.BillOfMaterials.Add(bom);
        var late = new ItemBatch { ItemId = preform.Id, BatchNumber = "PRE-LATE", ExpiryDate = DateTime.Today.AddYears(2) };
        var soon = new ItemBatch { ItemId = preform.Id, BatchNumber = "PRE-SOON", ExpiryDate = DateTime.Today.AddMonths(3) };
        db.ItemBatches.AddRange(late, soon);
        await db.SaveChangesAsync();
        StockTransaction In(int wh, int? batch, decimal q) => new() { ItemId = preform.Id, WarehouseId = wh, BatchId = batch, QuantityBaseUnits = q,
                                                                     TransactionType = StockTransactionType.Receipt, CreatedByUserId = user };
        db.StockTransactions.AddRange(In(stocked.Id, late.Id, 3000), In(stocked.Id, soon.Id, 1500), In(fg.Id, null, 900));
        await db.SaveChangesAsync();

        // شاشة الاحتياجات ومعاينة الأمر: المتاح من مخازن المواد الأولية فقط (900 في مخزن المنتج التام لا تُحسب)
        var req = (await new ManufacturingRequirementService(db).CalculateAsync(bottle.Id, 2000, null, empty.Id)).Single();
        Assert.Equal(4500m, req.QuantityAvailable);
        Assert.Equal(900m, req.ElsewhereQuantity);
        Assert.True(req.IsSufficient);
        Assert.Contains("مخزن المواد الأولية 2", req.WhereText);

        // الأمر مرتبط بالمخزن الفارغ ← البدء ينجح ويصرف من المخزن الآخر بترتيب الصلاحية (الأقرب انتهاءً أولًا)
        var prod = new ProductionService(db);
        var (cr, orderId) = await prod.CreateOrderAsync(bottle.Id, 2000, null, empty.Id, machineId, user);
        Assert.True(cr.Success, cr.ErrorMessage);
        var start = await prod.StartAsync(orderId!.Value, user);
        Assert.True(start.Success, start.ErrorMessage);
        await using var check = _f.NewDb();
        var consumed = await check.StockTransactions.Where(t => t.ReferenceTable == "ProductionOrders" && t.ReferenceId == orderId && t.ItemId == preform.Id
                                                                   && t.QuantityBaseUnits < 0).ToListAsync();
        Assert.Equal(-2000m, consumed.Sum(t => t.QuantityBaseUnits));
        Assert.All(consumed, t => Assert.Equal(stocked.Id, t.WarehouseId));
        Assert.Equal(-1500m, consumed.Single(t => t.BatchId == soon.Id).QuantityBaseUnits);
        Assert.Equal(-500m, consumed.Single(t => t.BatchId == late.Id).QuantityBaseUnits);
        Assert.Equal(900m, await Balance(check, preform.Id, fg.Id));

        // النقص: رسالة توضح المتاح في مخازن المواد الأولية والرصيد الموجود في مخازن أخرى، ولا صرف جزئي
        var (_, big) = await prod.CreateOrderAsync(bottle.Id, 3000, null, empty.Id, machineId, user);
        var error = (await prod.StartAsync(big!.Value, user)).ErrorMessage;
        Assert.Contains("المتاح في مخازن المواد الأولية 2500", error);
        Assert.Contains("يوجد 900 في مخازن أخرى", error);
        Assert.Equal(2500m, await Balance(check, preform.Id, stocked.Id));

        empty.IsActive = false;
        stocked.IsActive = false;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// تحت التصنيع لكل ماكينة: الصرف لأمر إنتاج ← الاستهلاك = قائمة المواد × المُنتَج فعلًا ← التالف من تحت التصنيع ←
    /// المتبقي مرحّل على الماكينة ← المطابقة: المصروف = المستهلك + التالف + المتبقي. ولا صرف حر من مخزن المواد الأولية.
    /// </summary>
    [Fact]
    public async Task Machine_wip_issue_consume_by_produced_damage_carry_over_and_reconcile()
    {
        await using var db = _f.NewDb();
        var user = _f.AdminLocalId;
        var prod = new ProductionService(db);
        var machines = new MachineService(db);
        var w500 = await db.Items.FirstAsync(i => i.ItemCode == "W-500");
        var cap = await db.Items.FirstAsync(i => i.ItemCode == "RM-CAP");
        var preformId = (await db.Items.FirstAsync(i => i.ItemCode == "RM-PRE")).Id;
        var raw = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.RawMaterial && w.Name == "مخزن المواد الأولية");
        var fg = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.FinishedGoods);
        var piece = await db.ItemPackagingLevels.FirstAsync(l => l.ItemId == w500.Id && l.EquivalentBaseUnits == 1);

        // ماكينة جديدة ← مخزن تحت تصنيع خاص بها، لا يظهر كنوع عادي ولا يقبل مستندات المخزن
        var (mr, machineId) = await machines.SaveAsync(null, "نافخة اختبار", "نفخ", "الخط الثاني", null, true);
        Assert.True(mr.Success, mr.ErrorMessage);
        Assert.False((await machines.SaveAsync(null, "نافخة اختبار", "نفخ", null, null, true)).result.Success);
        var machine = await db.Machines.AsNoTracking().Include(m => m.WipWarehouse).SingleAsync(m => m.Id == machineId);
        Assert.Equal(WarehouseType.WorkInProcess, machine.WipWarehouse.WarehouseType);
        Assert.Equal("تحت التصنيع — نافخة اختبار", machine.WipWarehouse.Name);
        var wip = machine.WipWarehouseId;

        var docs = new WarehouseDocumentService(db);
        var capPiece = await db.ItemPackagingLevels.FirstAsync(l => l.ItemId == cap.Id && l.EquivalentBaseUnits == 1);
        var capLine = new[] { new StockDocumentLineInput(cap.Id, capPiece.Id, 10) };
        var freeIssue = await docs.CreateAsync(new StockDocumentRequest(StockDocumentType.Issue, raw.Id, DateTime.Today, capLine, user, PartyName: "أي جهة"));
        Assert.Contains("لا يُسمح بالصرف الحر", freeIssue.result.ErrorMessage);
        var toWip = await docs.CreateAsync(new StockDocumentRequest(StockDocumentType.Receipt, wip, DateTime.Today, capLine, user));
        Assert.False(toWip.result.Success);

        // أمر بلا ماكينة يُرفض
        Assert.Contains("اختر الماكينة", (await prod.CreateOrderAsync(w500.Id, 100, null, raw.Id, null, user)).result.ErrorMessage);

        // صرف 500 لكل مادة ← تنتقل من مخزن المواد إلى تحت التصنيع
        var capRawBefore = await Balance(db, cap.Id, raw.Id);
        var (_, orderId) = await prod.CreateOrderAsync(w500.Id, 500, null, raw.Id, machineId, user);
        Assert.True((await prod.StartAsync(orderId!.Value, user)).Success);
        Assert.Equal(capRawBefore - 500, await Balance(db, cap.Id, raw.Id));
        Assert.Equal(500m, await Balance(db, cap.Id, wip));

        var tests = await prod.GetApplicableTestsAsync(w500.Id);
        QcInput For(string name, string value) => new(tests.Single(t => t.TestName.StartsWith(name)).Id, value);
        Assert.True((await prod.RecordQcAsync(orderId.Value, new[] { For("درجة", "7.2"), For("الأملاح", "120"), For("إحكام", "سليم") }, user)).result.Success);

        // إنتاج 300 فعليًا ← يُستهلك 300 من كل مادة فقط، ويبقى 200 على الماكينة
        Assert.True((await prod.PackAsync(orderId.Value, piece.Id, 300, fg.Id, user)).Success);
        Assert.Equal(200m, await Balance(db, cap.Id, wip));

        // تالف إنتاج 30 غطاء من تحت التصنيع، ولا يتجاوز المتبقي
        Assert.True((await machines.RecordDamageAsync(machineId!.Value, cap.Id, 30, orderId, user)).Success);
        Assert.Contains("تحت التصنيع", (await machines.RecordDamageAsync(machineId.Value, cap.Id, 1000, null, user)).ErrorMessage);
        Assert.Equal(170m, await Balance(db, cap.Id, wip));

        // إنتاج 200 أخرى يحتاج 200 غطاء والمتبقي 170 ← يُرفض بلا أي خصم، ثم صرف إضافي 30 يكمل
        var shortage = await prod.PackAsync(orderId.Value, piece.Id, 200, fg.Id, user);
        Assert.Contains("اصرف كمية إضافية", shortage.ErrorMessage);
        await using (var fresh = _f.NewDb()) Assert.Equal(170m, await Balance(fresh, cap.Id, wip));
        Assert.True((await prod.IssueAdditionalAsync(orderId.Value, cap.Id, 30, user)).Success);
        Assert.True((await prod.PackAsync(orderId.Value, piece.Id, 150, fg.Id, user)).Success);
        Assert.Equal(50m, await Balance(db, cap.Id, wip));

        // مطابقة الأمر: المصروف 530 = المستهلك 450 + التالف 30 + المتبقي 50
        var materials = await machines.GetOrderMaterialsAsync(orderId.Value);
        var capRow = materials.Single(m => m.RawItemId == cap.Id);
        Assert.Equal((530m, 450m, 30m, 50m), (capRow.Issued, capRow.Consumed, capRow.Damaged, capRow.Remaining));
        var preformRow = materials.Single(m => m.RawItemId == preformId);
        Assert.Equal((500m, 450m, 50m), (preformRow.Issued, preformRow.Consumed, preformRow.Remaining));

        // عرض الماكينة: المصروف والمستهلك والتالف والمتبقي، والمطابقة سليمة
        var summary = await machines.GetWipSummaryAsync(DateTime.Today.AddDays(-1), DateTime.Today, machineId);
        var capWip = summary.Single(r => r.RawItemId == cap.Id);
        Assert.Equal((0m, 530m, 450m, 30m, 50m), (capWip.CarriedOver, capWip.Issued, capWip.Consumed, capWip.Damaged, capWip.Remaining));
        Assert.True(capWip.IsReconciled);
        Assert.Equal("نافخة اختبار", capWip.MachineName);
        Assert.Equal("نفخ", capWip.MachineType);
        Assert.Contains(w500.ItemName, capWip.ProductsText);
        Assert.All(summary, r => Assert.True(r.IsReconciled));

        // الفترة التالية: المتبقي يظهر كمرحّل
        var next = (await machines.GetWipSummaryAsync(DateTime.Today.AddDays(1), DateTime.Today.AddDays(2), machineId)).Single(r => r.RawItemId == cap.Id);
        Assert.Equal((50m, 0m, 50m), (next.CarriedOver, next.Issued, next.Remaining));

        // إرجاع المتبقي للمخزن ← يصفر رصيد الماكينة
        Assert.True((await machines.ReturnToWarehouseAsync(machineId.Value, cap.Id, 50, raw.Id, user)).Success);
        Assert.Equal(0m, await Balance(db, cap.Id, wip));
        var afterReturn = (await machines.GetWipSummaryAsync(DateTime.Today, DateTime.Today, machineId)).Single(r => r.RawItemId == cap.Id);
        Assert.Equal((50m, 0m), (afterReturn.Returned, afterReturn.Remaining));
        Assert.True(afterReturn.IsReconciled);

        // حركة غير مصنّفة على تحت التصنيع ← تنبيه عدم مطابقة
        db.StockTransactions.Add(new StockTransaction { ItemId = cap.Id, WarehouseId = wip, QuantityBaseUnits = 7, TransactionType = StockTransactionType.Receipt, CreatedByUserId = user });
        await db.SaveChangesAsync();
        var unexplained = (await machines.GetWipSummaryAsync(DateTime.Today, DateTime.Today, machineId)).Single(r => r.RawItemId == cap.Id);
        Assert.False(unexplained.IsReconciled);
        Assert.Equal(7m, unexplained.Difference);
    }

    [Fact]
    public async Task Custom_recipe_replaces_the_named_component()
    {
        await using var db = _f.NewDb();
        var w500 = await db.Items.FirstAsync(i => i.ItemCode == "W-500");
        var label = await db.Items.FirstAsync(i => i.ItemCode == "RM-LBL");
        var customer = await db.Customers.FirstAsync();
        var special = new Item { ItemCode = "RM-LBL-HS", ItemName = "لاصق مطعم الحسون", SourcingMethod = SourcingMethod.Purchased };
        db.Items.Add(special);
        await db.SaveChangesAsync();
        var recipe = new CustomRecipe { FinishedItemId = w500.Id, CustomerId = customer.Id, Name = "وصفة مطعم الحسون" };
        recipe.Lines.Add(new CustomRecipeLine { ComponentItemId = special.Id, ComponentLabel = "لاصق أمامي", QuantityPerUnit = 1, ReplacesRawMaterialItemId = label.Id });
        db.CustomRecipes.Add(recipe);
        await db.SaveChangesAsync();

        var (lines, error) = await new ProductionService(db).MergeRecipeAsync(w500.Id, recipe.Id);
        Assert.Null(error);
        Assert.Equal(3, lines.Count);
        Assert.DoesNotContain(lines, l => l.rawItemId == label.Id);
        Assert.Contains(lines, l => l.rawItemId == special.Id && l.perUnit == 1);
    }

    [Theory]
    [InlineData("7.0", null, QCLineResult.Pass)]
    [InlineData("8.6", null, QCLineResult.Fail)]
    [InlineData("abc", null, QCLineResult.Fail)]
    [InlineData("8.6", true, QCLineResult.Pass)]
    public void Numeric_qc_evaluation(string measured, bool? manual, QCLineResult expected)
    {
        var t = new QualityTest { StandardMin = 6.5m, StandardMax = 8.5m };
        Assert.Equal(expected, ProductionService.Evaluate(t, measured, manual));
        Assert.Equal(QCLineResult.Pass, ProductionService.Evaluate(new QualityTest { StandardText = "سليم" }, " سليم ", null));
    }
}
