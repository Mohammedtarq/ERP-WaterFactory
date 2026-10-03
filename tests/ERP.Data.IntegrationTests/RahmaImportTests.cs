using System.Text.RegularExpressions;
using ERP.Data.Import;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Security;
using ERP.Data.Services;
using ERP.Data.Setup;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>مشروع جديد بلا بيانات تجريبية (كيوم الانتقال الفعلي) + نسخة مصغّرة من قاعدة نظام الرحمة على نفس السيرفر.</summary>
public class RahmaFixture : IAsyncLifetime
{
    private static string Master => Environment.GetEnvironmentVariable("ERP_TEST_MASTER_CONNECTION")
        ?? throw new InvalidOperationException("ERP_TEST_MASTER_CONNECTION غير معيّن");

    public readonly string Suffix = Guid.NewGuid().ToString("N")[..8];
    public string LegacyDb => $"ALRAHMA_T{Suffix}";
    public string LegacyCs => new SqlConnectionStringBuilder(Master) { InitialCatalog = LegacyDb }.ConnectionString;
    public string ProjectCs = "";
    public InstallResult Install = null!;
    public int AdminLocalId;

    public ProjectDbContext NewDb() => new(new DbContextOptionsBuilder<ProjectDbContext>().UseSqlServer(ProjectCs).Options);

    public async Task InitializeAsync()
    {
        var control = new SqlConnectionStringBuilder(Master) { InitialCatalog = $"ERP_Ctl_R{Suffix}" }.ConnectionString;
        Install = await new ProvisioningService().InstallAsync(new InstallRequest(control, "معمل الرحمة — تجربة النقل", $"ERP_Rahma_{Suffix}",
                                                                                   "محمد", "mohammed", "Rahma@2026", DemoData: false));
        ProjectCs = Install.ProjectConnectionString;
        await using (var db = NewDb())
            if (Install.Success) AdminLocalId = await db.Users.Where(u => u.Username == "mohammed").Select(u => u.Id).SingleAsync();
        await CreateLegacyAsync(Master, LegacyDb);
    }

