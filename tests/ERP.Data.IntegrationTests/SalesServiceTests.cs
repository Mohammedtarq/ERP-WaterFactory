using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Security;
using ERP.Data.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>
/// قاعدة بيانات نظيفة (01 → 10) تُجهَّز مرة واحدة عبر tests/run_tests.sh،
/// وتُعبّأ هنا بالكيانات عن طريق EF — فيثبت الاختبار أيضًا تطابق الكيانات مع الجداول.
/// </summary>
public class SalesFixture
{
    public string ConnectionString { get; } =
        Environment.GetEnvironmentVariable("ERP_TEST_CONNECTION")
        ?? throw new InvalidOperationException("عيّن ERP_TEST_CONNECTION (يضبطه tests/run_tests.sh تلقائيًا)");

    public int AdminUserId, ClerkUserId, AgentId, SubCustomerId, DirectId, MainWarehouseId, VanWarehouseId;
    public int WaterItemId, PieceLevelId, CartonLevelId;

    public ProjectDbContext NewDb() => new(new DbContextOptionsBuilder<ProjectDbContext>().UseSqlServer(ConnectionString).Options);

    public SalesFixture()
    {
        using var db = NewDb();

        var admin = new Role { Name = "مدير عام" };
        var clerk = new Role { Name = "موظف مبيعات" };
        db.Roles.AddRange(admin, clerk);
        db.SaveChanges();
        db.RolePermissions.AddRange(
            new RolePermission { RoleId = admin.Id, ModuleCode = ModuleCode.Sales, CanView = true, CanAdd = true, CanEdit = true, CanDelete = true, CanPost = true },
            new RolePermission { RoleId = clerk.Id, ModuleCode = ModuleCode.Sales, CanView = true, CanAdd = true, CanEdit = true });

        var rep = new Employee { FullName = "علي المندوب", IsSalesRep = true };
        db.Employees.Add(rep);
        db.SaveChanges();

        var adminUser = new User { Username = "admin", PasswordHash = PasswordHasher.Hash("x"), RoleId = admin.Id };
        var clerkUser = new User { Username = "clerk", PasswordHash = PasswordHasher.Hash("x"), RoleId = clerk.Id };
        db.Users.AddRange(adminUser, clerkUser);

        var accounts = new[]
        {
            new ChartOfAccount { AccountCode = "1101", AccountName = "الصندوق", AccountType = AccountType.Asset },
            new ChartOfAccount { AccountCode = "1103", AccountName = "عهدة المندوبين", AccountType = AccountType.Asset },
            new ChartOfAccount { AccountCode = "1201", AccountName = "العملاء", AccountType = AccountType.Asset },
            new ChartOfAccount { AccountCode = "2102", AccountName = "ضريبة مستحقة", AccountType = AccountType.Liability },
            new ChartOfAccount { AccountCode = "4101", AccountName = "إيرادات المبيعات", AccountType = AccountType.Revenue },
            new ChartOfAccount { AccountCode = "4102", AccountName = "إيراد مستلزمات التحميل", AccountType = AccountType.Revenue },
        };
        db.ChartOfAccounts.AddRange(accounts);
        db.SaveChanges();
        int A(string code) => accounts.Single(a => a.AccountCode == code).Id;
        db.AccountMappingRules.AddRange(
            new AccountMappingRule { TransactionType = "SalesInvoiceCash", DebitAccountId = A("1101"), CreditAccountId = A("4101") },
            new AccountMappingRule { TransactionType = "SalesInvoiceCredit", DebitAccountId = A("1201"), CreditAccountId = A("4101") },
            new AccountMappingRule { TransactionType = "SalesInvoiceRepCash", DebitAccountId = A("1103"), CreditAccountId = A("4101") },
            new AccountMappingRule { TransactionType = "SalesTax", DebitAccountId = A("1201"), CreditAccountId = A("2102") },
            new AccountMappingRule { TransactionType = "LoadingSuppliesCharge", DebitAccountId = A("1201"), CreditAccountId = A("4102") });
        db.LoadingSuppliesSettings.Add(new LoadingSuppliesSetting { RatePerPiece = 10, EffectiveDate = new DateTime(2026, 1, 1) });

        var branch = new Branch { Name = "الفرع الرئيسي - البصرة" };
        db.Branches.Add(branch);
        db.SaveChanges();

        var main = new Warehouse { BranchId = branch.Id, Name = "مخزن المنتج التام", WarehouseType = WarehouseType.FinishedGoods };
        var van = new Warehouse { BranchId = branch.Id, Name = "كاش فان علي", WarehouseType = WarehouseType.RepVan, OwnerEmployeeId = rep.Id };
        var water = new Item { ItemCode = "W500", ItemName = "ماء 500 مل", SalePrice = 250, BarCode = "6260000000017", MinStockAlertLevel = 100 };
        db.Warehouses.AddRange(main, van);
        db.Items.Add(water);
        db.SaveChanges();

        var piece = new ItemPackagingLevel { ItemId = water.Id, LevelName = "قطعة", ContainsQuantity = 1, EquivalentBaseUnits = 1 };
        db.ItemPackagingLevels.Add(piece);
        db.SaveChanges();
        var carton = new ItemPackagingLevel { ItemId = water.Id, LevelName = "كارتون", ParentLevelId = piece.Id, ContainsQuantity = 12, EquivalentBaseUnits = 12 };
        var early = new ItemBatch { ItemId = water.Id, BatchNumber = "EARLY", ExpiryDate = new DateTime(2027, 1, 1) };
        var late = new ItemBatch { ItemId = water.Id, BatchNumber = "LATE", ExpiryDate = new DateTime(2027, 6, 1) };
        db.ItemPackagingLevels.Add(carton);
        db.ItemBatches.AddRange(early, late);

        var agent = new Customer { Name = "وكيل الزبير", CustomerType = CustomerType.Agent };
        var direct = new Customer { Name = "زبون مباشر" };
        db.Customers.AddRange(agent, direct);
        db.SaveChanges();
        var sub = new Customer { Name = "محل أبو حيدر", CustomerType = CustomerType.SubCustomer, ParentAgentId = agent.Id };
        db.Customers.Add(sub);
        db.AgentItemPrices.Add(new AgentItemPrice { CustomerId = agent.Id, ItemId = water.Id, AgentPrice = 200 });

        db.StockTransactions.AddRange(
            new StockTransaction { ItemId = water.Id, WarehouseId = main.Id, BatchId = late.Id, QuantityBaseUnits = 1000, TransactionType = StockTransactionType.Receipt, CreatedByUserId = adminUser.Id },
            new StockTransaction { ItemId = water.Id, WarehouseId = main.Id, BatchId = early.Id, QuantityBaseUnits = 100, TransactionType = StockTransactionType.Receipt, CreatedByUserId = adminUser.Id },
            new StockTransaction { ItemId = water.Id, WarehouseId = van.Id, BatchId = early.Id, QuantityBaseUnits = 240, TransactionType = StockTransactionType.RepLoad, CreatedByUserId = adminUser.Id });
        db.SaveChanges();

        (AdminUserId, ClerkUserId, AgentId, SubCustomerId, DirectId) = (adminUser.Id, clerkUser.Id, agent.Id, sub.Id, direct.Id);
        (MainWarehouseId, VanWarehouseId, WaterItemId, PieceLevelId, CartonLevelId) = (main.Id, van.Id, water.Id, piece.Id, carton.Id);
    }
}

