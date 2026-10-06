using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.ProjectDb;

/// <summary>
/// سياق قاعدة بيانات مشروع واحد. يُنشأ اتصال جديد بهذا السياق بعد اختيار
/// المستخدم للمشروع في شاشة تسجيل الدخول (Connection String تُبنى وقت التشغيل
/// من بيانات Projects في قاعدة التحكم).
///
/// المرحلة الحالية (Phase 0): البنية الأساسية والأمان فقط.
/// كل وحدة لاحقة (المخازن، المالية، المبيعات...) تُضاف كـ DbSet جديد هنا
/// عند بناء تلك المرحلة، دون تعديل ما هو موجود.
/// </summary>
public partial class ProjectDbContext : DbContext
{
    public ProjectDbContext(DbContextOptions<ProjectDbContext> options) : base(options) { }

    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<WorkDayException> WorkDayExceptions => Set<WorkDayException>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<User> Users => Set<User>();

    // ---- المخازن (Phase 1.1) ----
    public DbSet<Item> Items => Set<Item>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<ItemBatch> ItemBatches => Set<ItemBatch>();
    public DbSet<StockTransaction> StockTransactions => Set<StockTransaction>();
    public DbSet<ItemPackagingLevel> ItemPackagingLevels => Set<ItemPackagingLevel>();
    public DbSet<WarehouseLocation> WarehouseLocations => Set<WarehouseLocation>();

    // ---- المالية (Phase 1.2) ----
    public DbSet<ChartOfAccount> ChartOfAccounts => Set<ChartOfAccount>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<JournalEntryLine> JournalEntryLines => Set<JournalEntryLine>();
    public DbSet<Voucher> Vouchers => Set<Voucher>();
    public DbSet<AccountMappingRule> AccountMappingRules => Set<AccountMappingRule>();
    public DbSet<ExchangeRate> ExchangeRates => Set<ExchangeRate>();

    // ---- الموردون ----
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<PurchaseOrderLine> PurchaseOrderLines => Set<PurchaseOrderLine>();
    public DbSet<GoodsReceipt> GoodsReceipts => Set<GoodsReceipt>();
    public DbSet<GoodsReceiptLine> GoodsReceiptLines => Set<GoodsReceiptLine>();

    // ---- المبيعات والعملاء ----
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<AgentItemPrice> AgentItemPrices => Set<AgentItemPrice>();
    public DbSet<LoadingSuppliesSetting> LoadingSuppliesSettings => Set<LoadingSuppliesSetting>();
    public DbSet<SalesInvoice> SalesInvoices => Set<SalesInvoice>();
    public DbSet<SalesInvoiceLine> SalesInvoiceLines => Set<SalesInvoiceLine>();

    // ---- الموارد البشرية ----
    public DbSet<AttendanceRecord> AttendanceRecords => Set<AttendanceRecord>();
    public DbSet<PayrollRun> PayrollRuns => Set<PayrollRun>();
    public DbSet<PayrollLine> PayrollLines => Set<PayrollLine>();
    public DbSet<PromotionAndRaise> PromotionsAndRaises => Set<PromotionAndRaise>();
    public DbSet<IncentiveScoreWeights> IncentiveScoreWeights => Set<IncentiveScoreWeights>();
    public DbSet<IncentiveScoreToAmountScale> IncentiveScoreToAmountScale => Set<IncentiveScoreToAmountScale>();
    public DbSet<MonthlyIncentiveEvaluation> MonthlyIncentiveEvaluations => Set<MonthlyIncentiveEvaluation>();
    public DbSet<RepItemIncentiveRate> RepItemIncentiveRates => Set<RepItemIncentiveRate>();
    public DbSet<SalesManagerIncentiveTier> SalesManagerIncentiveTiers => Set<SalesManagerIncentiveTier>();

    // ---- المندوبون ----
    public DbSet<RepWalletTransaction> RepWalletTransactions => Set<RepWalletTransaction>();
    public DbSet<RepTerritory> RepTerritories => Set<RepTerritory>();
    public DbSet<RepCustomerAssignment> RepCustomerAssignments => Set<RepCustomerAssignment>();
    public DbSet<SyncConflict> SyncConflicts => Set<SyncConflict>();

    // ---- الإنتاج والمختبر ----
    public DbSet<BillOfMaterials> BillOfMaterials => Set<BillOfMaterials>();
    public DbSet<BOMLine> BOMLines => Set<BOMLine>();
    public DbSet<PackagingTemplate> PackagingTemplates => Set<PackagingTemplate>();
    public DbSet<PackagingTemplateLine> PackagingTemplateLines => Set<PackagingTemplateLine>();
    public DbSet<ProductionOrderComponentOverride> ProductionOrderComponentOverrides => Set<ProductionOrderComponentOverride>();
    public DbSet<CustomRecipe> CustomRecipes => Set<CustomRecipe>();
    public DbSet<CustomRecipeLine> CustomRecipeLines => Set<CustomRecipeLine>();
    public DbSet<ProductionOrder> ProductionOrders => Set<ProductionOrder>();
    public DbSet<ProductionOrderConsumption> ProductionOrderConsumptions => Set<ProductionOrderConsumption>();
    public DbSet<ProductionOrderLine> ProductionOrderLines => Set<ProductionOrderLine>();
    public DbSet<Machine> Machines => Set<Machine>();
    public DbSet<PaymentAllocation> PaymentAllocations => Set<PaymentAllocation>();
    public DbSet<CustomerDeposit> CustomerDeposits => Set<CustomerDeposit>();
    public DbSet<EmployeeDeduction> EmployeeDeductions => Set<EmployeeDeduction>();
    public DbSet<Partner> Partners => Set<Partner>();
    public DbSet<LegacyImport> LegacyImports => Set<LegacyImport>();
    public DbSet<LegacyImportMapEntry> LegacyImportMap => Set<LegacyImportMapEntry>();
    public DbSet<DamagedSale> DamagedSales => Set<DamagedSale>();
    public DbSet<DamagedSaleLine> DamagedSaleLines => Set<DamagedSaleLine>();
    public DbSet<PartnerTransaction> PartnerTransactions => Set<PartnerTransaction>();
    public DbSet<AssetReconciliation> AssetReconciliations => Set<AssetReconciliation>();
    public DbSet<AssetReconciliationLine> AssetReconciliationLines => Set<AssetReconciliationLine>();
    public DbSet<EmployeeDeductionInstallment> EmployeeDeductionInstallments => Set<EmployeeDeductionInstallment>();
    public DbSet<WipAdjustment> WipAdjustments => Set<WipAdjustment>();
    public DbSet<BatchNumberChange> BatchNumberChanges => Set<BatchNumberChange>();
    public DbSet<QualityTest> QualityTests => Set<QualityTest>();
    public DbSet<QCBatchResult> QCBatchResults => Set<QCBatchResult>();
    public DbSet<QCTestResultLine> QCTestResultLines => Set<QCTestResultLine>();
    public DbSet<PackingOrder> PackingOrders => Set<PackingOrder>();
    public DbSet<StockDocument> StockDocuments => Set<StockDocument>();
    public DbSet<StockDocumentLine> StockDocumentLines => Set<StockDocumentLine>();
    public DbSet<CashBox> CashBoxes => Set<CashBox>();
    public DbSet<CashBoxTransaction> CashBoxTransactions => Set<CashBoxTransaction>();
    public DbSet<CompanyProfile> CompanyProfiles => Set<CompanyProfile>();

