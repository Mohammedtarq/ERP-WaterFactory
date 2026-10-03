using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Security;
using ERP.Data.Services;
using ERP.Data.Setup;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>مشروع كامل مستقل (ببيانات تجريبية): قفل الفترة لا يجوز أن يمسّ قواعد الاختبارات الأخرى.</summary>
public class ControlsFixture : IAsyncLifetime
{
    private static string Master => Environment.GetEnvironmentVariable("ERP_TEST_MASTER_CONNECTION")
        ?? throw new InvalidOperationException("ERP_TEST_MASTER_CONNECTION غير معيّن");

    public readonly string Suffix = Guid.NewGuid().ToString("N")[..8];
    public string ControlCs => new SqlConnectionStringBuilder(Master) { InitialCatalog = $"ERP_Ctl_C{Suffix}" }.ConnectionString;
    public string ProjectCs = "";
    public int AdminId;

    public ProjectDbContext NewDb(int? auditUser = null) =>
        new(new DbContextOptionsBuilder<ProjectDbContext>().UseSqlServer(ProjectCs).Options) { AuditUserId = auditUser };

    public async Task InitializeAsync()
    {
        var install = await new ProvisioningService().InstallAsync(new InstallRequest(ControlCs, "ضوابط", $"ERP_Controls_{Suffix}",
                                                                                      "المدير", "boss", "Boss@2026", DemoData: true));
        Assert.True(install.Success, install.ErrorMessage);
        ProjectCs = install.ProjectConnectionString;
        await using var db = NewDb();
        AdminId = await db.Users.Where(u => u.Username == "boss").Select(u => u.Id).SingleAsync();
    }

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var conn = new SqlConnection(Master);
        await conn.OpenAsync();
        foreach (var db in new[] { $"ERP_Controls_{Suffix}", $"ERP_Ctl_C{Suffix}" })
        {
            await using var cmd = new SqlCommand($"IF DB_ID('{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END", conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>فاتورة نقدية مرحّلة لعميل مباشر من مخزن المنتج التام.</summary>
    public async Task<int> PostedCashInvoiceAsync(DateTime date, decimal qty = 24)
    {
        await using var db = NewDb(AdminId);
        var customer = await db.Customers.FirstAsync(c => c.CustomerType == CustomerType.Direct);
        var fg = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.FinishedGoods);
        var item = await db.Items.FirstAsync(i => i.ItemCode == "W-500");
        var piece = await db.ItemPackagingLevels.FirstAsync(l => l.ItemId == item.Id && l.EquivalentBaseUnits == 1);
        var sales = new SalesService(db);
        var (created, id) = await sales.CreateInvoiceAsync(new SalesInvoiceHeaderInput(customer.Id, fg.Id, date, InvoicePaymentMethod.Cash), AdminId);
        Assert.True(created.Success, created.ErrorMessage);
        var line = await sales.AddLineAsync(id!.Value, new SalesInvoiceLineInput(item.Id, piece.Id, qty), AdminId);
        Assert.True(line.Success, line.ErrorMessage);
        var (posted, _) = await sales.PostInvoiceAsync(id.Value, AdminId);
        Assert.True(posted.Success, posted.ErrorMessage);
        return id.Value;
    }
}

[CollectionDefinition("controls")] public class ControlsCollection : ICollectionFixture<ControlsFixture> { }

[Collection("controls")]
public class ControlsTests
{
    private readonly ControlsFixture _f;
    public ControlsTests(ControlsFixture f) => _f = f;

    [Fact]
    public async Task Audit_log_records_insert_and_update_with_before_and_after()
    {
        int id;
        await using (var db = _f.NewDb(_f.AdminId))
        {
            var c = new Customer { Name = "عميل السجل", CustomerType = CustomerType.Direct, Province = "البصرة" };
            db.Customers.Add(c);
            await db.SaveChangesAsync();
            id = c.Id;
            c.Phone = "07800000000";
            await db.SaveChangesAsync();
        }
        await using var check = _f.NewDb();
        var rows = await check.AuditLogs.Where(a => a.TableName == "Customers" && a.RecordId == id.ToString()).OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(new[] { "Insert", "Update" }, rows.Select(r => r.Action));
        Assert.All(rows, r => Assert.Equal(_f.AdminId, r.UserId));
        Assert.Equal("عميل السجل", rows[0].Summary);
        Assert.Contains("07800000000", rows[1].Changes);
        Assert.Contains("Phone", rows[1].Changes);
    }

