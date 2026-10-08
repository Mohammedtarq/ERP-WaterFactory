using ERP.Data.ProjectDb.Entities;
using ERP.Data.Security;
using ERP.Data.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>المرحلة م2: إنتاج اليوم بكلفة فعلية، والبيع بانتظار الإنتاج وتسويته، والجرد السريع، وتقارير الإنتاج والخسائر.</summary>
[Collection("controls")]
public class ProductionStockTests
{
    private readonly ControlsFixture _f;
    public ProductionStockTests(ControlsFixture f) => _f = f;

    private static async Task SetCostsAsync(ERP.Data.ProjectDb.ProjectDbContext db)
    {
        foreach (var (code, cost) in new[] { ("RM-PRE", 30m), ("RM-CAP", 5m), ("RM-LBL", 2m) })
            await db.Items.Where(i => i.ItemCode == code).ExecuteUpdateAsync(u => u.SetProperty(i => i.CostPrice, cost));
    }

    [Fact]
    public async Task Daily_production_consumes_recipe_at_average_cost_and_settles_pending_sales()
    {
        await using var db = _f.NewDb(_f.AdminId);
        await SetCostsAsync(db);
        var fg = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.FinishedGoods);
        var w500 = await db.Items.FirstAsync(i => i.ItemCode == "W-500");
        var carton = await db.ItemPackagingLevels.FirstAsync(l => l.ItemId == w500.Id && l.EquivalentBaseUnits == 12);
        var piece = await db.ItemPackagingLevels.FirstAsync(l => l.ItemId == w500.Id && l.EquivalentBaseUnits == 1);
        var customer = await db.Customers.FirstAsync(c => c.CustomerType == CustomerType.Direct);

        // بيع أكثر من الرصيد بصلاحية "البيع بانتظار الإنتاج": يخرج المتاح والفرق عجز مسجّل
        var available = await db.StockTransactions.Where(t => t.ItemId == w500.Id && t.WarehouseId == fg.Id).SumAsync(t => t.QuantityBaseUnits);
        var sales = new SalesService(db);
        var (created, invId) = await sales.CreateInvoiceAsync(new SalesInvoiceHeaderInput(customer.Id, fg.Id, DateTime.Today, InvoicePaymentMethod.Cash), _f.AdminId);
        Assert.True(created.Success, created.ErrorMessage);
        await sales.AddLineAsync(invId!.Value, new SalesInvoiceLineInput(w500.Id, piece.Id, available + 50), _f.AdminId);
        var (posted, _) = await sales.PostInvoiceAsync(invId.Value, _f.AdminId);
        Assert.True(posted.Success, posted.ErrorMessage);
        var shortage = await db.PendingProductionShortages.AsNoTracking().SingleAsync(s => s.SalesInvoiceId == invId);
        Assert.Equal(50m, shortage.Quantity);
        Assert.Equal(-50m, await db.StockTransactions.Where(t => t.ItemId == w500.Id && t.WarehouseId == fg.Id).SumAsync(t => t.QuantityBaseUnits));

        // إنتاج اليوم: 10 كراتين = 120 قطعة؛ المواد من الوصفة (امبولة + سدادة + ليبلان أمامي وخلفي) بكلفة 39 للقطعة
        var rawBefore = await db.StockTransactions.Where(t => t.Item.ItemCode == "RM-PRE").SumAsync(t => t.QuantityBaseUnits);
        var (r, orderId, lines) = await new DailyProductionService(db).RecordAsync(DateTime.Today,
            new[] { new DailyProductionLineInput(w500.Id, carton.Id, 10) }, _f.AdminId);
        Assert.True(r.Success, r.ErrorMessage);
        var line = Assert.Single(lines);
        Assert.Equal(120m, line.Pieces);
        Assert.Equal(120m * 39m, line.MaterialCost);
        Assert.Equal(39m, line.UnitCost);
        Assert.Equal(50m, line.SettledShortage);
        Assert.Equal(rawBefore - 120, await db.StockTransactions.Where(t => t.Item.ItemCode == "RM-PRE").SumAsync(t => t.QuantityBaseUnits));
        Assert.Equal(ProductionOrderStatus.Completed, (await db.ProductionOrders.FindAsync(orderId))!.Status);

        // العجز سُوّي، ورصيد كل تشغيلة صحيح: الجديدة 70، وبلا تشغيلة صفر
        Assert.Equal(50m, (await db.PendingProductionShortages.AsNoTracking().SingleAsync(s => s.Id == shortage.Id)).SettledQuantity);
        Assert.Empty(await new PendingProductionService(db).OpenAsync());
        var batches = await db.StockTransactions.Where(t => t.ItemId == w500.Id && t.WarehouseId == fg.Id)
            .GroupBy(t => t.BatchId).Select(g => new { g.Key, Qty = g.Sum(t => t.QuantityBaseUnits) }).ToListAsync();
        Assert.Equal(70m, batches.Sum(b => b.Qty));
        Assert.Equal(0m, batches.Where(b => b.Key == null).Sum(b => b.Qty));
        Assert.DoesNotContain(batches, b => b.Qty < 0);

        // متوسط كلفة المنتج التام = كلفة الإنتاج الفعلية (لا رصيد مسعَّر قبله)
        Assert.Equal(39m, (await db.Items.AsNoTracking().FirstAsync(i => i.Id == w500.Id)).CostPrice);