    // ---- الضوابط (27_controls.sql) ----
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<PeriodLock> PeriodLocks => Set<PeriodLock>();

    // ---- الإنتاج والمخازن (29_production_stock.sql) ----
    public DbSet<StockCount> StockCounts => Set<StockCount>();
    public DbSet<StockCountLine> StockCountLines => Set<StockCountLine>();
    public DbSet<FreeIssueBeneficiary> FreeIssueBeneficiaries => Set<FreeIssueBeneficiary>();
    public DbSet<PendingProductionShortage> PendingProductionShortages => Set<PendingProductionShortage>();

    // ---- المندوبون: التحميل والتسوية (30_rep_loads_settlement.sql) ----
    public DbSet<RepDefaultLoad> RepDefaultLoads => Set<RepDefaultLoad>();
    public DbSet<RepLoadOrder> RepLoadOrders => Set<RepLoadOrder>();
    public DbSet<RepLoadOrderLine> RepLoadOrderLines => Set<RepLoadOrderLine>();
    public DbSet<RepSettlement> RepSettlements => Set<RepSettlement>();
    public DbSet<RepFreeGood> RepFreeGoods => Set<RepFreeGood>();

    // ---- المصروفات والحسابات الختامية (31_expenses_final_accounts.sql) ----
    public DbSet<FinanceCategory> FinanceCategories => Set<FinanceCategory>();
    public DbSet<FinanceEntry> FinanceEntries => Set<FinanceEntry>();
    public DbSet<WorkingCapitalSetting> WorkingCapitalSettings => Set<WorkingCapitalSetting>();

    // ---- العمال الوقتيون (32_temp_workers.sql) ----
    public DbSet<TempWorkDay> TempWorkDays => Set<TempWorkDay>();
    public DbSet<TempWorkerPayment> TempWorkerPayments => Set<TempWorkerPayment>();
    public DbSet<FingerprintImport> FingerprintImports => Set<FingerprintImport>();

    // ---- متغيرات المنتج وقوالب أمر اليوم (34_batch_variants.sql) ----
    public DbSet<DailyProductionTemplate> DailyProductionTemplates => Set<DailyProductionTemplate>();
    public DbSet<DailyProductionTemplateLine> DailyProductionTemplateLines => Set<DailyProductionTemplateLine>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Employee>().Property(e => e.SocialSecurityAmount).HasPrecision(18, 2);
        modelBuilder.Entity<Employee>().Property(e => e.SocialSolidarityAmount).HasPrecision(18, 2);
        modelBuilder.Entity<PayrollLine>().Property(l => l.SocialSecurityDeduction).HasPrecision(18, 2);
        modelBuilder.Entity<PayrollLine>().Property(l => l.SocialSolidarityDeduction).HasPrecision(18, 2);
        modelBuilder.Entity<Employee>()
            .Property(e => e.SalaryCurrency)
            .HasConversion<string>()
            .HasMaxLength(3);

        modelBuilder.Entity<RolePermission>()
            .HasIndex(rp => new { rp.RoleId, rp.ModuleCode })
            .IsUnique();

        modelBuilder.Entity<User>()
            .HasIndex(u => u.Username)
            .IsUnique();