    [Fact]
    public async Task Posted_invoice_is_voided_with_reversals_not_deleted()
    {
        var invoiceId = await _f.PostedCashInvoiceAsync(DateTime.Today);
        await using var db = _f.NewDb(_f.AdminId);
        var inv = await db.SalesInvoices.AsNoTracking().FirstAsync(i => i.Id == invoiceId);
        var stockBefore = await db.StockTransactions.Where(t => t.ReferenceTable == "SalesInvoices" && t.ReferenceId == invoiceId).SumAsync(t => t.QuantityBaseUnits);
        Assert.Equal(-24, stockBefore);

        var sales = new SalesService(db);
        Assert.False((await sales.VoidInvoiceAsync(invoiceId, " ", _f.AdminId)).Success);   // السبب إلزامي
        var r = await sales.VoidInvoiceAsync(invoiceId, "خطأ في العميل", _f.AdminId);
        Assert.True(r.Success, r.ErrorMessage);

        var after = await db.SalesInvoices.AsNoTracking().FirstAsync(i => i.Id == invoiceId);
        Assert.Equal(DocumentStatus.Voided, after.Status);
        Assert.Equal("خطأ في العميل", after.VoidReason);
        // المخزون عاد، والقيد معكوس بالكامل، وحركة الصندوق ملغاة
        Assert.Equal(0, await db.StockTransactions.Where(t => t.ReferenceTable == "SalesInvoices" && t.ReferenceId == invoiceId).SumAsync(t => t.QuantityBaseUnits));
        var lines = await db.JournalEntryLines.Where(l => l.JournalEntry.SourceTable == "SalesInvoices" && l.JournalEntry.SourceId == invoiceId).ToListAsync();
        Assert.Equal(2, lines.Select(l => l.JournalEntryId).Distinct().Count());
        foreach (var acc in lines.GroupBy(l => l.AccountId)) Assert.Equal(0, acc.Sum(l => l.Debit - l.Credit));
        Assert.All(await db.CashBoxTransactions.Where(t => t.ReferenceTable == "SalesInvoices" && t.ReferenceId == invoiceId).ToListAsync(), t => Assert.True(t.IsVoided));
        // لا تظهر في كشف العميل، وإلغاء ثانٍ مرفوض
        Assert.DoesNotContain(await sales.GetCustomerStatementAsync(inv.CustomerId), s => s.DocNumber == inv.InvoiceNumber);
        Assert.False((await sales.VoidInvoiceAsync(invoiceId, "مرة ثانية", _f.AdminId)).Success);
        Assert.True(await db.AuditLogs.AnyAsync(a => a.TableName == "SalesInvoices" && a.Action == "Void" && a.RecordId == invoiceId.ToString()));
    }

    [Fact]
    public async Task Voucher_is_voided_with_reverse_entry_and_cash_box_void()
    {
        await using var db = _f.NewDb(_f.AdminId);
        var customer = await db.Customers.FirstAsync(c => c.CustomerType == CustomerType.Agent);
        var fin = new FinanceService(db);
        var created = await fin.CreateVoucherAsync(VoucherType.Receipt, VoucherPartyType.Customer, customer.Id, 50_000, PaymentMethod.Cash,
                                                   DateTime.Today, "CashReceiptVoucher", _f.AdminId, "دفعة");
        Assert.True(created.Success, created.ErrorMessage);
        var v = await db.Vouchers.AsNoTracking().OrderByDescending(x => x.Id).FirstAsync();

        var r = await fin.VoidVoucherAsync(v.Id, "سند مكرر", _f.AdminId);
        Assert.True(r.Success, r.ErrorMessage);
        var after = await db.Vouchers.AsNoTracking().FirstAsync(x => x.Id == v.Id);
        Assert.True(after.IsVoided);
        Assert.Equal("سند مكرر", after.VoidReason);
        Assert.All(await db.CashBoxTransactions.Where(t => t.ReferenceTable == "Vouchers" && t.ReferenceId == v.Id).ToListAsync(), t => Assert.True(t.IsVoided));
        var rev = await db.JournalEntries.Include(j => j.Lines).FirstAsync(j => j.SourceTable == "Vouchers" && j.SourceId == v.Id);
        var original = await db.JournalEntryLines.Where(l => l.JournalEntryId == v.JournalEntryId).ToListAsync();
        Assert.Equal(original.Sum(l => l.Debit), rev.Lines.Sum(l => l.Credit));
        Assert.DoesNotContain(await new SalesService(db).GetCustomerStatementAsync(customer.Id), s => s.DocNumber == v.VoucherNumber);
        Assert.True(await db.AuditLogs.AnyAsync(a => a.TableName == "Vouchers" && a.Action == "Void" && a.RecordId == v.Id.ToString()));
    }