[CollectionDefinition("sales")] public class SalesCollection : ICollectionFixture<SalesFixture> { }

[Collection("sales")]
public class SalesServiceTests
{
    private readonly SalesFixture _f;
    public SalesServiceTests(SalesFixture f) => _f = f;

    private static readonly DateTime Today = new(2026, 9, 30);

    private async Task<int> NewInvoiceAsync(SalesService svc, SalesInvoiceHeaderInput h, int? user = null)
    {
        var (r, id) = await svc.CreateInvoiceAsync(h, user ?? _f.AdminUserId);
        Assert.True(r.Success, r.ErrorMessage);
        return id!.Value;
    }

    [Fact]
    public async Task Hierarchical_pricing_per_packaging_level()
    {
        using var db = _f.NewDb();
        var svc = new SalesService(db);
        Assert.Equal(2400m, await svc.GetSuggestedUnitPriceAsync(_f.AgentId, _f.WaterItemId, _f.CartonLevelId, true));
        Assert.Equal(2400m, await svc.GetSuggestedUnitPriceAsync(_f.SubCustomerId, _f.WaterItemId, _f.CartonLevelId, true));
        Assert.Equal(3000m, await svc.GetSuggestedUnitPriceAsync(_f.DirectId, _f.WaterItemId, _f.CartonLevelId, true));
        Assert.Equal(250m, await svc.GetSuggestedUnitPriceAsync(_f.AgentId, _f.WaterItemId, _f.PieceLevelId, false));
    }