        modelBuilder.Entity<User>()
            .HasOne(u => u.Employee)
            .WithMany()
            .HasForeignKey(u => u.EmployeeId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<User>()
            .HasOne(u => u.Role)
            .WithMany()
            .HasForeignKey(u => u.RoleId)
            .OnDelete(DeleteBehavior.Restrict);

        // ---- المخازن ----
        modelBuilder.Entity<Item>()
            .HasIndex(i => i.ItemCode)
            .IsUnique();
        modelBuilder.Entity<Item>()
            .Property(i => i.SourcingMethod)
            .HasConversion<string>();

        modelBuilder.Entity<Warehouse>()
            .Property(w => w.WarehouseType)
            .HasConversion<string>();

        modelBuilder.Entity<ItemBatch>()
            .HasIndex(b => new { b.ItemId, b.BatchNumber })
            .IsUnique();

        modelBuilder.Entity<StockTransaction>()
            .Property(t => t.TransactionType)
            .HasConversion<string>();
        modelBuilder.Entity<StockTransaction>()
            .Property(t => t.DamageReason)
            .HasConversion<string>();
        modelBuilder.Entity<StockTransaction>()
            .HasOne(t => t.CreatedByUser)
            .WithMany()
            .HasForeignKey(t => t.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<StockTransaction>()
            .HasOne(t => t.Location)
            .WithMany()
            .HasForeignKey(t => t.LocationId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ItemPackagingLevel>()
            .HasOne(p => p.ParentLevel)
            .WithMany()
            .HasForeignKey(p => p.ParentLevelId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<WarehouseLocation>()
            .Property(l => l.LevelType)
            .HasConversion<string>();
        modelBuilder.Entity<WarehouseLocation>()
            .HasOne(l => l.ParentLocation)
            .WithMany()
            .HasForeignKey(l => l.ParentLocationId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<WarehouseLocation>()
            .HasOne(l => l.Warehouse)
            .WithMany()
            .HasForeignKey(l => l.WarehouseId)
            .OnDelete(DeleteBehavior.Restrict);

        // ---- المالية ----
        modelBuilder.Entity<ChartOfAccount>()
            .HasIndex(a => a.AccountCode)
            .IsUnique();
        modelBuilder.Entity<ChartOfAccount>()
            .Property(a => a.AccountType)
            .HasConversion<string>();
        modelBuilder.Entity<ChartOfAccount>()
            .HasOne(a => a.ParentAccount)
            .WithMany()
            .HasForeignKey(a => a.ParentAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<JournalEntry>()
            .HasIndex(j => j.EntryNumber)
            .IsUnique();
        modelBuilder.Entity<JournalEntry>()
            .Property(j => j.EntryType)
            .HasConversion<string>();
        modelBuilder.Entity<JournalEntry>()
            .HasOne(j => j.CreatedByUser)
            .WithMany()
            .HasForeignKey(j => j.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
        // IsBalanced خاصية محسوبة في الذاكرة فقط، لا تُخزَّن في قاعدة البيانات
        modelBuilder.Entity<JournalEntry>().Ignore(j => j.IsBalanced);

        modelBuilder.Entity<JournalEntryLine>()
            .HasOne(l => l.JournalEntry)
            .WithMany(j => j.Lines)
            .HasForeignKey(l => l.JournalEntryId);

        modelBuilder.Entity<Voucher>()
            .HasIndex(v => v.VoucherNumber)
            .IsUnique();
        modelBuilder.Entity<CompanyProfile>(e =>
        {
            e.ToTable("CompanyProfile");
            e.Property(p => p.Id).ValueGeneratedNever();
        });

        // ---- مستندات المخزن والصناديق (12_warehouse_docs_cashboxes.sql) ----
        modelBuilder.Entity<StockDocument>(e =>
        {
            e.Property(d => d.DocumentType).HasConversion<string>();
            e.Property(d => d.DamageReason).HasConversion<string>();
            e.Property(d => d.BeneficiaryCategory).HasConversion<string>();
            e.HasOne(d => d.Warehouse).WithMany().HasForeignKey(d => d.WarehouseId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(d => d.CounterWarehouse).WithMany().HasForeignKey(d => d.CounterWarehouseId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(d => d.RepEmployee).WithMany().HasForeignKey(d => d.RepEmployeeId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(d => d.CreatedByUser).WithMany().HasForeignKey(d => d.CreatedByUserId).OnDelete(DeleteBehavior.NoAction);
            e.HasMany(d => d.Lines).WithOne(l => l.StockDocument).HasForeignKey(l => l.StockDocumentId);
        });
        modelBuilder.Entity<StockDocumentLine>().Property(l => l.QuantityInLevel).HasPrecision(18, 3);
        modelBuilder.Entity<StockDocumentLine>().Property(l => l.QuantityBaseUnits).HasPrecision(18, 3);
        modelBuilder.Entity<CashBox>(e =>
        {
            e.Property(b => b.BoxType).HasConversion<string>();
            e.HasOne(b => b.OwnerUser).WithMany().HasForeignKey(b => b.OwnerUserId).OnDelete(DeleteBehavior.NoAction);
        });
        modelBuilder.Entity<CashBoxTransaction>(e =>
        {
            e.Property(t => t.TxType).HasConversion<string>();
            e.Property(t => t.Amount).HasPrecision(18, 2);
            e.Property(t => t.OriginalAmount).HasPrecision(18, 2);
            e.HasOne(t => t.CashBox).WithMany().HasForeignKey(t => t.CashBoxId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(t => t.CounterCashBox).WithMany().HasForeignKey(t => t.CounterCashBoxId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(t => t.CreatedByUser).WithMany().HasForeignKey(t => t.CreatedByUserId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(t => t.ModifiedByUser).WithMany().HasForeignKey(t => t.ModifiedByUserId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<Voucher>().Property(v => v.VoucherType).HasConversion<string>();
        modelBuilder.Entity<Voucher>().Property(v => v.PartyType).HasConversion<string>();
        modelBuilder.Entity<Voucher>().Property(v => v.PaymentMethod).HasConversion<string>();
        modelBuilder.Entity<Voucher>()
            .HasOne(v => v.CreatedByUser)
            .WithMany()
            .HasForeignKey(v => v.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AccountMappingRule>()
            .HasIndex(r => r.TransactionType)
            .IsUnique();
        modelBuilder.Entity<AccountMappingRule>()
            .HasOne(r => r.DebitAccount)
            .WithMany()
            .HasForeignKey(r => r.DebitAccountId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AccountMappingRule>()
            .HasOne(r => r.CreditAccount)
            .WithMany()
            .HasForeignKey(r => r.CreditAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        // ================= الموردون =================
        modelBuilder.Entity<Supplier>().Property(s => s.DefaultPaymentTerms).HasConversion<string>();

        modelBuilder.Entity<PurchaseOrder>().HasIndex(p => p.PONumber).IsUnique();
        modelBuilder.Entity<PurchaseOrder>().Property(p => p.Status).HasConversion<string>();
        modelBuilder.Entity<PurchaseOrder>().Property(p => p.PaymentTerms).HasConversion<string>();
        modelBuilder.Entity<PurchaseOrder>().HasOne(p => p.Supplier).WithMany().HasForeignKey(p => p.SupplierId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PurchaseOrder>().HasOne(p => p.Warehouse).WithMany().HasForeignKey(p => p.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PurchaseOrder>().HasOne(p => p.CreatedByUser).WithMany().HasForeignKey(p => p.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PurchaseOrderLine>().HasOne(l => l.PurchaseOrder).WithMany(p => p.Lines).HasForeignKey(l => l.PurchaseOrderId);
        modelBuilder.Entity<PurchaseOrderLine>().HasOne(l => l.Item).WithMany().HasForeignKey(l => l.ItemId).OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<GoodsReceipt>().HasIndex(g => g.ReceiptNumber).IsUnique();
        modelBuilder.Entity<GoodsReceipt>().Property(g => g.Status).HasConversion<string>();
        modelBuilder.Entity<GoodsReceipt>().HasOne(g => g.Supplier).WithMany().HasForeignKey(g => g.SupplierId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<GoodsReceipt>().HasOne(g => g.Warehouse).WithMany().HasForeignKey(g => g.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<GoodsReceipt>().HasOne(g => g.CreatedByUser).WithMany().HasForeignKey(g => g.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<GoodsReceiptLine>().HasOne(l => l.GoodsReceipt).WithMany(g => g.Lines).HasForeignKey(l => l.GoodsReceiptId);
        modelBuilder.Entity<GoodsReceiptLine>().HasOne(l => l.Item).WithMany().HasForeignKey(l => l.ItemId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<GoodsReceiptLine>().HasOne(l => l.Batch).WithMany().HasForeignKey(l => l.BatchId).OnDelete(DeleteBehavior.Restrict);

        // ================= المبيعات والعملاء =================
        modelBuilder.Entity<Customer>().Property(c => c.CustomerType).HasConversion<string>();
        modelBuilder.Entity<Customer>().Property(c => c.CreditLimit).HasPrecision(18, 2);
        modelBuilder.Entity<SalesInvoiceLine>().Property(l => l.ListUnitPrice).HasPrecision(18, 2);
        modelBuilder.Entity<Customer>().HasOne(c => c.ParentAgent).WithMany().HasForeignKey(c => c.ParentAgentId).OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AgentItemPrice>().HasIndex(a => new { a.CustomerId, a.ItemId }).IsUnique();
        modelBuilder.Entity<AgentItemPrice>().HasOne(a => a.Customer).WithMany().HasForeignKey(a => a.CustomerId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AgentItemPrice>().HasOne(a => a.Item).WithMany().HasForeignKey(a => a.ItemId).OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<LegacyImport>().HasOne(i => i.ImportedByUser).WithMany().HasForeignKey(i => i.ImportedByUserId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<LegacyImportMapEntry>().ToTable("LegacyImportMap");
        modelBuilder.Entity<SalesInvoice>().HasIndex(s => s.InvoiceNumber).IsUnique();
        modelBuilder.Entity<SalesInvoice>().Property(s => s.PaymentMethod).HasConversion<string>();
        modelBuilder.Entity<SalesInvoice>().Property(s => s.Status).HasConversion<string>();
        modelBuilder.Entity<SalesInvoice>().HasOne(s => s.Customer).WithMany().HasForeignKey(s => s.CustomerId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SalesInvoice>().HasOne(s => s.Warehouse).WithMany().HasForeignKey(s => s.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SalesInvoice>().HasOne(s => s.SalesRepEmployee).WithMany().HasForeignKey(s => s.SalesRepEmployeeId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SalesInvoice>().HasOne(s => s.CreatedByUser).WithMany().HasForeignKey(s => s.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SalesInvoice>().HasOne(s => s.PostedByUser).WithMany().HasForeignKey(s => s.PostedByUserId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SalesInvoiceLine>().HasOne(l => l.SalesInvoice).WithMany(s => s.Lines).HasForeignKey(l => l.SalesInvoiceId);
        modelBuilder.Entity<SalesInvoiceLine>().HasOne(l => l.Item).WithMany().HasForeignKey(l => l.ItemId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SalesInvoiceLine>().HasOne(l => l.Batch).WithMany().HasForeignKey(l => l.BatchId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SalesInvoiceLine>().HasOne(l => l.PackagingLevel).WithMany().HasForeignKey(l => l.PackagingLevelId).OnDelete(DeleteBehavior.Restrict);

        // ================= الموارد البشرية =================
        modelBuilder.Entity<AttendanceRecord>().HasIndex(a => new { a.EmployeeId, a.AttendanceDate }).IsUnique();
        modelBuilder.Entity<AttendanceRecord>().Property(a => a.Status).HasConversion<string>();
        modelBuilder.Entity<AttendanceRecord>().HasOne(a => a.Employee).WithMany().HasForeignKey(a => a.EmployeeId).OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PayrollRun>().HasIndex(p => new { p.PeriodMonth, p.PeriodYear }).IsUnique();
        // الأعمدة في SQL من نوع TINYINT/SMALLINT بينما الخاصية int — بدون التحويل تفشل القراءة عند أول صف
        modelBuilder.Entity<PayrollRun>().Property(p => p.PeriodMonth).HasConversion<byte>();
        modelBuilder.Entity<PayrollRun>().Property(p => p.PeriodYear).HasConversion<short>();
        modelBuilder.Entity<MonthlyIncentiveEvaluation>().Property(m => m.PeriodMonth).HasConversion<byte>();
        modelBuilder.Entity<MonthlyIncentiveEvaluation>().Property(m => m.PeriodYear).HasConversion<short>();
        modelBuilder.Entity<PayrollRun>().HasOne(p => p.ApprovedByUser).WithMany().HasForeignKey(p => p.ApprovedByUserId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PayrollRun>().HasOne(p => p.JournalEntry).WithMany().HasForeignKey(p => p.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ExchangeRate>().HasOne(r => r.EnteredByUser).WithMany().HasForeignKey(r => r.EnteredByUserId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AttendanceRecord>().Property(a => a.AttendanceDate).HasColumnType("date");
        modelBuilder.Entity<PayrollRun>().Property(p => p.Status).HasConversion<string>();
        modelBuilder.Entity<PayrollLine>().HasOne(l => l.PayrollRun).WithMany(p => p.Lines).HasForeignKey(l => l.PayrollRunId);
        modelBuilder.Entity<PayrollLine>().HasOne(l => l.Employee).WithMany().HasForeignKey(l => l.EmployeeId).OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PromotionAndRaise>().Property(p => p.MovementType).HasConversion<string>();
        modelBuilder.Entity<PromotionAndRaise>().Property(p => p.ApplicationType).HasConversion<string>();
        modelBuilder.Entity<PromotionAndRaise>().HasOne(p => p.Employee).WithMany().HasForeignKey(p => p.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PromotionAndRaise>().HasOne(p => p.CreatedByUser).WithMany().HasForeignKey(p => p.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<MonthlyIncentiveEvaluation>().HasIndex(m => new { m.EmployeeId, m.PeriodMonth, m.PeriodYear }).IsUnique();
        modelBuilder.Entity<MonthlyIncentiveEvaluation>().HasOne(m => m.Employee).WithMany().HasForeignKey(m => m.EmployeeId).OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<RepItemIncentiveRate>().HasIndex(r => r.ItemId).IsUnique();
        modelBuilder.Entity<RepItemIncentiveRate>().HasOne(r => r.Item).WithMany().HasForeignKey(r => r.ItemId).OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<SalesManagerIncentiveTier>().HasOne(t => t.Employee).WithMany().HasForeignKey(t => t.EmployeeId).OnDelete(DeleteBehavior.Restrict);

        // ================= المندوبون =================
        modelBuilder.Entity<RepWalletTransaction>().HasOne(w => w.Employee).WithMany().HasForeignKey(w => w.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<RepWalletTransaction>().HasOne(w => w.Vehicle).WithMany().HasForeignKey(w => w.VehicleId).OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<RepTerritory>().HasIndex(t => new { t.EmployeeId, t.TerritoryName }).IsUnique();
        modelBuilder.Entity<RepTerritory>().HasOne(t => t.Employee).WithMany().HasForeignKey(t => t.EmployeeId).OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<RepCustomerAssignment>().HasIndex(r => new { r.EmployeeId, r.CustomerId }).IsUnique();
        modelBuilder.Entity<RepCustomerAssignment>().HasOne(r => r.Employee).WithMany().HasForeignKey(r => r.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<RepCustomerAssignment>().HasOne(r => r.Customer).WithMany().HasForeignKey(r => r.CustomerId).OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<SyncConflict>().Property(s => s.Status).HasConversion<string>();
        modelBuilder.Entity<SyncConflict>().HasOne(s => s.StockTransaction).WithMany().HasForeignKey(s => s.StockTransactionId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SyncConflict>().HasOne(s => s.Employee).WithMany().HasForeignKey(s => s.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SyncConflict>().HasOne(s => s.Item).WithMany().HasForeignKey(s => s.ItemId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SyncConflict>().HasOne(s => s.Batch).WithMany().HasForeignKey(s => s.BatchId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SyncConflict>().HasOne(s => s.ResolvedByUser).WithMany().HasForeignKey(s => s.ResolvedByUserId).OnDelete(DeleteBehavior.Restrict);

        // ================= الإنتاج والمختبر =================
        modelBuilder.Entity<BillOfMaterials>().HasOne(b => b.FinishedItem).WithMany().HasForeignKey(b => b.FinishedItemId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<BOMLine>().HasOne(l => l.BOM).WithMany(b => b.Lines).HasForeignKey(l => l.BOMId);
        modelBuilder.Entity<BOMLine>().Property(l => l.QuantityPerUnit).HasPrecision(18, 6);
        modelBuilder.Entity<BillOfMaterials>().HasOne(b => b.PackagingTemplate).WithMany().HasForeignKey(b => b.PackagingTemplateId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PackagingTemplate>().HasIndex(t => t.Name).IsUnique();
        modelBuilder.Entity<PackagingTemplateLine>().HasOne(l => l.Template).WithMany(t => t.Lines).HasForeignKey(l => l.TemplateId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<PackagingTemplateLine>().HasOne(l => l.DefaultItem).WithMany().HasForeignKey(l => l.DefaultItemId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PackagingTemplateLine>().Property(l => l.ComponentQuantity).HasPrecision(18, 4);
        modelBuilder.Entity<PackagingTemplateLine>().Property(l => l.PerUnits).HasPrecision(18, 4);
        modelBuilder.Entity<PackagingTemplateLine>().Ignore(l => l.QuantityPerUnit);
        modelBuilder.Entity<PackagingTemplateLine>().Ignore(l => l.RatioText);
        modelBuilder.Entity<ProductionOrderComponentOverride>().HasOne(o => o.Line).WithMany().HasForeignKey(o => o.ProductionOrderLineId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ProductionOrderComponentOverride>().HasOne(o => o.OriginalItem).WithMany().HasForeignKey(o => o.OriginalItemId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ProductionOrderComponentOverride>().HasOne(o => o.ReplacementItem).WithMany().HasForeignKey(o => o.ReplacementItemId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ProductionOrderComponentOverride>().HasOne(o => o.ChangedByUser).WithMany().HasForeignKey(o => o.ChangedByUserId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ProductionOrderComponentOverride>().Property(o => o.Quantity).HasPrecision(18, 4);
        modelBuilder.Entity<BOMLine>().HasOne(l => l.RawMaterialItem).WithMany().HasForeignKey(l => l.RawMaterialItemId).OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<CustomRecipe>().HasOne(c => c.FinishedItem).WithMany().HasForeignKey(c => c.FinishedItemId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<CustomRecipe>().HasOne(c => c.Customer).WithMany().HasForeignKey(c => c.CustomerId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<CustomRecipeLine>().HasOne(l => l.CustomRecipe).WithMany(c => c.Lines).HasForeignKey(l => l.CustomRecipeId);
        modelBuilder.Entity<CustomRecipeLine>().HasOne(l => l.ComponentItem).WithMany().HasForeignKey(l => l.ComponentItemId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<CustomRecipeLine>().HasOne(l => l.ReplacesRawMaterialItem).WithMany().HasForeignKey(l => l.ReplacesRawMaterialItemId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PackingOrder>().Property(p => p.PackingDate);
        modelBuilder.Entity<Vehicle>().HasOne(v => v.AssignedEmployee).WithMany().HasForeignKey(v => v.AssignedEmployeeId).OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ProductionOrder>().HasIndex(p => p.MONumber).IsUnique();
        modelBuilder.Entity<ProductionOrder>().Property(p => p.Status).HasConversion<string>();
        modelBuilder.Entity<ProductionOrder>().HasOne(p => p.FinishedItem).WithMany().HasForeignKey(p => p.FinishedItemId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ProductionOrder>().HasOne(p => p.BOM).WithMany().HasForeignKey(p => p.BOMId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ProductionOrder>().HasOne(p => p.CustomRecipe).WithMany().HasForeignKey(p => p.CustomRecipeId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ProductionOrder>().HasOne(p => p.RawMaterialsWarehouse).WithMany().HasForeignKey(p => p.RawMaterialsWarehouseId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ProductionOrder>().HasOne(p => p.Machine).WithMany().HasForeignKey(p => p.MachineId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Machine>().HasIndex(m => m.Name).IsUnique();
        modelBuilder.Entity<SalesInvoice>().Property(i => i.AmountSettled).HasPrecision(18, 2);
        modelBuilder.Entity<SalesInvoice>().Property(i => i.AmountRemaining).HasPrecision(18, 2)
            .HasComputedColumnSql("[TotalAmount] - [AmountSettled]").ValueGeneratedOnAddOrUpdate();
        modelBuilder.Entity<PaymentAllocation>().Property(a => a.Amount).HasPrecision(18, 2);
        modelBuilder.Entity<PaymentAllocation>().HasOne(a => a.Voucher).WithMany().HasForeignKey(a => a.VoucherId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PaymentAllocation>().HasOne(a => a.SalesInvoice).WithMany().HasForeignKey(a => a.SalesInvoiceId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PaymentAllocation>().HasOne(a => a.AllocatedByUser).WithMany().HasForeignKey(a => a.AllocatedByUserId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Item>().Property(i => i.CostPrice).HasPrecision(18, 4);
        modelBuilder.Entity<Item>().Property(i => i.UnitWeightGrams).HasPrecision(18, 3);
        modelBuilder.Entity<StockTransaction>().Property(t => t.UnitCost).HasPrecision(18, 4);
        modelBuilder.Entity<GoodsReceiptLine>().Property(l => l.UnitCost).HasPrecision(18, 4);
        modelBuilder.Entity<GoodsReceiptLine>().Property(l => l.PurchaseQuantity).HasPrecision(18, 3);
        modelBuilder.Entity<GoodsReceiptLine>().Property(l => l.PurchaseUnitPrice).HasPrecision(18, 4);
        modelBuilder.Entity<PurchaseOrderLine>().Property(l => l.ExpectedUnitCost).HasPrecision(18, 4);
        modelBuilder.Entity<DamagedSale>(e =>
        {
            e.Property(s => s.TotalAmount).HasPrecision(18, 2);
            e.HasOne(s => s.StockDocument).WithMany().HasForeignKey(s => s.StockDocumentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(s => s.JournalEntry).WithMany().HasForeignKey(s => s.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(s => s.CreatedByUser).WithMany().HasForeignKey(s => s.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(s => s.Lines).WithOne(l => l.DamagedSale).HasForeignKey(l => l.DamagedSaleId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<DamagedSaleLine>(e =>
        {
            e.Property(l => l.Quantity).HasPrecision(18, 3);
            e.Property(l => l.UnitPrice).HasPrecision(18, 4);
            e.Property(l => l.Amount).HasPrecision(18, 2);
            e.HasOne(l => l.Item).WithMany().HasForeignKey(l => l.ItemId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<Partner>(e =>
        {
            e.Property(p => p.SharePercent).HasPrecision(7, 4);
            e.HasIndex(p => p.Name).IsUnique();
        });
        modelBuilder.Entity<PartnerTransaction>(e =>
        {
            e.Property(t => t.Kind).HasConversion<string>();
            e.Property(t => t.Amount).HasPrecision(18, 2);
            e.Property(t => t.SharePercent).HasPrecision(7, 4);
            e.HasOne(t => t.Partner).WithMany().HasForeignKey(t => t.PartnerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(t => t.Reconciliation).WithMany().HasForeignKey(t => t.ReconciliationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(t => t.JournalEntry).WithMany().HasForeignKey(t => t.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(t => t.CreatedByUser).WithMany().HasForeignKey(t => t.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<AssetReconciliation>(e =>
        {
            e.Property(r => r.FinishedGoodsValuation).HasConversion<string>();
            foreach (var p in new[] { nameof(AssetReconciliation.RawMaterialsValue), nameof(AssetReconciliation.WorkInProcessValue), nameof(AssetReconciliation.FinishedGoodsValue),
                                      nameof(AssetReconciliation.CustomerDebts), nameof(AssetReconciliation.CashInBoxes), nameof(AssetReconciliation.CashWithReps),
                                      nameof(AssetReconciliation.EmployeeAdvances), nameof(AssetReconciliation.SupplierAdvances), nameof(AssetReconciliation.SupplierDebts),
                                      nameof(AssetReconciliation.CustomerDeposits), nameof(AssetReconciliation.NetAssets), nameof(AssetReconciliation.PreviousNetAssets),
                                      nameof(AssetReconciliation.PartnerWithdrawalsSincePrevious), nameof(AssetReconciliation.OwnerDepositsSincePrevious), nameof(AssetReconciliation.Surplus) })
                e.Property(p).HasPrecision(18, 2);
            e.HasOne(r => r.PreviousReconciliation).WithMany().HasForeignKey(r => r.PreviousReconciliationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.JournalEntry).WithMany().HasForeignKey(r => r.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.CreatedByUser).WithMany().HasForeignKey(r => r.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(r => r.Lines).WithOne(l => l.Reconciliation).HasForeignKey(l => l.ReconciliationId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<AssetReconciliationLine>(e =>
        {
            e.Property(l => l.Quantity).HasPrecision(18, 3);
            e.Property(l => l.UnitValue).HasPrecision(18, 4);
            e.Property(l => l.Value).HasPrecision(18, 2);
        });
        modelBuilder.Entity<EmployeeDeduction>(e =>
        {
            e.Property(d => d.Kind).HasConversion<string>();
            e.Property(d => d.Amount).HasPrecision(18, 2);
            e.Property(d => d.MonthlyInstallment).HasPrecision(18, 2);
            e.Ignore(d => d.PaysCash);
            e.HasOne(d => d.Employee).WithMany().HasForeignKey(d => d.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(d => d.JournalEntry).WithMany().HasForeignKey(d => d.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(d => d.CreatedByUser).WithMany().HasForeignKey(d => d.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(d => d.Installments).WithOne(i => i.EmployeeDeduction).HasForeignKey(i => i.EmployeeDeductionId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<EmployeeDeductionInstallment>(e =>
        {
            e.Property(i => i.Amount).HasPrecision(18, 2);
            e.HasOne(i => i.PayrollRun).WithMany().HasForeignKey(i => i.PayrollRunId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<CustomerDeposit>(e =>
        {
            e.Property(d => d.Kind).HasConversion<string>();
            e.Property(d => d.Amount).HasPrecision(18, 2);
            e.Property(d => d.CurrencyAmount).HasPrecision(18, 2);
            e.Ignore(d => d.SignedAmount);
            e.HasOne(d => d.Customer).WithMany().HasForeignKey(d => d.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(d => d.CustomRecipe).WithMany().HasForeignKey(d => d.CustomRecipeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(d => d.JournalEntry).WithMany().HasForeignKey(d => d.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(d => d.CreatedByUser).WithMany().HasForeignKey(d => d.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<WipAdjustment>().Property(a => a.Kind).HasConversion<string>();
        modelBuilder.Entity<WipAdjustment>().Property(a => a.BeforeQuantity).HasPrecision(18, 3);
        modelBuilder.Entity<WipAdjustment>().Property(a => a.AfterQuantity).HasPrecision(18, 3);
        modelBuilder.Entity<WipAdjustment>().HasOne(a => a.Machine).WithMany().HasForeignKey(a => a.MachineId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<WipAdjustment>().HasOne(a => a.Item).WithMany().HasForeignKey(a => a.ItemId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<WipAdjustment>().HasOne(a => a.ProductionOrder).WithMany().HasForeignKey(a => a.ProductionOrderId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<WipAdjustment>().HasOne(a => a.ChangedByUser).WithMany().HasForeignKey(a => a.ChangedByUserId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<BatchNumberChange>().HasOne(c => c.Batch).WithMany().HasForeignKey(c => c.BatchId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<BatchNumberChange>().HasOne(c => c.ChangedByUser).WithMany().HasForeignKey(c => c.ChangedByUserId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Machine>().HasIndex(m => m.WipWarehouseId).IsUnique();
        modelBuilder.Entity<Machine>().HasOne(m => m.WipWarehouse).WithMany().HasForeignKey(m => m.WipWarehouseId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ProductionOrder>().HasOne(p => p.OutputBatch).WithMany().HasForeignKey(p => p.OutputBatchId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ProductionOrder>().HasOne(p => p.CreatedByUser).WithMany().HasForeignKey(p => p.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ProductionOrderConsumption>().HasOne(c => c.ProductionOrder).WithMany(p => p.Consumptions).HasForeignKey(c => c.ProductionOrderId);
        modelBuilder.Entity<ProductionOrderLine>().HasOne(l => l.ProductionOrder).WithMany(o => o.Lines).HasForeignKey(l => l.ProductionOrderId);
        modelBuilder.Entity<ProductionOrderLine>().HasOne(l => l.FinishedItem).WithMany().HasForeignKey(l => l.FinishedItemId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ProductionOrderLine>().HasOne(l => l.BOM).WithMany().HasForeignKey(l => l.BOMId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ProductionOrderLine>().HasOne(l => l.CustomRecipe).WithMany().HasForeignKey(l => l.CustomRecipeId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ProductionOrderLine>().HasOne(l => l.OutputBatch).WithMany().HasForeignKey(l => l.OutputBatchId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ProductionOrderLine>().HasIndex(l => new { l.ProductionOrderId, l.FinishedItemId }).IsUnique();
        modelBuilder.Entity<ProductionOrderConsumption>().HasOne(c => c.Line).WithMany().HasForeignKey(c => c.ProductionOrderLineId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ProductionOrderConsumption>().HasOne(c => c.RawMaterialItem).WithMany().HasForeignKey(c => c.RawMaterialItemId).OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<QualityTest>().HasOne(q => q.ApplicableItem).WithMany().HasForeignKey(q => q.ApplicableItemId).OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<QCBatchResult>().Property(q => q.OverallResult).HasConversion<string>();
        modelBuilder.Entity<QCBatchResult>().HasOne(q => q.ProductionOrder).WithMany().HasForeignKey(q => q.ProductionOrderId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<QCBatchResult>().HasOne(q => q.Batch).WithMany().HasForeignKey(q => q.BatchId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<QCBatchResult>().HasOne(q => q.TestedByUser).WithMany().HasForeignKey(q => q.TestedByUserId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<QCTestResultLine>().Property(q => q.Result).HasConversion<string>();
        modelBuilder.Entity<QCTestResultLine>().HasOne(l => l.QCBatchResult).WithMany(q => q.ResultLines).HasForeignKey(l => l.QCBatchResultId);
        modelBuilder.Entity<QCTestResultLine>().HasOne(l => l.QualityTest).WithMany().HasForeignKey(l => l.QualityTestId).OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PackingOrder>().HasOne(p => p.ProductionOrder).WithMany().HasForeignKey(p => p.ProductionOrderId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PackingOrder>().HasOne(p => p.PackagingLevel).WithMany().HasForeignKey(p => p.PackagingLevelId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PackingOrder>().HasOne(p => p.ResultingFinishedGoodsWarehouse).WithMany().HasForeignKey(p => p.ResultingFinishedGoodsWarehouseId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PackingOrder>().HasOne(p => p.CreatedByUser).WithMany().HasForeignKey(p => p.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PackingOrder>().HasOne(p => p.Line).WithMany().HasForeignKey(p => p.ProductionOrderLineId).OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PeriodLock>(e =>
        {
            e.Property(p => p.LockedThrough).HasColumnType("date");
            e.HasOne(p => p.User).WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        // جداول عليها مشغّلات قفل الفترة: EF لا يستخدم OUTPUT المباشر معها
        foreach (var t in new[] { typeof(JournalEntry), typeof(SalesInvoice), typeof(Voucher), typeof(CashBoxTransaction), typeof(StockDocument), typeof(GoodsReceipt) })
            modelBuilder.Entity(t).ToTable(tb => tb.HasTrigger("trg_" + tb.Name + "_PeriodLock"));
        modelBuilder.Entity<StockCount>(e =>
        {
            e.Property(c => c.CountDate).HasColumnType("date");
            e.Property(c => c.ShortageValue).HasPrecision(18, 2);
            e.Property(c => c.SurplusValue).HasPrecision(18, 2);
            e.HasOne(c => c.Warehouse).WithMany().HasForeignKey(c => c.WarehouseId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(c => c.CreatedByUser).WithMany().HasForeignKey(c => c.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(c => c.Lines).WithOne(l => l.StockCount).HasForeignKey(l => l.StockCountId);
            e.ToTable(tb => tb.HasTrigger("trg_StockCounts_PeriodLock"));
        });
        modelBuilder.Entity<StockCountLine>(e =>
        {
            e.Property(l => l.SystemQuantity).HasPrecision(18, 3);
            e.Property(l => l.CountedQuantity).HasPrecision(18, 3);
            e.Property(l => l.UnitCost).HasPrecision(18, 4);
            e.Property(l => l.VarianceValue).HasPrecision(18, 2);
            e.HasOne(l => l.Item).WithMany().HasForeignKey(l => l.ItemId).OnDelete(DeleteBehavior.Restrict);
            e.Ignore(l => l.Variance);
        });
        modelBuilder.Entity<FreeIssueBeneficiary>().Property(b => b.Category).HasConversion<string>();
        modelBuilder.Entity<PendingProductionShortage>(e =>
        {
            e.Property(p => p.Quantity).HasPrecision(18, 3);
            e.Property(p => p.SettledQuantity).HasPrecision(18, 3);
            e.HasOne(p => p.Item).WithMany().HasForeignKey(p => p.ItemId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(p => p.Warehouse).WithMany().HasForeignKey(p => p.WarehouseId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(p => p.SalesInvoice).WithMany().HasForeignKey(p => p.SalesInvoiceId).OnDelete(DeleteBehavior.Restrict);
            e.Ignore(p => p.Open);
        });
        modelBuilder.Entity<RepDefaultLoad>(e =>
        {
            e.Property(l => l.QuantityInLevel).HasPrecision(18, 3);
            e.HasOne(l => l.RepEmployee).WithMany().HasForeignKey(l => l.RepEmployeeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.Item).WithMany().HasForeignKey(l => l.ItemId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.PackagingLevel).WithMany().HasForeignKey(l => l.PackagingLevelId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<RepLoadOrder>(e =>
        {
            e.Property(o => o.Status).HasConversion<string>();
            e.Property(o => o.LoadDate).HasColumnType("date");
            e.HasOne(o => o.RepEmployee).WithMany().HasForeignKey(o => o.RepEmployeeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(o => o.VanWarehouse).WithMany().HasForeignKey(o => o.VanWarehouseId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(o => o.FromWarehouse).WithMany().HasForeignKey(o => o.FromWarehouseId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(o => o.RequestedByUser).WithMany().HasForeignKey(o => o.RequestedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(o => o.PreparedByUser).WithMany().HasForeignKey(o => o.PreparedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(o => o.StockDocument).WithMany().HasForeignKey(o => o.StockDocumentId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(o => o.Lines).WithOne(l => l.RepLoadOrder).HasForeignKey(l => l.RepLoadOrderId);
        });
        modelBuilder.Entity<RepLoadOrderLine>(e =>
        {
            e.Property(l => l.QuantityInLevel).HasPrecision(18, 3);
            e.Property(l => l.PreparedQuantity).HasPrecision(18, 3);
            e.HasOne(l => l.Item).WithMany().HasForeignKey(l => l.ItemId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.PackagingLevel).WithMany().HasForeignKey(l => l.PackagingLevelId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<RepSettlement>(e =>
        {
            e.Property(x => x.SettlementDate).HasColumnType("date");
            foreach (var p in new[] { nameof(RepSettlement.ReturnedPieces), nameof(RepSettlement.FreePieces) }) e.Property(p).HasPrecision(18, 3);
            foreach (var p in new[] { nameof(RepSettlement.FreeCost), nameof(RepSettlement.FieldExpenses), nameof(RepSettlement.ExpectedCash),
                                      nameof(RepSettlement.ReceivedCash), nameof(RepSettlement.Difference) }) e.Property(p).HasPrecision(18, 2);
            e.HasOne(x => x.RepEmployee).WithMany().HasForeignKey(x => x.RepEmployeeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.VanWarehouse).WithMany().HasForeignKey(x => x.VanWarehouseId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ReturnDocument).WithMany().HasForeignKey(x => x.ReturnDocumentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.AutoInvoice).WithMany().HasForeignKey(x => x.AutoInvoiceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.CreatedByUser).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.FreeGoods).WithOne(f => f.RepSettlement).HasForeignKey(f => f.RepSettlementId);
            e.ToTable(tb => tb.HasTrigger("trg_RepSettlements_PeriodLock"));
        });
        modelBuilder.Entity<RepFreeGood>(e =>
        {
            e.Property(f => f.QuantityBaseUnits).HasPrecision(18, 3);
            e.Property(f => f.UnitCost).HasPrecision(18, 4);
            e.HasOne(f => f.Item).WithMany().HasForeignKey(f => f.ItemId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(f => f.Customer).WithMany().HasForeignKey(f => f.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<FinanceCategory>(e =>
        {
            e.Property(c => c.Kind).HasConversion<string>();
            e.HasOne(c => c.Account).WithMany().HasForeignKey(c => c.AccountId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<FinanceEntry>(e =>
        {
            e.Property(x => x.EntryDate).HasColumnType("date");
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Vehicle).WithMany().HasForeignKey(x => x.VehicleId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.JournalEntry).WithMany().HasForeignKey(x => x.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.CreatedByUser).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(tb => tb.HasTrigger("trg_FinanceEntries_PeriodLock"));
        });
        modelBuilder.Entity<WorkingCapitalSetting>(e =>
        {
            e.Property(x => x.EffectiveFrom).HasColumnType("date");
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.HasOne(x => x.CreatedByUser).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<Employee>().Property(e => e.DailyWage).HasPrecision(18, 2);
        modelBuilder.Entity<Employee>().Property(e => e.EndOfServiceDate).HasColumnType("date");
        modelBuilder.Entity<TempWorkDay>(e =>
        {
            e.Property(x => x.WorkDate).HasColumnType("date");
            e.Property(x => x.Days).HasPrecision(4, 2);
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Payment).WithMany().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<TempWorkerPayment>(e =>
        {
            foreach (var d in new[] { nameof(TempWorkerPayment.PaidDate), nameof(TempWorkerPayment.FromDate), nameof(TempWorkerPayment.ToDate) })
                e.Property(d).HasColumnType("date");
            e.Property(x => x.Days).HasPrecision(6, 2);
            e.Property(x => x.DailyWage).HasPrecision(18, 2);
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.JournalEntry).WithMany().HasForeignKey(x => x.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.CreatedByUser).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(tb => tb.HasTrigger("trg_TempWorkerPayments_PeriodLock"));
        });
        modelBuilder.Entity<FingerprintImport>(e =>
        {
            e.Property(x => x.PeriodFrom).HasColumnType("date");
            e.Property(x => x.PeriodTo).HasColumnType("date");
            e.HasOne(x => x.ImportedByUser).WithMany().HasForeignKey(x => x.ImportedByUserId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ProductionOrderLine>().HasOne(l => l.PackagingLevel).WithMany().HasForeignKey(l => l.PackagingLevelId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ProductionOrderLine>().Property(l => l.Packs).HasPrecision(18, 3);
        modelBuilder.Entity<ItemBatch>().HasOne(b => b.CustomRecipe).WithMany().HasForeignKey(b => b.CustomRecipeId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SalesInvoiceLine>().HasOne(l => l.CustomRecipe).WithMany().HasForeignKey(l => l.CustomRecipeId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<RepLoadOrderLine>().HasOne(l => l.CustomRecipe).WithMany().HasForeignKey(l => l.CustomRecipeId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<DailyProductionTemplate>(e =>
        {
            e.HasIndex(t => t.Name).IsUnique();
            e.HasMany(t => t.Lines).WithOne(l => l.Template).HasForeignKey(l => l.TemplateId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<DailyProductionTemplateLine>(e =>
        {
            e.Property(l => l.Packs).HasPrecision(18, 3);
            e.HasOne(l => l.FinishedItem).WithMany().HasForeignKey(l => l.FinishedItemId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.PackagingLevel).WithMany().HasForeignKey(l => l.PackagingLevelId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.CustomRecipe).WithMany().HasForeignKey(l => l.CustomRecipeId).OnDelete(DeleteBehavior.Restrict);
        });
        // محرك الكلفة (28_costing_purchasing.sql): الحركة المخزنية تأخذ كلفتها من مشغّل
        modelBuilder.Entity<StockTransaction>().ToTable(tb => tb.HasTrigger("trg_StockTransactions_Cost"));
    }
}
