using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>المرحلة م4: المصروف بأنواعه، والحسابات الختامية، ورأس المال التشغيلي وصندوق المنزل، ومحاكاة الكلفة، والتقرير اليومي للصناديق.</summary>
[Collection("controls")]
public class FinalAccountsTests
{
    private readonly ControlsFixture _f;
    public FinalAccountsTests(ControlsFixture f) => _f = f;

    // شهر مستقبلي لا يمسه قفل الفترات ولا حركات الاختبارات الأخرى
    private static readonly DateTime Day = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 10).AddMonths(2);

    [Fact]
    public async Task Expenses_hit_box_and_ledger_by_kind_and_final_accounts_split_operating_from_non_operating()
    {
        await using var db = _f.NewDb(_f.AdminId);
        var svc = new FinanceEntryService(db);
        var cats = await svc.GetCategoriesAsync();
        int Cat(string name) => cats.Single(c => c.Name == name).Id;

        var (r1, power) = await svc.CreateAsync(new FinanceEntryInput(Cat("كهرباء ومولدة"), 50_000, Day, PartyName: "المولدة الأهلية", ReceiptNumber: "77"), _f.AdminId);
        Assert.True(r1.Success, r1.ErrorMessage);
        var (r2, _) = await svc.CreateAsync(new FinanceEntryInput(Cat("توسعة"), 200_000, Day), _f.AdminId);
        Assert.True(r2.Success, r2.ErrorMessage);
        var (r3, _) = await svc.CreateAsync(new FinanceEntryInput(Cat("إيراد آخر"), 30_000, Day, PartyName: "بيع كراتين فارغة"), _f.AdminId);
        Assert.True(r3.Success, r3.ErrorMessage);
        var (r4, wrong) = await svc.CreateAsync(new FinanceEntryInput(Cat("نثرية"), 9_000, Day), _f.AdminId);
        Assert.True(r4.Success, r4.ErrorMessage);
        Assert.False((await svc.CreateAsync(new FinanceEntryInput(Cat("نثرية"), 0, Day), _f.AdminId)).result.Success);

        // القيد على حساب نوعه، والصندوق تحرك
        var je = await db.JournalEntries.AsNoTracking().Include(j => j.Lines).ThenInclude(l => l.Account).SingleAsync(j => j.Id == power!.JournalEntryId);
        Assert.Equal(("5101", "1101"), (je.Lines.Single(l => l.Debit > 0).Account.AccountCode, je.Lines.Single(l => l.Credit > 0).Account.AccountCode));
        var cashTx = await db.CashBoxTransactions.AsNoTracking().SingleAsync(t => t.ReferenceTable == "FinanceEntries" && t.ReferenceId == power!.Id);
        Assert.Equal((CashBoxTxType.Expense, -50_000m), (cashTx.TxType, cashTx.Amount));

        // الإلغاء: قيد عكسي وحركة الصندوق ملغاة
        Assert.False((await svc.VoidAsync(wrong!.Id, "", _f.AdminId)).Success);
        Assert.True((await svc.VoidAsync(wrong.Id, "تكرار", _f.AdminId)).Success);
        Assert.True(await db.CashBoxTransactions.AnyAsync(t => t.ReferenceTable == "FinanceEntries" && t.ReferenceId == wrong.Id && t.IsVoided));
        Assert.Equal(0m, await db.JournalEntryLines.Where(l => l.JournalEntry.SourceTable == "FinanceEntries" && l.JournalEntry.SourceId == wrong.Id)
                                                   .SumAsync(l => l.Debit - l.Credit));
        Assert.Single(await svc.ListAsync(Day, Day), e => e.IsVoided);

        // مبيعات الشهر: كارتونان بسعر خاص 2,400 بدل 3,000
        var fg = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.FinishedGoods);
        var w500 = await db.Items.FirstAsync(i => i.ItemCode == "W-500");
        var carton = await db.ItemPackagingLevels.FirstAsync(l => l.ItemId == w500.Id && l.EquivalentBaseUnits == 12);
        var (rc, _) = await new WarehouseDocumentService(db).CreateAsync(new StockDocumentRequest(StockDocumentType.Receipt, fg.Id, DateTime.Today,
            new[] { new StockDocumentLineInput(w500.Id, carton.Id, 2, NewBatchNumber: "FA-T1") }, _f.AdminId));
        Assert.True(rc.Success, rc.ErrorMessage);
        var customer = await db.Customers.FirstAsync(c => c.CustomerType == CustomerType.Direct);
        var sales = new SalesService(db);
        var (_, invId) = await sales.CreateInvoiceAsync(new SalesInvoiceHeaderInput(customer.Id, fg.Id, Day, InvoicePaymentMethod.Credit), _f.AdminId);
        Assert.True((await sales.AddLineAsync(invId!.Value, new SalesInvoiceLineInput(w500.Id, carton.Id, 2, UnitPrice: 2400), _f.AdminId)).Success);
        var (posted, _) = await sales.PostInvoiceAsync(invId.Value, _f.AdminId);
        Assert.True(posted.Success, posted.ErrorMessage);
        var issueCost = -await db.StockTransactions.Where(t => t.ReferenceTable == "SalesInvoices" && t.ReferenceId == invId).SumAsync(t => t.QuantityBaseUnits * (t.UnitCost ?? 0));

        var report = await new FinalAccountsService(db).MonthAsync(Day.Year, Day.Month);
        var p = Assert.Single(report.Products);
        Assert.Equal((24m, 4_800m, 6_000m, 1_200m), (p.PiecesSold, p.Revenue, p.ListValue, p.Discount));
        Assert.Equal(Math.Round(issueCost, 2), p.Cost);
        Assert.Equal(50_000m, report.OperatingExpenses);              // النثرية الملغاة لا تُحسب
        Assert.Equal(200_000m, report.NonOperatingExpenses);
        Assert.Equal(30_000m, report.OtherIncome);
        Assert.Equal(report.Revenue - report.MaterialsCost - 50_000m - 200_000m + 30_000m - report.Salaries - report.RepFieldExpenses - report.Losses,
                     report.NetProfit);
        // التوسعة لا تدخل كلفة القنينة
        Assert.Equal(Math.Round((report.MaterialsCost + 50_000m + report.Salaries + report.RepFieldExpenses) / 24m, 2), report.CostPerPiece);
        Assert.Contains(report.Lines, l => l.Label == "خصم الوكلاء والأسعار الخاصة" && l.Amount == -1_200m);
        Assert.Contains(report.Lines, l => l.Section.StartsWith("غير تشغيلي") && l.Label == "توسعة");
        Assert.Equal(report.NetProfit, report.Lines.Last().Amount);

        // التقرير اليومي للصناديق
        var daily = await new FinalAccountsService(db).DailyCashAsync(Day);
        var box = daily.Single(b => b.BoxId == cashTx.CashBoxId);
        Assert.Equal(250_000m, box.Out);
        Assert.Equal(30_000m, box.In);
        Assert.Equal(box.Opening + 30_000m - 250_000m, box.Closing);
    }

    [Fact]
    public async Task Surplus_over_working_capital_moves_to_the_home_box_and_no_more()
    {
        await using var db = _f.NewDb(_f.AdminId);
        var cash = new CashBoxService(db);
        var box = (await cash.GetBoxesAsync(_f.AdminId)).First(b => b.BoxType != CashBoxType.Home && b.BoxType != CashBoxType.Bank);
        Assert.True((await cash.DepositAsync(box.Id, 100_000, DateTime.Today, "المالك", "رأس مال إضافي", _f.AdminId)).result.Success);

        var fa = new FinalAccountsService(db);
        var before = await fa.WorkingCapitalAsync(DateTime.Today);
        // رأس المال الساري: آخر قيمة بتاريخ سريان ≤ اليوم (والمستقبلية لا تُطبَّق بعد)
        Assert.True((await fa.SetCapitalAsync(DateTime.Today.AddDays(-30), 1, "قديم", _f.AdminId)).Success);
        Assert.True((await fa.SetCapitalAsync(DateTime.Today, before.NetAssetsInBusiness - 40_000, "رأس المال التشغيلي", _f.AdminId)).Success);
        Assert.True((await fa.SetCapitalAsync(DateTime.Today.AddDays(5), 999_999_999, "لاحق", _f.AdminId)).Success);
        var wc = await fa.WorkingCapitalAsync(DateTime.Today);
        Assert.Equal(40_000m, wc.Surplus);
        Assert.Equal(DateTime.Today, wc.CapitalEffectiveFrom);

        Assert.False((await fa.MoveSurplusToHomeAsync(box.Id, 40_001, DateTime.Today, _f.AdminId)).Success);
        Assert.True((await fa.MoveSurplusToHomeAsync(box.Id, 25_000, DateTime.Today, _f.AdminId)).Success);
        var after = await fa.WorkingCapitalAsync(DateTime.Today);
        Assert.Equal(before.HomeBoxBalance + 25_000m, after.HomeBoxBalance);
        Assert.Equal(15_000m, after.Surplus);
        Assert.Equal(wc.NetAssets, after.NetAssets);                    // النقل لا يغيّر صافي الموجودات
        Assert.Single(await db.CashBoxes.Where(b => b.BoxType == CashBoxType.Home).ToListAsync());

        // نظّف: رأس المال لا يؤثر على بقية الاختبارات
        await db.WorkingCapitalSettings.ExecuteDeleteAsync();
    }

    [Fact]
    public async Task Cost_simulation_reprices_recipe_without_touching_real_costs()
    {
        await using var db = _f.NewDb(_f.AdminId);
        var pre = await db.Items.AsNoTracking().FirstAsync(i => i.ItemCode == "RM-PRE");
        var w500 = await db.Items.AsNoTracking().FirstAsync(i => i.ItemCode == "W-500");
        var qty = await db.BOMLines.Where(l => l.BOM.FinishedItemId == w500.Id && l.BOM.IsActive && l.RawMaterialItemId == pre.Id)
                                  .Select(l => l.QuantityPerUnit).FirstAsync();
        var current = pre.CostPrice ?? 0;
        var rows = await new FinalAccountsService(db).SimulateAsync(new Dictionary<int, decimal> { [pre.Id] = current + 20 }, overheadPerPiece: 10);
        var row = rows.Single(r => r.ItemId == w500.Id);
        Assert.Equal(Math.Round(qty * 20, 2), row.SimulatedMaterialCost - row.CurrentMaterialCost);
        Assert.Equal(row.SimulatedMaterialCost + 10, row.SimulatedCost);
        Assert.Equal(w500.SalePrice - row.SimulatedCost, row.SimulatedMargin);
        Assert.Equal(pre.CostPrice, (await db.Items.AsNoTracking().FirstAsync(i => i.Id == pre.Id)).CostPrice);
    }
}
