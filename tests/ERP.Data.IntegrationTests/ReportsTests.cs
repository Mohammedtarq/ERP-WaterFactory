using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>
/// تقارير المبيعات والإنتاج والكلفة والربح: بالاسم الموحد لكل منتج، والمباع والمرتجع والمجاني والصافي، والإنتاج يوميًا/أسبوعيًا
/// (من السبت)/شهريًا بالكلفة، والربح بكلفة الصرف الفعلية بعد طرح المرتجع، ولمنتج واحد أو للكل.
/// </summary>
[Collection("controls")]
public class ReportsTests
{
    private readonly ControlsFixture _f;
    public ReportsTests(ControlsFixture f) => _f = f;

    [Fact]
    public void Week_starts_on_saturday()
    {
        Assert.Equal(new DateTime(2026, 10, 10), ReportsService.WeekStart(new DateTime(2026, 10, 10)));   // السبت
        Assert.Equal(new DateTime(2026, 10, 10), ReportsService.WeekStart(new DateTime(2026, 10, 16)));   // الجمعة
        Assert.Equal(new DateTime(2026, 10, 17), ReportsService.WeekStart(new DateTime(2026, 10, 17)));
        Assert.StartsWith("أسبوع 2026/10/10 – 10/16", ReportsService.PeriodOf(new DateTime(2026, 10, 13), ReportPeriod.Week).Label);
    }

    [Fact]
    public async Task Sales_returns_free_production_and_profit_per_product_by_unified_name()
    {
        await using var db = _f.NewDb(_f.AdminId);
        var fg = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.FinishedGoods);
        var water = new Item { ItemCode = "RPT-330", ItemName = "ماء تقرير 330 مل", SalePrice = 250, CostPrice = 100 };
        var other = new Item { ItemCode = "RPT-500", ItemName = "ماء تقرير آخر", SalePrice = 300, CostPrice = 120, SizeMl = 500 };
        var customer = new Customer { Name = "زبون التقارير" };
        db.AddRange(water, other, customer);
        await db.SaveChangesAsync();
        var shrink = new ItemPackagingLevel { ItemId = water.Id, LevelName = "شرنك", EquivalentBaseUnits = 20 };
        var carton = new ItemPackagingLevel { ItemId = other.Id, LevelName = "كارتون", EquivalentBaseUnits = 12 };
        db.ItemPackagingLevels.AddRange(new ItemPackagingLevel { ItemId = water.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 }, shrink,
                                        new ItemPackagingLevel { ItemId = other.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 }, carton);
        // إنتاج اليوم بكلفة 100 للقطعة (يصبح متوسط الكلفة 100)
        foreach (var (item, pieces) in new[] { (water, 1_000m), (other, 240m) })
            db.StockTransactions.Add(new StockTransaction { ItemId = item.Id, WarehouseId = fg.Id, QuantityBaseUnits = pieces, UnitCost = item.CostPrice,
                                                            TransactionType = StockTransactionType.ProductionOutput, CreatedByUserId = _f.AdminId });
        await db.SaveChangesAsync();

        var sales = new SalesService(db);
        async Task<int> Sell(Item item, ItemPackagingLevel level, decimal qty, bool free = false)
        {
            var (_, id) = await sales.CreateInvoiceAsync(new SalesInvoiceHeaderInput(customer.Id, fg.Id, DateTime.Today, InvoicePaymentMethod.Credit,
                                                                                     IsFreeSale: free, FreeSaleRecipient: free ? "معرض" : null), _f.AdminId);
            Assert.True((await sales.AddLineAsync(id!.Value, new SalesInvoiceLineInput(item.Id, level.Id, qty), _f.AdminId)).Success);
            var posted = await sales.PostInvoiceAsync(id.Value, _f.AdminId);
            Assert.True(posted.result.Success, posted.result.ErrorMessage);
            return id.Value;
        }
        await Sell(water, shrink, 5);                 // 100 قطعة × 250 = 25,000
        await Sell(water, shrink, 1, free: true);     // مجاني 20 قطعة
        await Sell(other, carton, 2);                 // 24 × 300 = 7,200
        var (ok, _) = await new CustomerReturnService(db).CreateAsync(new CustomerReturnRequest(customer.Id, fg.Id, DateTime.Today, CustomerReturnSettlement.Debt,
            "تالف الشكل", new[] { new CustomerReturnLineInput(water.Id, shrink.Id, 2) }, _f.AdminId));
        Assert.True(ok.Success, ok.ErrorMessage);   // 40 قطعة = 10,000