        // تقرير الإنتاج الشهري يظهر اليوم بالكراتين والكلفة
        var report = await new ProductionStockReports(db).MonthlyProductionAsync(DateTime.Today.Year, DateTime.Today.Month);
        Assert.Contains(report, x => x.Date == DateTime.Today && x.ItemName == w500.ItemName && x.Packs >= 10 && x.PackLabel == "كارتون" && x.Cost >= 4440);
    }

    [Fact]
    public async Task Overselling_without_the_permission_is_rejected()
    {
        await using var db = _f.NewDb(_f.AdminId);
        var role = await db.Roles.FirstAsync(r => r.Name == "محاسب");
        var user = new User { Username = "acc_nopend", PasswordHash = PasswordHasher.Hash("x"), RoleId = role.Id };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var fg = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.FinishedGoods);
        var w1500 = await db.Items.FirstAsync(i => i.ItemCode == "W-1500");
        var piece = await db.ItemPackagingLevels.FirstAsync(l => l.ItemId == w1500.Id && l.EquivalentBaseUnits == 1);
        var customer = await db.Customers.FirstAsync(c => c.CustomerType == CustomerType.Direct);
        var available = await db.StockTransactions.Where(t => t.ItemId == w1500.Id && t.WarehouseId == fg.Id).SumAsync(t => t.QuantityBaseUnits);
        var sales = new SalesService(db);
        var (_, id) = await sales.CreateInvoiceAsync(new SalesInvoiceHeaderInput(customer.Id, fg.Id, DateTime.Today, InvoicePaymentMethod.Cash), user.Id);
        await sales.AddLineAsync(id!.Value, new SalesInvoiceLineInput(w1500.Id, piece.Id, available + 1), user.Id);
        var (posted, _) = await sales.PostInvoiceAsync(id.Value, user.Id);
        Assert.False(posted.Success);
        Assert.Contains("الرصيد غير كافٍ", posted.ErrorMessage);
        Assert.False(await db.PendingProductionShortages.AnyAsync(s => s.SalesInvoiceId == id));
    }

    [Fact]
    public async Task Production_is_refused_when_materials_do_not_cover_the_day()
    {
        await using var db = _f.NewDb(_f.AdminId);
        var w500 = await db.Items.FirstAsync(i => i.ItemCode == "W-500");
        var carton = await db.ItemPackagingLevels.FirstAsync(l => l.ItemId == w500.Id && l.EquivalentBaseUnits == 12);
        var before = await db.ProductionOrders.CountAsync();
        var (r, _, _) = await new DailyProductionService(db).RecordAsync(DateTime.Today, new[] { new DailyProductionLineInput(w500.Id, carton.Id, 1_000_000) }, _f.AdminId);
        Assert.False(r.Success);
        Assert.Contains("لا تكفي", r.ErrorMessage);
        Assert.Equal(before, await db.ProductionOrders.CountAsync());
    }

    [Fact]
    public async Task Stocktake_counts_big_units_and_records_variance_at_cost_separately_from_damage()
    {
        await using var db = _f.NewDb(_f.AdminId);
        var fg = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.FinishedGoods);
        var w1500 = await db.Items.FirstAsync(i => i.ItemCode == "W-1500");
        await db.Items.Where(i => i.Id == w1500.Id).ExecuteUpdateAsync(u => u.SetProperty(i => i.CostPrice, 300m));
        var svc = new StocktakeService(db);
        var row = (await svc.SheetAsync(fg.Id)).Single(x => x.ItemId == w1500.Id);
        Assert.Equal(("شرنك", 6m), (row.Units[0].Name, row.Units[0].Pieces));    // الأكبر أولًا
        var system = row.SystemQuantity;

        // عُدّ: 100 شرنك + 3 قطع = 603 قطعة
        var counted = new StocktakeLineInput(w1500.Id, new[] { ("شرنك", 6m, 100m), ("قطعة", 1m, 3m) });
        Assert.Equal(603m, counted.Pieces);
        Assert.Equal("100 شرنك + 3 قطعة", counted.Detail);
        var (r, summary) = await svc.PostAsync(fg.Id, DateTime.Today, "جرد أسبوعي", new[] { counted }, _f.AdminId);
        Assert.True(r.Success, r.ErrorMessage);
        var variance = 603m - system;
        Assert.Equal(1, summary!.ItemsWithVariance);
        if (variance < 0) Assert.Equal(-variance * 300m, summary.ShortageValue);
        else Assert.Equal(variance * 300m, summary.SurplusValue);
        Assert.Equal(603m, await db.StockTransactions.Where(t => t.ItemId == w1500.Id && t.WarehouseId == fg.Id).SumAsync(t => t.QuantityBaseUnits));
        Assert.All(await db.StockTransactions.Where(t => t.ReferenceTable == "StockCounts" && t.ReferenceId == summary.CountId).ToListAsync(),
                   t => Assert.Equal(StockTransactionType.StocktakeVariance, t.TransactionType));
        var line = await db.StockCountLines.AsNoTracking().SingleAsync(l => l.StockCountId == summary.CountId);
        Assert.Equal("100 شرنك + 3 قطعة", line.CountDetail);

        // تقرير الخسائر: فرق الجرد سطر مستقل عن التلف
        var losses = await new ProductionStockReports(db).MonthlyLossesAsync(DateTime.Today.Year, DateTime.Today.Month);
        if (variance < 0) Assert.Contains(losses, l => l.Kind == "نقص جرد" && l.ItemName == w1500.ItemName && l.Value == -variance * 300m);
        Assert.DoesNotContain(losses, l => l.Kind == "تلف" && l.ItemName == w1500.ItemName);
    }
}