    [Fact]
    public async Task Credit_invoice_posts_journal_and_customer_balance()
    {
        using var db = _f.NewDb();
        var svc = new SalesService(db);
        var id = await NewInvoiceAsync(svc, new(_f.SubCustomerId, _f.MainWarehouseId, Today, InvoicePaymentMethod.Credit,
            TaxEnabled: true, LoadingSuppliesEnabled: true));
        Assert.True((await svc.AddLineAsync(id, new(_f.WaterItemId, _f.CartonLevelId, 2), _f.AdminUserId)).Success);

        var (r, s) = await svc.PostInvoiceAsync(id, _f.AdminUserId);
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(4800m, s!.SubTotal);            // 2 كارتون × 12 × 200 (سعر الوكيل الأب)
        Assert.Equal(672m, s.TaxAmount);              // 14%
        Assert.Equal(240m, s.LoadingSuppliesAmount);  // 24 قطعة × 10
        Assert.Equal(5712m, s.AmountDue);

        var je = await db.JournalEntries.Include(j => j.Lines).SingleAsync(j => j.Id == s.JournalEntryId);
        Assert.True(je.IsPosted);
        Assert.True(je.IsBalanced);
        Assert.Equal(JournalEntryType.AutoSales, je.EntryType);

        var inv = await svc.GetInvoiceAsync(id);
        Assert.Equal(DocumentStatus.Posted, inv!.Status);
        Assert.Equal(5712m, inv.TotalAmount);

        var bal = (await svc.GetCustomerBalancesAsync()).Single(b => b.CustomerId == _f.SubCustomerId);
        Assert.Equal(5712m, bal.Balance);
        var stmt = await svc.GetCustomerStatementAsync(_f.SubCustomerId);
        Assert.Equal(5712m, stmt.Last().RunningBalance);
        Assert.Contains(await svc.GetInvoiceListAsync(customerId: _f.SubCustomerId), x => x.Id == id);
    }