        var reports = new ReportsService(db);
        var today = new ReportRequest(DateTime.Today, DateTime.Today, water.Id);

        // ---- المبيعات والمرتجع والمجاني لمنتج واحد: بالاسم الموحد وبعبوته ----
        var s = await reports.SalesByProductAsync(today);
        var row = Assert.Single(s.Rows);
        Assert.Equal(new[] { "شرنك 20×330", "5", "25,000", "2", "10,000", "1", "3", "15,000" }, row);
        Assert.Contains(("صافي المبيعات", "15,000"), s.Totals);

        // ---- لكل المنتجات: كل منتج سطر بعبوته (لا يُجمع شرنك مع كارتون) ----
        var all = await reports.SalesByProductAsync(today with { ItemId = null });
        Assert.Contains(all.Rows, r => r[0] == "كارتون 12×500" && r[1] == "2" && r[2] == "7,200");
        Assert.Contains(all.Rows, r => r[0] == "شرنك 20×330");

        // ---- الكلفة والربح: الكلفة الفعلية لحظة الصرف بعد طرح المرتجع، والمجاني مستقل ----
        var p = await reports.CostAndProfitAsync(today);
        var pr = Assert.Single(p.Rows);
        // صافي 60 قطعة: مبيع 15,000، كلفة 6,000، ربح 9,000 (60%)، كلفة المجاني 2,000
        Assert.Equal(new[] { "شرنك 20×330", "3", "15,000", "6,000", "9,000", "60.0", "2,000" }, pr);
        Assert.Contains(("الربح الإجمالي", "9,000"), p.Totals);

        // ---- الإنتاج: يومي وأسبوعي وشهري، بالكلفة أو بلا كلفة ----
        var daily = await reports.ProductionAsync(new ReportRequest(DateTime.Today, DateTime.Today, water.Id, Period: ReportPeriod.Day, WithCost: true));
        Assert.Equal(new[] { "الفترة", "المنتج", "المنتَج", "الكلفة", "كلفة العبوة" }, daily.Columns);
        Assert.Equal(new[] { DateTime.Today.ToString("yyyy/MM/dd", System.Globalization.CultureInfo.InvariantCulture), "شرنك 20×330", "50", "100,000", "2,000" }, Assert.Single(daily.Rows));
        Assert.Equal("الإنتاج اليومي بالكلفة", daily.Title);
        var weekly = await reports.ProductionAsync(new ReportRequest(DateTime.Today.AddDays(-10), DateTime.Today, null, Period: ReportPeriod.Week));
        Assert.Equal(new[] { "الفترة", "المنتج", "المنتَج" }, weekly.Columns);                // بلا كلفة لمن لا يملكها
        Assert.Contains(weekly.Rows, r => r[0].StartsWith($"أسبوع {ReportsService.WeekStart(DateTime.Today):yyyy/MM/dd}") && r[1] == "كارتون 12×500" && r[2] == "20");
        Assert.Equal("الأسبوع يبدأ السبت.", weekly.Notes);
        var monthly = await reports.ProductionAsync(new ReportRequest(DateTime.Today, DateTime.Today, water.Id, Period: ReportPeriod.Month));
        Assert.Equal(DateTime.Today.ToString("yyyy/MM", System.Globalization.CultureInfo.InvariantCulture), Assert.Single(monthly.Rows)[0]);
    }
}