    [Fact]
    public async Task Closed_month_blocks_any_change_until_the_manager_reopens_it()
    {
        var lastMonth = DateTime.Today.AddDays(1 - DateTime.Today.Day).AddMonths(-1);
        var oldInvoice = await _f.PostedCashInvoiceAsync(lastMonth.AddDays(3), qty: 12);
        await using var db = _f.NewDb(_f.AdminId);
        var locks = new PeriodLockService(db);
        var fin = new FinanceService(db);
        var sales = new SalesService(db);
        var customer = await db.Customers.FirstAsync(c => c.CustomerType == CustomerType.Direct);

        Assert.False((await locks.CloseMonthAsync(DateTime.Today.Year, DateTime.Today.Month, _f.AdminId)).Success);  // الشهر الجاري لم ينتهِ
        var closed = await locks.CloseMonthAsync(lastMonth.Year, lastMonth.Month, _f.AdminId);
        Assert.True(closed.Success, closed.ErrorMessage);
        Assert.Equal(lastMonth.AddMonths(1).AddDays(-1), await locks.GetLockedThroughAsync());
        try
        {
            // سند بتاريخ داخل الشهر المقفل: مرفوض برسالة عربية واضحة
            var blocked = await fin.CreateVoucherAsync(VoucherType.Receipt, VoucherPartyType.Customer, customer.Id, 1_000, PaymentMethod.Cash,
                                                       lastMonth.AddDays(5), "CashReceiptVoucher", _f.AdminId);
            Assert.False(blocked.Success);
            Assert.Contains("مقفلة", blocked.ErrorMessage);
        }
        catch (DbUpdateException ex)
        {
            Assert.Contains("مقفلة", ex.InnerException!.Message);
        }
        await using (var fresh = _f.NewDb(_f.AdminId))
        {
            // إلغاء فاتورة الشهر المقفل مرفوض، وسند بتاريخ اليوم مسموح
            var voidBlocked = await new SalesService(fresh).VoidInvoiceAsync(oldInvoice, "تصحيح", _f.AdminId);
            Assert.False(voidBlocked.Success);
            Assert.Contains("مقفلة", voidBlocked.ErrorMessage);
            var today = await new FinanceService(fresh).CreateVoucherAsync(VoucherType.Receipt, VoucherPartyType.Customer, customer.Id, 1_000,
                                                                          PaymentMethod.Cash, DateTime.Today, "CashReceiptVoucher", _f.AdminId);
            Assert.True(today.Success, today.ErrorMessage);
        }

        // الفتح يحتاج سببًا، ثم يصبح الإلغاء ممكنًا
        await using var db2 = _f.NewDb(_f.AdminId);
        var locks2 = new PeriodLockService(db2);
        Assert.False((await locks2.ReopenFromMonthAsync(lastMonth.Year, lastMonth.Month, "", _f.AdminId)).Success);
        var reopened = await locks2.ReopenFromMonthAsync(lastMonth.Year, lastMonth.Month, "تصحيح فاتورة بعد مراجعة المحاسب", _f.AdminId);
        Assert.True(reopened.Success, reopened.ErrorMessage);
        Assert.True((await new SalesService(db2).VoidInvoiceAsync(oldInvoice, "تصحيح", _f.AdminId)).Success);
        var history = await locks2.HistoryAsync();
        Assert.Equal(new[] { "فتح", "إغلاق" }, history.Take(2).Select(h => h.Action));
    }

    [Fact]
    public async Task Role_payment_ceiling_requires_approval_permission_above_it()
    {
        await using var db = _f.NewDb(_f.AdminId);
        var role = await db.Roles.FirstAsync(r => r.Name == "محاسب");
        role.MaxPaymentAmount = 100_000;
        var user = new User { Username = "acc_ceiling", PasswordHash = PasswordHasher.Hash("x"), RoleId = role.Id };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var supplier = await db.Suppliers.FirstAsync();
        var fin = new FinanceService(db);

        var above = await fin.CreateVoucherAsync(VoucherType.Payment, VoucherPartyType.Supplier, supplier.Id, 150_000, PaymentMethod.Bank,
                                                 DateTime.Today, "SupplierPaymentVoucher", user.Id);
        Assert.False(above.Success);
        Assert.Contains("سقف", above.ErrorMessage);
        var within = await fin.CreateVoucherAsync(VoucherType.Payment, VoucherPartyType.Supplier, supplier.Id, 90_000, PaymentMethod.Bank,
                                                  DateTime.Today, "SupplierPaymentVoucher", user.Id);
        Assert.True(within.Success, within.ErrorMessage);
        // المدير يملك صلاحية الاعتماد فوق السقف
        Assert.Null(await ApprovalLimits.CheckPaymentAsync(db, _f.AdminId, 10_000_000));
    }