    /// <summary>
    /// كشف حساب العميل والتوزيع على الأقدم أولًا — المثال المرجعي: فاتورتان 25 و25 ودفعة 30 ←
    /// الأولى مسددة بالكامل، والثانية 5 (المتبقي 20). مع التوزيع اليدوي، والمدفوع عند البيع، واستقلال دين العميل الفرعي.
    /// </summary>
    [Fact]
    public async Task Payments_are_allocated_oldest_invoice_first_with_manual_override()
    {
        using var db = _f.NewDb();
        var svc = new SalesService(db);
        var account = new CustomerAccountService(db);
        var finance = new FinanceService(db);
        var cashId = await db.ChartOfAccounts.Where(a => a.AccountCode == "1101").Select(a => a.Id).SingleAsync();
        var customersId = await db.ChartOfAccounts.Where(a => a.AccountCode == "1201").Select(a => a.Id).SingleAsync();
        if (!await db.AccountMappingRules.AnyAsync(r => r.TransactionType == "CustomerReceiptTest"))
        {
            db.AccountMappingRules.Add(new AccountMappingRule { TransactionType = "CustomerReceiptTest", DebitAccountId = cashId, CreditAccountId = customersId });
            await db.SaveChangesAsync();
        }
        var customer = new Customer { Name = $"عميل التوزيع {Guid.NewGuid():N}"[..24] };
        var agent = new Customer { Name = $"وكيل التوزيع {Guid.NewGuid():N}"[..24], CustomerType = CustomerType.Agent };
        db.Customers.AddRange(customer, agent);
        await db.SaveChangesAsync();
        var sub = new Customer { Name = $"فرعي التوزيع {Guid.NewGuid():N}"[..24], CustomerType = CustomerType.SubCustomer, ParentAgentId = agent.Id };
        db.Customers.Add(sub);
        await db.SaveChangesAsync();

        async Task<int> Invoice(int customerId, decimal price, InvoicePaymentMethod method = InvoicePaymentMethod.Credit, decimal paidNow = 0)
        {
            var id = await NewInvoiceAsync(svc, new(customerId, _f.MainWarehouseId, Today, method, AmountPaidNow: paidNow, IsAgentPricing: false));
            Assert.True((await svc.AddLineAsync(id, new(_f.WaterItemId, _f.PieceLevelId, 1, UnitPrice: price), _f.AdminUserId)).Success);
            var (r, _) = await svc.PostInvoiceAsync(id, _f.AdminUserId);
            Assert.True(r.Success, r.ErrorMessage);
            return id;
        }
        Task<FinanceOperationResult> Pay(int customerId, decimal amount) =>
            finance.CreateVoucherAsync(VoucherType.Receipt, VoucherPartyType.Customer, customerId, amount, PaymentMethod.Bank, Today, "CustomerReceiptTest", _f.AdminUserId);

        // المثال المرجعي
        var inv1 = await Invoice(customer.Id, 25);
        var inv2 = await Invoice(customer.Id, 25);
        Assert.True((await Pay(customer.Id, 30)).Success);
        var invoices = await account.GetInvoicesAsync(customer.Id);
        Assert.Equal((25m, 25m, 0m, InvoiceSettlementStatus.Paid), Row(invoices, inv1));
        Assert.Equal((25m, 5m, 20m, InvoiceSettlementStatus.PartiallyPaid), Row(invoices, inv2));
        // الفاتورة تحفظ المدفوع والمتبقي (المتبقي عمود محسوب)
        await using (var fresh = _f.NewDb())
        {
            var stored = await fresh.SalesInvoices.AsNoTracking().SingleAsync(i => i.Id == inv2);
            Assert.Equal((5m, 20m), (stored.AmountSettled, stored.AmountRemaining));
        }
        var payment = Assert.Single(await account.GetPaymentsAsync(customer.Id));
        Assert.Equal((30m, 30m, 0m), (payment.Amount, payment.Allocated, payment.Unallocated));
        Assert.Matches(@": 25 · .*: 5$", payment.AllocationText);
        // الكشف: إجمالي الدين 20 والرصيد التراكمي
        Assert.Equal(20m, (await svc.GetCustomerStatementAsync(customer.Id)).Last().RunningBalance);

        // فاتورة ثالثة ثم دفعة 30 ← تُكمل الثانية (20) ثم 10 للثالثة
        var inv3 = await Invoice(customer.Id, 25);
        Assert.True((await Pay(customer.Id, 30)).Success);
        invoices = await account.GetInvoicesAsync(customer.Id);
        Assert.Equal(InvoiceSettlementStatus.Paid, Row(invoices, inv2).Item4);
        Assert.Equal((25m, 10m, 15m, InvoiceSettlementStatus.PartiallyPaid), Row(invoices, inv3));

        // المدفوع عند البيع يُحتسب أولًا على فاتورته
        var inv4 = await Invoice(customer.Id, 25, InvoicePaymentMethod.Partial, paidNow: 10);
        Assert.Equal((25m, 10m, 15m, InvoiceSettlementStatus.PartiallyPaid), Row(await account.GetInvoicesAsync(customer.Id), inv4));

        // التوزيع اليدوي يتقدم: دفعة 20 تُخصص يدويًا 15 للفاتورة الرابعة، والباقي 5 تلقائيًا للأقدم (الثالثة)
        Assert.True((await Pay(customer.Id, 20)).Success);
        var last = (await account.GetPaymentsAsync(customer.Id)).Last();
        Assert.Contains("المتاح", (await account.AllocateManualAsync(last.VoucherId, inv4, 50, _f.AdminUserId)).ErrorMessage);
        Assert.True((await account.AllocateManualAsync(last.VoucherId, inv4, 15, _f.AdminUserId)).Success);
        invoices = await account.GetInvoicesAsync(customer.Id);
        Assert.Equal(InvoiceSettlementStatus.Paid, Row(invoices, inv4).Item4);
        Assert.Equal((25m, 15m, 10m, InvoiceSettlementStatus.PartiallyPaid), Row(invoices, inv3));
        last = (await account.GetPaymentsAsync(customer.Id)).Last();
        Assert.True(last.HasManual);
        Assert.Contains("(يدوي)", last.AllocationText);
        // إعادة التوزيع التلقائي: الأقدم أولًا ← 15 تُكمل الثالثة و5 للرابعة (10 عند البيع + 5 = 15، المتبقي 10)
        Assert.True((await account.ResetToAutomaticAsync(last.VoucherId)).Success);
        invoices = await account.GetInvoicesAsync(customer.Id);
        Assert.Equal(InvoiceSettlementStatus.Paid, Row(invoices, inv3).Item4);
        Assert.Equal((25m, 15m, 10m, InvoiceSettlementStatus.PartiallyPaid), Row(invoices, inv4));

        // دين العميل الفرعي مستقل: دفعة الوكيل لا تسدد فاتورة الفرعي، ولا يُسمح بتوزيعها عليها يدويًا
        var subInvoice = await Invoice(sub.Id, 25);
        Assert.True((await Pay(agent.Id, 25)).Success);
        Assert.Equal(InvoiceSettlementStatus.Unpaid, Row(await account.GetInvoicesAsync(sub.Id), subInvoice).Item4);
        var agentPayment = Assert.Single(await account.GetPaymentsAsync(agent.Id));
        Assert.Equal(25m, agentPayment.Unallocated);
        Assert.Contains("مستقل", (await account.AllocateManualAsync(agentPayment.VoucherId, subInvoice, 25, _f.AdminUserId)).ErrorMessage);
    }

