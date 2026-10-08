using ERP.Data.ProjectDb.Entities;
using ERP.Data.Security;
using ERP.Data.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>المرحلة م3: الحمولة الافتراضية وطلب التحميل وتجهيزه، والتسوية اليومية، وحد الدين، وسعر القائمة للخصم.</summary>
[Collection("controls")]
public class RepOperationsTests
{
    private readonly ControlsFixture _f;
    public RepOperationsTests(ControlsFixture f) => _f = f;

    private static async Task ReceiveAsync(ERP.Data.ProjectDb.ProjectDbContext db, int warehouseId, int itemId, int levelId, decimal qty, string batch, int userId)
    {
        var (r, _) = await new WarehouseDocumentService(db).CreateAsync(new StockDocumentRequest(StockDocumentType.Receipt, warehouseId, DateTime.Today,
            new[] { new StockDocumentLineInput(itemId, levelId, qty, NewBatchNumber: batch, NewBatchExpiry: DateTime.Today.AddYears(1)) }, userId));
        Assert.True(r.Success, r.ErrorMessage);
    }

    [Fact]
    public async Task Load_order_moves_stock_only_when_the_store_keeper_prepares_it()
    {
        await using var db = _f.NewDb(_f.AdminId);
        var fg = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.FinishedGoods);
        var van = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.RepVan);
        var w500 = await db.Items.FirstAsync(i => i.ItemCode == "W-500");
        var carton = await db.ItemPackagingLevels.FirstAsync(l => l.ItemId == w500.Id && l.EquivalentBaseUnits == 12);
        await ReceiveAsync(db, fg.Id, w500.Id, carton.Id, 20, "LOAD-T1", _f.AdminId);
        var svc = new RepOperationsService(db);

        // الحمولة الافتراضية تُحفظ وتُستبدل كاملة
        Assert.True((await svc.SaveDefaultLoadAsync(van.OwnerEmployeeId!.Value, new[] { new RepLoadLineInput(w500.Id, carton.Id, 10) }, _f.AdminId)).Success);
        var defaults = await svc.GetDefaultLoadAsync(van.OwnerEmployeeId.Value);
        Assert.Equal(10m, Assert.Single(defaults).QuantityInLevel);

        var vanBefore = await db.StockTransactions.Where(t => t.WarehouseId == van.Id).SumAsync(t => t.QuantityBaseUnits);
        var (created, order) = await svc.CreateLoadOrderAsync(van.Id, fg.Id, DateTime.Today,
            defaults.Select(d => new RepLoadLineInput(d.ItemId, d.PackagingLevelId, d.QuantityInLevel)).ToList(), null, _f.AdminId);
        Assert.True(created.Success, created.ErrorMessage);
        Assert.StartsWith("LO-", order!.OrderNumber);
        // لا حركة قبل التجهيز، ولا طلب ثانٍ للسيارة نفسها
        Assert.Equal(vanBefore, await db.StockTransactions.Where(t => t.WarehouseId == van.Id).SumAsync(t => t.QuantityBaseUnits));
        var (second, _) = await svc.CreateLoadOrderAsync(van.Id, fg.Id, DateTime.Today, new[] { new RepLoadLineInput(w500.Id, carton.Id, 1) }, null, _f.AdminId);
        Assert.False(second.Success);

        // أمين المخزن جهّز 8 كراتين فقط من 10
        var lineId = order.Lines.Single().Id;
        var (prepared, doc) = await svc.PrepareLoadOrderAsync(order.Id, new Dictionary<int, decimal> { [lineId] = 8 }, _f.AdminId);
        Assert.True(prepared.Success, prepared.ErrorMessage);
        Assert.StartsWith("RL-", doc!.DocumentNumber);
        Assert.Equal(vanBefore + 96, await db.StockTransactions.Where(t => t.WarehouseId == van.Id).SumAsync(t => t.QuantityBaseUnits));
        var row = (await svc.GetLoadOrdersAsync(DateTime.Today, DateTime.Today)).Single(o => o.Id == order.Id);
        Assert.Equal(RepLoadOrderStatus.Prepared, row.Status);
        Assert.Equal(doc.DocumentNumber, row.DocumentNumber);
        Assert.False((await svc.CancelLoadOrderAsync(order.Id, "خطأ", _f.AdminId)).Success);
        Assert.True(await db.AuditLogs.AnyAsync(a => a.TableName == "RepLoadOrders" && a.RecordId == order.Id.ToString()));
    }

    [Fact]
    public async Task Settlement_returns_free_goods_invoices_rest_and_keeps_the_shortfall_in_the_wallet()
    {
        await using var db = _f.NewDb(_f.AdminId);
        var fg = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.FinishedGoods);
        var van = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.RepVan);
        var repId = van.OwnerEmployeeId!.Value;
        var w500 = await db.Items.FirstAsync(i => i.ItemCode == "W-500");
        var carton = await db.ItemPackagingLevels.FirstAsync(l => l.ItemId == w500.Id && l.EquivalentBaseUnits == 12);
        var piece = await db.ItemPackagingLevels.FirstAsync(l => l.ItemId == w500.Id && l.EquivalentBaseUnits == 1);
        var customer = await db.Customers.FirstAsync(c => c.CustomerType == CustomerType.Direct);
        await ReceiveAsync(db, fg.Id, w500.Id, carton.Id, 10, "SETTLE-T1", _f.AdminId);
        Assert.True((await new RepsService(db).LoadVanAsync(van.Id, fg.Id, new[] { new StockLineInput(w500.Id, null, 60) }, _f.AdminId)).Success);

        var vanStock = await db.StockTransactions.Where(t => t.WarehouseId == van.Id).GroupBy(t => t.ItemId)
            .Select(g => new { g.Key, Qty = g.Sum(t => t.QuantityBaseUnits) }).Where(x => x.Qty > 0).ToListAsync();
        var w500InVan = vanStock.Single(x => x.Key == w500.Id).Qty;
        var reps = new RepsService(db);
        var walletBefore = await reps.GetWalletBalanceAsync(repId);
        var cost = (await db.Items.AsNoTracking().FirstAsync(i => i.Id == w500.Id)).CostPrice ?? 0;

        // مرتجع كارتون سليم + 2 قطعة تالفة، ومجاني كارتون لعميل، والباقي يُفوتر نقدًا
        var sold = w500InVan - 12 - 2 - 12;
        var expectedSales = Math.Round(sold * 250m, 2);
        var expected = walletBefore + expectedSales - 1000;
        var request = new RepSettlementRequest(van.Id, fg.Id, DateTime.Today,
            new[] { new StockDocumentLineInput(w500.Id, carton.Id, 1), new StockDocumentLineInput(w500.Id, piece.Id, 2, IsDamaged: true) },
            new[] { new RepFreeLineInput(w500.Id, carton.Id, 1, customer.Id, "هدية افتتاح محل") },
            new[] { new RepExpenseInput(1000, "وقود") },
            expected - 500, _f.AdminId, InvoiceRemainingToCustomerId: customer.Id);
        var (result, settlement) = await new RepOperationsService(db).SettleAsync(request);
        Assert.True(result.Success, result.ErrorMessage);

        Assert.Equal(0m, await db.StockTransactions.Where(t => t.WarehouseId == van.Id && t.ItemId == w500.Id).SumAsync(t => t.QuantityBaseUnits));
        Assert.Equal(14m, settlement!.ReturnedPieces);
        Assert.Equal(12m, settlement.FreePieces);
        Assert.Equal(Math.Round(12 * cost, 2), settlement.FreeCost);
        Assert.Equal(1000m, settlement.FieldExpenses);
        Assert.Equal(expected, settlement.ExpectedCash);
        Assert.Equal(500m, settlement.Difference);
        Assert.Equal(500m, await reps.GetWalletBalanceAsync(repId));         // العجز يبقى في ذمة المندوب
        var invoice = await db.SalesInvoices.AsNoTracking().SingleAsync(i => i.Id == settlement.AutoInvoiceId);
        Assert.Equal(DocumentStatus.Posted, invoice.Status);
        Assert.Equal(repId, invoice.SalesRepEmployeeId);
        Assert.Equal(expectedSales, invoice.TotalAmount);
        Assert.True(await db.StockTransactions.AnyAsync(t => t.TransactionType == StockTransactionType.RepFreeSale && t.ReferenceTable == "RepSettlements" && t.ReferenceId == settlement.Id));
        var row = Assert.Single(await new RepOperationsService(db).GetSettlementsAsync(DateTime.Today, DateTime.Today), s => s.Id == settlement.Id);
        Assert.Equal(invoice.InvoiceNumber, row.InvoiceNumber);

        // المستلم أكثر من المتوقع مرفوض ولا يترك أثرًا
        var count = await db.RepSettlements.CountAsync();
        var (tooMuch, _) = await new RepOperationsService(db).SettleAsync(new RepSettlementRequest(van.Id, fg.Id, DateTime.Today,
            Array.Empty<StockDocumentLineInput>(), Array.Empty<RepFreeLineInput>(), Array.Empty<RepExpenseInput>(), 1_000_000, _f.AdminId));
        Assert.False(tooMuch.Success);
        db.ChangeTracker.Clear();
        Assert.Equal(count, await db.RepSettlements.CountAsync());
        Assert.Equal(500m, await reps.GetWalletBalanceAsync(repId));

        // عنده نقد في المحفظة لكنه سُوّي اليوم: ليس متأخرًا؛ بعد يومين يصبح متأخرًا
        var svc = new RepOperationsService(db);
        Assert.DoesNotContain(await svc.GetOverdueAsync(DateTime.Today), o => o.RepEmployeeId == repId);
        // النقد قد يبقى مع المندوب يومين (إعدادات تطبيق المندوبين)، والتنبيه من اليوم الثالث
        Assert.DoesNotContain(await svc.GetOverdueAsync(DateTime.Today.AddDays(2)), o => o.RepEmployeeId == repId);
        Assert.Contains(await svc.GetOverdueAsync(DateTime.Today.AddDays(3)), o => o.RepEmployeeId == repId && o.WalletBalance == 500m);

        // تسليم الباقي يصفّر المحفظة
        Assert.True((await reps.RecordCashHandoverAsync(repId, 500, DateTime.Today, _f.AdminId)).Success);
    }

    [Fact]
    public async Task Credit_limit_blocks_credit_sales_unless_the_user_may_override_and_list_price_is_kept()
    {
        await using var db = _f.NewDb(_f.AdminId);
        var fg = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.FinishedGoods);
        var w500 = await db.Items.FirstAsync(i => i.ItemCode == "W-500");
        var carton = await db.ItemPackagingLevels.FirstAsync(l => l.ItemId == w500.Id && l.EquivalentBaseUnits == 12);
        await ReceiveAsync(db, fg.Id, w500.Id, carton.Id, 5, "CREDIT-T1", _f.AdminId);
        var customer = new Customer { Name = "عميل بحد دين", CustomerType = CustomerType.Direct, CreditLimit = 1000 };
        db.Customers.Add(customer);
        var role = await db.Roles.FirstAsync(r => r.Name == "محاسب");
        var clerk = new User { Username = "acc_credit", PasswordHash = PasswordHasher.Hash("x"), RoleId = role.Id };
        db.Users.Add(clerk);
        await db.SaveChangesAsync();

        var sales = new SalesService(db);
        async Task<(FinanceOperationResult r, int id)> SellOnCredit(int userId)
        {
            var (_, id) = await sales.CreateInvoiceAsync(new SalesInvoiceHeaderInput(customer.Id, fg.Id, DateTime.Today, InvoicePaymentMethod.Credit), userId);
            Assert.True((await sales.AddLineAsync(id!.Value, new SalesInvoiceLineInput(w500.Id, carton.Id, 1, UnitPrice: 2400), userId)).Success);
            var (posted, _) = await sales.PostInvoiceAsync(id.Value, userId);
            return (posted, id.Value);
        }

        var (blocked, blockedId) = await SellOnCredit(clerk.Id);
        Assert.False(blocked.Success);
        Assert.Contains("حد دينه", blocked.ErrorMessage);
        Assert.Equal(DocumentStatus.Draft, (await db.SalesInvoices.AsNoTracking().FirstAsync(i => i.Id == blockedId)).Status);

        // المدير يملك "تجاوز حد الدين"
        var (approved, approvedId) = await SellOnCredit(_f.AdminId);
        Assert.True(approved.Success, approved.ErrorMessage);

        // سعر القائمة محفوظ في السطر (لحساب الخصم في الحسابات الختامية): 250 × 12
        var line = await db.SalesInvoiceLines.AsNoTracking().FirstAsync(l => l.SalesInvoiceId == approvedId);
        Assert.Equal(3000m, line.ListUnitPrice);
        Assert.Equal(2400m, line.UnitPrice);
    }
}