    [Fact]
    public async Task Preset_roles_and_special_permissions_are_seeded()
    {
        await using var db = _f.NewDb();
        var admin = await db.Roles.Include(r => r.Permissions).FirstAsync(r => r.Name == "مدير عام");
        Assert.All(SpecialPermission.All, sp => Assert.Contains(admin.Permissions, p => p.ModuleCode == sp.Code && p.CanView));
        foreach (var name in new[] { "محاسب", "أمين مخزن", "أمين صندوق", "شريك", "مراجع", "مسؤول الموارد البشرية" })
            Assert.True(await db.Roles.AnyAsync(r => r.Name == name), name);
        var auditor = await db.Roles.Include(r => r.Permissions).FirstAsync(r => r.Name == "مراجع");
        Assert.Contains(auditor.Permissions, p => p.ModuleCode == SpecialPermission.AuditLog && p.CanView);
        Assert.DoesNotContain(auditor.Permissions, p => p.CanAdd || p.CanEdit || p.CanDelete || p.CanPost);
        var accountant = await db.Roles.Include(r => r.Permissions).FirstAsync(r => r.Name == "محاسب");
        Assert.DoesNotContain(accountant.Permissions, p => p.ModuleCode == SpecialPermission.CostAndProfit && p.CanView);
        Assert.False(await SpecialPermission.HasAsync(db, _f.AdminId + 100000, SpecialPermission.CostAndProfit));
        Assert.True(await SpecialPermission.HasAsync(db, _f.AdminId, SpecialPermission.PeriodClose));
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_temporarily()
    {
        await using (var db = _f.NewDb(_f.AdminId))
        {
            var role = await db.Roles.FirstAsync(r => r.Name == "أمين صندوق");
            db.Users.Add(new User { Username = "locky", PasswordHash = PasswordHasher.Hash("Right@123"), RoleId = role.Id });
            await db.SaveChangesAsync();
        }
        await using (var cdb = new ERP.Data.ControlDb.ControlDbContext(new DbContextOptionsBuilder<ERP.Data.ControlDb.ControlDbContext>().UseSqlServer(_f.ControlCs).Options))
        {
            cdb.GlobalUsers.Add(new ERP.Data.ControlDb.Entities.GlobalUser { Username = "locky", FullName = "مقفول", PasswordHash = PasswordHasher.Hash("Right@123") });
            await cdb.SaveChangesAsync();
        }
        var auth = new AuthService(_f.ControlCs);
        for (var i = 0; i < AuthService.MaxFailedLogins - 1; i++)
            Assert.Contains("غير صحيحة", (await auth.LoginAsync("locky", "wrong")).ErrorMessage);
        var locked = await auth.LoginAsync("locky", "wrong");
        Assert.Contains("قُفل", locked.ErrorMessage);
        var stillLocked = await auth.LoginAsync("locky", "Right@123");
        Assert.False(stillLocked.Success);
        Assert.Contains("مقفل مؤقتًا", stillLocked.ErrorMessage);

        // المدير يفتحه بتعيين كلمة مرور جديدة (يصفّر القفل)
        await using (var cdb = new ERP.Data.ControlDb.ControlDbContext(new DbContextOptionsBuilder<ERP.Data.ControlDb.ControlDbContext>().UseSqlServer(_f.ControlCs).Options))
        {
            var g = await cdb.GlobalUsers.FirstAsync(u => u.Username == "locky");
            g.LockedUntilUtc = null;
            g.FailedLoginCount = 0;
            await cdb.SaveChangesAsync();
        }
        var ok = await auth.LoginAsync("locky", "Right@123");
        Assert.Contains("مشروع", ok.ErrorMessage ?? "مشروع");   // صحيح كلمة المرور؛ لا مشروع مرتبط بهذا الحساب التجريبي
        Assert.DoesNotContain("مقفل", ok.ErrorMessage ?? "");
    }
}