    private static (decimal total, decimal paid, decimal remaining, InvoiceSettlementStatus status) Row(List<CustomerInvoiceRow> rows, int id)
    {
        var r = rows.Single(x => x.InvoiceId == id);
        return (r.Total, r.Paid, r.Remaining, r.Status);
    }

    [Fact]
    public async Task Business_errors_come_back_as_arabic_messages_without_side_effects()
    {
        using var db = _f.NewDb();
        var svc = new SalesService(db);
        int stockBefore = await db.StockTransactions.CountAsync();

        var id = await NewInvoiceAsync(svc, new(_f.DirectId, _f.MainWarehouseId, Today, InvoicePaymentMethod.Cash));
        await svc.AddLineAsync(id, new(_f.WaterItemId, _f.CartonLevelId, 500), _f.AdminUserId);   // 6000 قطعة
        var (r, s) = await svc.PostInvoiceAsync(id, _f.AdminUserId);

        Assert.False(r.Success);
        Assert.Null(s);
        Assert.Contains("الرصيد غير كافٍ", r.ErrorMessage);
        Assert.Equal(stockBefore, await db.StockTransactions.CountAsync());
        Assert.True((await svc.DeleteDraftInvoiceAsync(id, _f.AdminUserId)).Success);

        var (fr, _) = await svc.CreateInvoiceAsync(new(_f.DirectId, _f.MainWarehouseId, Today, InvoicePaymentMethod.Cash, IsFreeSale: true), _f.AdminUserId);
        Assert.Contains("الجهة المستفيدة", fr.ErrorMessage);
    }

    [Fact]
    public async Task Clerk_can_draft_but_not_post()
    {
        using var db = _f.NewDb();
        var svc = new SalesService(db);
        var id = await NewInvoiceAsync(svc, new(_f.DirectId, _f.MainWarehouseId, Today, InvoicePaymentMethod.Cash), _f.ClerkUserId);
        Assert.True((await svc.AddLineAsync(id, new(_f.WaterItemId, _f.PieceLevelId, 1), _f.ClerkUserId)).Success);
        var (r, _) = await svc.PostInvoiceAsync(id, _f.ClerkUserId);
        Assert.Contains("صلاحية ترحيل", r.ErrorMessage);
        Assert.True((await svc.PostInvoiceAsync(id, _f.AdminUserId)).result.Success);
    }

    [Fact]
    public async Task Van_cash_sale_and_free_sale_hit_the_right_ledgers()
    {
        using var db = _f.NewDb();
        var svc = new SalesService(db);

        var van = await NewInvoiceAsync(svc, new(_f.DirectId, _f.VanWarehouseId, Today, InvoicePaymentMethod.Cash));
        await svc.AddLineAsync(van, new(_f.WaterItemId, _f.PieceLevelId, 10), _f.AdminUserId);
        Assert.True((await svc.PostInvoiceAsync(van, _f.AdminUserId)).result.Success);
        Assert.Equal(2500m, await db.RepWalletTransactions.Where(w => w.ReferenceId == van).SumAsync(w => w.AmountIn));
        Assert.Equal(StockTransactionType.RepSale,
            (await db.StockTransactions.FirstAsync(t => t.ReferenceTable == "SalesInvoices" && t.ReferenceId == van)).TransactionType);

        var free = await NewInvoiceAsync(svc, new(_f.DirectId, _f.MainWarehouseId, Today, InvoicePaymentMethod.Cash,
            IsFreeSale: true, FreeSaleRecipient: "مستشفى البصرة العام"));
        await svc.AddLineAsync(free, new(_f.WaterItemId, _f.CartonLevelId, 1), _f.AdminUserId);
        var (r, s) = await svc.PostInvoiceAsync(free, _f.AdminUserId);
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(0m, s!.TotalAmount);
        Assert.Null(s.JournalEntryId);
        var tx = await db.StockTransactions.SingleAsync(t => t.ReferenceTable == "SalesInvoices" && t.ReferenceId == free);
        Assert.Equal(StockTransactionType.FreeIssue, tx.TransactionType);
        Assert.Equal("مستشفى البصرة العام", tx.FreeIssueRecipient);
    }
}