    /// <summary>ينشئ قاعدة بشكل نظام الرحمة من Rahma/rahma_sample.sql.</summary>
    public static async Task CreateLegacyAsync(string master, string name)
    {
        await using var conn = new SqlConnection(master);
        await conn.OpenAsync();
        await using (var c = new SqlCommand($"CREATE DATABASE [{name}] COLLATE Arabic_CI_AS", conn)) await c.ExecuteNonQueryAsync();
        conn.ChangeDatabase(name);
        var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Rahma", "rahma_sample.sql"));
        foreach (var batch in Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline).Where(b => b.Trim().Length > 0))
        {
            await using var cmd = new SqlCommand(batch, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var conn = new SqlConnection(Master);
        await conn.OpenAsync();
        foreach (var db in new[] { $"ERP_Rahma_{Suffix}", $"ERP_Ctl_R{Suffix}", LegacyDb })
        {
            await using var cmd = new SqlCommand($"IF DB_ID('{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END", conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}

[CollectionDefinition("rahma")] public class RahmaCollection : ICollectionFixture<RahmaFixture> { }

/// <summary>
/// النقل من نظام الرحمة: البيانات الأساسية والأرصدة الافتتاحية فقط، تجربة تُظهر المطابقة ولا تحفظ، ثم تنفيذ مرة واحدة.
/// </summary>
[Collection("rahma")]
public class RahmaImportTests
{
    private readonly RahmaFixture _f;
    public RahmaImportTests(RahmaFixture f) => _f = f;

    [Fact]
    public async Task Analyze_dry_run_then_import_opening_balances_once()
    {
        Assert.True(_f.Install.Success, _f.Install.ErrorMessage);

        // ---------- العثور على القاعدة والتحليل (قراءة فقط) ----------
        var candidates = await RahmaLegacyReader.FindCandidatesAsync(_f.ProjectCs);
        var found = Assert.Single(candidates, c => c.Name == _f.LegacyDb);
        Assert.Equal(DateTime.Today.AddDays(-1), found.LastActivity);
        Assert.DoesNotContain(candidates, c => c.Name.StartsWith("ERP_"));

        var plan = await RahmaLegacyReader.AnalyzeAsync(_f.LegacyCs);
        Assert.Equal("معمل مياه الرحمة", plan.CompanyName);
        Assert.Equal(new[] { "RH-330-S20", "RH-330-C40" }, plan.Products.Select(p => p.NewCode));
        var c40 = plan.Products.Single(p => p.LegacyId == 2);
        Assert.Equal("ماء الرحمة 330 مل — كارتون 40", c40.NewName);
        Assert.Equal(106.25m, c40.UnitPrice);                       // 4250 ÷ 40 (آخر سعر عادي، لا سعر الملصق الخاص)
        Assert.Equal(112.5m, plan.Products.Single(p => p.LegacyId == 1).UnitPrice);

        // المواد: العامة + كل لون سدادة + كل ملصق خاص (ومنها ملصق انتهى رصيده لكنه في الوصفات)، و"بدون ليبل" ليست مادة
        Assert.Equal(11, plan.RawMaterials.Count);
        var preform = plan.RawMaterials.Single(m => m.NewCode == "RH-PRE");
        Assert.Equal(1500, preform.LegacyRemaining);
        Assert.Equal(38.44m, preform.UnitCost);                     // سعر آخر دفعة
        Assert.Equal(500000, preform.AlertLevel);
        Assert.Equal(new[] { "RH-CAP", "RH-CAP-001", "RH-CAP-002" }, plan.RawMaterials.Where(m => m.Kind == RahmaRawKind.Cap).Select(m => m.NewCode));
        Assert.Equal(150, plan.RawMaterials.Single(m => m.Kind == RahmaRawKind.Cap && m.Descriptor == "اسود").Quantity);   // اللون المكتوب في خانة الاسم الخاص يُدمج
        Assert.Equal(new[] { "رمضان كريم", "زواج سعيد", "كافيه شغف", "مطعم الحسون" },
                     plan.RawMaterials.Where(m => m.IsSpecialLabel).Select(m => m.Descriptor));
        Assert.DoesNotContain(plan.RawMaterials, m => m.Descriptor.Contains("بدون"));

        // الوصفة للقطعة: 40 امبولة لكارتون 40 = 1، و80 ليبل = 2، وكارتون واحد = 0.025
        var bom = plan.Boms[2].ToDictionary(l => l.role, l => l.perUnit);
        Assert.Equal(1m, bom["امبولة"]);
        Assert.Equal(2m, bom["لاصق"]);
        Assert.Equal(0.025m, bom["كارتون"]);

        // الملصقات الخاصة: مربوطة بالعميل بالاسم، والمناسبات عامة
        Assert.Equal(4, plan.Recipes.Count);
        Assert.Equal(1, plan.Recipes.Single(r => r.SpecialName == "مطعم الحسون").CustomerLegacyId);
        Assert.Equal(2, plan.Recipes.Single(r => r.SpecialName == "كافيه شغف").CustomerLegacyId);
        Assert.Null(plan.Recipes.Single(r => r.SpecialName == "رمضان كريم").CustomerLegacyId);
        Assert.Null(plan.Recipes.Single(r => r.SpecialName == "زواج سعيد").CustomerLegacyId);
        Assert.All(plan.Recipes, r => Assert.Equal(2m, r.LabelsPerUnit));

        // المنتج التام: المسجل مقابل المحسوب من الحركات (الإنتاج − المحمّل − التالف − المسحوب)
        Assert.Equal(4, plan.FinishedStock.Count);                   // المنتهي رصيده لا يُنقل
        var general = plan.FinishedStock.Single(f => f.ProductLegacyId == 1 && f.SpecialName == "");
        Assert.Equal((30m, 30m), (general.RecordedPacks, general.ComputedPacks));
        var ramadan = plan.FinishedStock.Single(f => f.SpecialName == "رمضان كريم");
        Assert.Equal((10m, 15m, 10m), (ramadan.RecordedPacks, ramadan.ComputedPacks, ramadan.Packs));
        Assert.Contains(plan.Warnings, w => w.Contains("يختلف"));

        Assert.Equal(5, plan.Customers.Count);
        Assert.Equal(1_250_000, plan.CustomersDebt);
        Assert.Equal(50_000, plan.CustomersCredit);
        Assert.Equal(800_000, plan.DepositsTotal);                  // استُرجع تأمين كافيه شغف بالكامل
        Assert.Equal(3_718_022, plan.SuppliersDebt);
        Assert.Equal(100_000, plan.SuppliersAdvance);
        Assert.Equal(2, plan.Employees.Count);                       // الفعّالون فقط
        var ahmed = plan.Employees.Single(e => e.LegacyId == 10);
        Assert.Equal((300_000m, 50_000m), (ahmed.LoanBalance, ahmed.LoanInstallment));   // آخر قسط استُقطع
        Assert.True(plan.Employees.Single(e => e.LegacyId == 11).IsUsd);
        Assert.True(plan.Employees.Single(e => e.LegacyId == 11).IsSalesRep);   // ورد في فواتير تحميل السيارات
        Assert.False(plan.Employees.Single(e => e.LegacyId == 10).IsSalesRep);
        Assert.Contains(plan.Warnings, w => w.Contains("غير فعّالين"));
        Assert.Equal(18_458_350, plan.CashTotal);
        Assert.True(plan.CashBoxes[0].MergeIntoDefault);

        await using var db = _f.NewDb();
        var importer = new RahmaImporter(db);

        // ---------- غير الأدمن مرفوض ----------
        var role = await db.Roles.FirstAsync(r => r.Name == "محاسب");
        var clerk = new User { Username = "rahma_clerk", PasswordHash = PasswordHasher.Hash("x"), RoleId = role.Id };
        db.Users.Add(clerk);
        await db.SaveChangesAsync();
        Assert.Contains("للأدمن", (await importer.ExecuteAsync(plan, clerk.Id, commit: false)).Error);

        // ---------- تجربة: كل شيء يُنفَّذ ويُطابَق ثم يُتراجع عنه ----------
        var customersBefore = await db.Customers.CountAsync();
        var dry = await importer.ExecuteAsync(plan, _f.AdminLocalId, commit: false);
        Assert.True(dry.Success, dry.Error);
        Assert.False(dry.Committed);
        Assert.True(dry.AllMatch, string.Join("\n", dry.Reconciliation.Where(r => !r.Matches).Select(r => $"{r.Description}: {r.Legacy} ≠ {r.New}")));
        Assert.Contains(dry.Reconciliation, r => r.Description == "ديون العملاء (عليهم)" && r.New == 1_250_000);
        Assert.Contains(dry.Reconciliation, r => r.Description == "المنتج التام (بالقطعة)" && r.New == 30 * 20 + 50 * 40 + 10 * 20 + 10 * 20);
        Assert.Equal(customersBefore, await db.Customers.CountAsync());
        Assert.False(await db.Items.AnyAsync(i => i.ItemCode.StartsWith("RH-")));
        Assert.False(await importer.AlreadyImportedAsync());

        // ---------- بعد الجرد: تعديل الكميات والنقد ثم التنفيذ ----------
        plan.RawMaterials.Single(m => m.NewCode == "RH-PRE").Quantity = 1400;
        plan.CashBoxes.Single(b => b.Name == "صندوق محمد").CountedAmount = 15_000_000;
        plan.Employees.Single(e => e.LegacyId == 10).LoanInstallment = 400_000;
        Assert.Contains("قسط سلفة", (await importer.ExecuteAsync(plan, _f.AdminLocalId, commit: true)).Error);   // قسط > الرصيد
        plan.Employees.Single(e => e.LegacyId == 10).LoanInstallment = 75_000;

        var result = await importer.ExecuteAsync(plan, _f.AdminLocalId, commit: true);
        Assert.True(result.Success, result.Error);
        Assert.True(result.Committed);
        Assert.Contains("18,000,000", result.Summary);               // 3,000,000 + 15,000,000
        Assert.True(await importer.AlreadyImportedAsync());
        Assert.Contains("مسبقًا", (await importer.ExecuteAsync(plan, _f.AdminLocalId, commit: false)).Error);

        // الأصناف والوصفات
        var item = await db.Items.SingleAsync(i => i.ItemCode == "RH-330-C40");
        Assert.Equal(106.25m, item.SalePrice);
        Assert.Equal(SourcingMethod.Manufactured, item.SourcingMethod);
        Assert.Contains(await db.ItemPackagingLevels.Where(l => l.ItemId == item.Id).ToListAsync(), l => l.LevelName == "كارتون" && l.EquivalentBaseUnits == 40);
        var bomLines = await db.BOMLines.Include(l => l.RawMaterialItem).Where(l => l.BOM.FinishedItemId == item.Id).ToListAsync();
        Assert.Equal(4, bomLines.Count);
        Assert.Equal(0.025m, bomLines.Single(l => l.RawMaterialItem.ItemCode == "RH-CTN").QuantityPerUnit);
        Assert.Equal(38.44m, (await db.Items.SingleAsync(i => i.ItemCode == "RH-PRE")).CostPrice);

        var hassoun = await db.Customers.SingleAsync(c => c.Name == "مطعم الحسون");
        Assert.Equal("07701112222", hassoun.Phone);
        var recipes = await db.CustomRecipes.Include(r => r.Lines).ThenInclude(l => l.ComponentItem).ToListAsync();
        var hassounRecipe = recipes.Single(r => r.Name.StartsWith("مطعم الحسون"));
        Assert.Equal(hassoun.Id, hassounRecipe.CustomerId);
        var line = Assert.Single(hassounRecipe.Lines);
        Assert.Equal("ليبل — مطعم الحسون", line.ComponentItem.ItemName);
        Assert.Equal((await db.Items.SingleAsync(i => i.ItemCode == "RH-LBL")).Id, line.ReplacesRawMaterialItemId);
        Assert.Null(recipes.Single(r => r.Name.StartsWith("رمضان كريم")).CustomerId);
        // الوصفة المخصصة تعمل في الإنتاج: الليبل الخاص يحل محل العام
        var (merged, mergeError) = await new ProductionService(db).MergeRecipeAsync(item.Id, hassounRecipe.Id);
        Assert.Null(mergeError);
        Assert.Contains(merged, m => m.rawItemId == line.ComponentItemId && m.perUnit == 2);
        Assert.DoesNotContain(merged, m => m.rawItemId == line.ReplacesRawMaterialItemId);

        // المخزون الافتتاحي: تشغيلة لكل ملصق، والكمية بعد الجرد
        var fgWh = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.FinishedGoods);
        var rawWh = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.RawMaterial);
        var fgBatches = await db.StockTransactions.Where(t => t.ItemId == item.Id && t.WarehouseId == fgWh.Id)
            .Select(t => new { t.Batch!.BatchNumber, t.QuantityBaseUnits }).ToListAsync();
        Assert.Equal(2000, fgBatches.Single(b => b.BatchNumber == "RH-مطعم الحسون · اسود").QuantityBaseUnits);
        var preformId = (await db.Items.SingleAsync(i => i.ItemCode == "RH-PRE")).Id;
        Assert.Equal(1400, await db.StockTransactions.Where(t => t.ItemId == preformId && t.WarehouseId == rawWh.Id).SumAsync(t => t.QuantityBaseUnits));

        // كشف العميل: الرصيد الافتتاحي أول سطر، والدفعة تُوزَّع عليه، ولا يظهر في قائمة الفواتير ولا المبيعات
        var sales = new SalesService(db);
        var statement = await sales.GetCustomerStatementAsync(hassoun.Id);
        var opening = Assert.Single(statement);
        Assert.Equal(("OpeningBalance", 1_000_000m), (opening.TxType, opening.Debit));
        Assert.Equal("رصيد افتتاحي (نظام الرحمة)", opening.Description);
        Assert.Empty(await sales.GetInvoiceListAsync(customerId: hassoun.Id));
        Assert.True((await new FinanceService(db).CreateVoucherAsync(VoucherType.Receipt, VoucherPartyType.Customer, hassoun.Id, 400_000,
                     PaymentMethod.Cash, DateTime.Today, "CashReceiptVoucher", _f.AdminLocalId)).Success);
        var account = new CustomerAccountService(db);
        await account.SyncAsync(hassoun.Id);
        var invoice = Assert.Single(await account.GetInvoicesAsync(hassoun.Id));
        Assert.Equal((400_000m, 600_000m), (invoice.Paid, invoice.Remaining));
        var balances = await sales.GetCustomerBalancesAsync();
        Assert.Equal(600_000, balances.Single(b => b.CustomerId == hassoun.Id).Balance);
        Assert.Equal(-50_000, balances.Single(b => b.Name == "وكيل البصرة").Balance);
        Assert.Equal(800_000, await new CustomerDepositService(db).GetBalanceAsync(hassoun.Id));

        // الموردون في الدفتر
        var payables = await db.JournalEntryLines.Where(l => l.Account.AccountCode == "2101").SumAsync(l => l.Credit - l.Debit);
        Assert.Equal(3_718_022, payables);

        // السلفة: رصيد افتتاحي بلا حركة صندوق، يُستقطع بالقسط من رواتب شهر الانتقال
        var emp = await db.Employees.SingleAsync(e => e.FullName == "أحمد عامل الخط");
        Assert.NotNull(emp.ShiftId);
        Assert.Equal("عامل خط", emp.JobTitle);
        var loan = await db.EmployeeDeductions.SingleAsync(d => d.EmployeeId == emp.Id);
        Assert.True(loan.IsOpening);
        Assert.Equal((300_000m, 75_000m), (loan.Amount, loan.MonthlyInstallment!.Value));
        Assert.False(await db.CashBoxTransactions.AnyAsync(t => t.ReferenceTable == "EmployeeDeductions" && t.ReferenceId == loan.Id));
        var due = await new EmployeeDeductionService(db).PlanForPeriodAsync(DateTime.Today.Month, DateTime.Today.Year, null);
        Assert.Equal(75_000, due.Single(d => d.deduction.Id == loan.Id).amount);
        var rep = await db.Employees.SingleAsync(e => e.FullName == "سامي المندوب");
        Assert.Equal(SalaryCurrency.USD, rep.SalaryCurrency);
        Assert.True(rep.IsSalesRep);
        Assert.True(await db.Warehouses.AnyAsync(w => w.WarehouseType == WarehouseType.RepVan && w.OwnerEmployeeId == rep.Id));

        // الصناديق: الأول يصبح الرئيسي، والبقية صناديق جديدة، والجرد رصيد افتتاحي قابل للتعديل
        var cash = new CashBoxService(db);
        var main = await db.CashBoxes.SingleAsync(b => b.IsDefault);
        Assert.Equal(3_000_000 + 400_000, await cash.GetBalanceAsync(main.Id));      // الجرد + دفعة مطعم الحسون أعلاه
        var mohammedBox = await db.CashBoxes.SingleAsync(b => b.Name == "صندوق محمد");
        Assert.Equal(15_000_000, await cash.GetBalanceAsync(mohammedBox.Id));
        Assert.Equal(0, await cash.GetBalanceAsync((await db.CashBoxes.SingleAsync(b => b.Name == "صندوق سيف")).Id));
        var openingTx = await db.CashBoxTransactions.Include(t => t.JournalEntry!).ThenInclude(j => j.Lines).ThenInclude(l => l.Account)
            .SingleAsync(t => t.CashBoxId == mohammedBox.Id);
        Assert.Equal(CashBoxTxType.Opening, openingTx.TxType);
        Assert.Equal(15_000_000, openingTx.JournalEntry!.Lines.Single(l => l.Account.AccountCode == "3101").Credit);

        // خريطة المعرّفات القديمة ← الجديدة
        Assert.Equal(hassoun.Id, (await db.LegacyImportMap.SingleAsync(m => m.EntityType == "Customer" && m.LegacyKey == "1")).NewId);
        Assert.All(await db.JournalEntries.Include(j => j.Lines).ToListAsync(), j => Assert.True(j.IsBalanced));
    }
}
