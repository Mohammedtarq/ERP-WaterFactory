using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>بيع المواد التالفة نقدًا، وتسليم نقد المندوب للصندوق فورًا عند ترحيل فاتورة السيارة.</summary>
[Collection("provisioned")]
public class DamagedSaleAndHandoverTests
{
    private readonly ProvisionedFixture _f;
    public DamagedSaleAndHandoverTests(ProvisionedFixture f) => _f = f;

    private static async Task<decimal> Balance(ProjectDbContext db, int itemId, int whId) =>
        await db.StockTransactions.Where(t => t.ItemId == itemId && t.WarehouseId == whId).SumAsync(t => (decimal?)t.QuantityBaseUnits) ?? 0;

    [Fact]
    public async Task Damaged_materials_are_sold_for_cash_from_the_damaged_store()
    {
        Assert.True(_f.Install.Success, _f.Install.ErrorMessage);
        await using var db = _f.NewDb();
        var user = _f.AdminLocalId;
        var item = new Item { ItemCode = "DS-PRE", ItemName = "أمبولة تالفة للبيع", SourcingMethod = SourcingMethod.Purchased };
        db.Items.Add(item);
        await db.SaveChangesAsync();
        db.ItemPackagingLevels.Add(new ItemPackagingLevel { ItemId = item.Id, LevelName = "قطعة", ContainsQuantity = 1, EquivalentBaseUnits = 1 });
        var damaged = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.Damaged);
        db.StockTransactions.Add(new StockTransaction { ItemId = item.Id, WarehouseId = damaged.Id, QuantityBaseUnits = 100,
                                                        TransactionType = StockTransactionType.Damaged, CreatedByUserId = user });
        await db.SaveChangesAsync();

        var svc = new DamagedSaleService(db);
        Assert.Contains(await svc.GetAvailableAsync(), r => r.ItemId == item.Id && r.Available == 100);
        var boxId = await db.CashBoxes.Where(b => b.IsActive && b.IsDefault).Select(b => b.Id).FirstAsync();
        var cash = new CashBoxService(db);
        var boxBefore = await cash.GetBalanceAsync(boxId);

        Assert.False((await svc.CreateAsync(" ", DateTime.Today, new[] { new DamagedSaleLineInput(item.Id, 10, 25) }, null, user, boxId)).result.Success);
        Assert.False((await svc.CreateAsync("الخردة", DateTime.Today, new[] { new DamagedSaleLineInput(item.Id, 10, 25), new DamagedSaleLineInput(item.Id, 5, 25) }, null, user, boxId)).result.Success);
        Assert.False((await svc.CreateAsync("الخردة", DateTime.Today, new[] { new DamagedSaleLineInput(item.Id, 101, 25) }, null, user, boxId)).result.Success);
        Assert.Equal(100, await Balance(db, item.Id, damaged.Id));                     // الرفض لم يترك أثرًا

        var (r, sale) = await svc.CreateAsync("محل أبو جاسم للخردة", DateTime.Today, new[] { new DamagedSaleLineInput(item.Id, 60, 25) }, "أمبولات مشوهة", user, boxId);
        Assert.True(r.Success, r.ErrorMessage);
        Assert.StartsWith($"DS-{DateTime.Today.Year}-", sale!.SaleNumber);
        Assert.Equal(1_500, sale.TotalAmount);
        Assert.Equal(40, await Balance(db, item.Id, damaged.Id));
        Assert.Equal(boxBefore + 1_500, await cash.GetBalanceAsync(boxId));
        var doc = await db.StockDocuments.SingleAsync(d => d.Id == sale.StockDocumentId);
        Assert.Equal(StockDocumentType.Issue, doc.DocumentType);
        Assert.Contains("محل أبو جاسم", doc.PartyName);
        var je = await db.JournalEntryLines.Include(l => l.Account).Where(l => l.JournalEntryId == sale.JournalEntryId).ToListAsync();
        Assert.Equal(1_500, je.Single(l => l.Account.AccountCode == "1101").Debit);
        Assert.Equal(1_500, je.Single(l => l.Account.AccountCode == "4103").Credit);
        var row = Assert.Single(await svc.GetListAsync(DateTime.Today, DateTime.Today), x => x.Id == sale.Id);
        Assert.Contains("أمبولة تالفة للبيع", row.ItemsText);
    }

    [Fact]
    public async Task Van_cash_invoice_can_be_handed_over_to_the_box_immediately()
    {
        Assert.True(_f.Install.Success, _f.Install.ErrorMessage);
        await using var db = _f.NewDb();
        var user = _f.AdminLocalId;
        var reps = new RepsService(db);
        var van = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.RepVan);
        var repId = van.OwnerEmployeeId!.Value;
        var fg = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.FinishedGoods);
        var item = await db.Items.FirstAsync(i => i.ItemCode == "W-1500");
        Assert.True((await reps.LoadVanAsync(van.Id, fg.Id, new[] { new StockLineInput(item.Id, null, 10) }, user)).Success);
        var walletBefore = await reps.GetWalletBalanceAsync(repId);
        var cash = new CashBoxService(db);
        var boxId = await db.CashBoxes.Where(b => b.IsActive && b.IsDefault).Select(b => b.Id).FirstAsync();
        var boxBefore = await cash.GetBalanceAsync(boxId);

        var sales = new SalesService(db);
        var direct = await db.Customers.FirstAsync(c => c.CustomerType == CustomerType.Direct);
        var piece = await db.ItemPackagingLevels.FirstAsync(l => l.ItemId == item.Id && l.EquivalentBaseUnits == 1);
        var (_, invId) = await sales.CreateInvoiceAsync(new(direct.Id, van.Id, DateTime.Today, InvoicePaymentMethod.Cash), user);
        await sales.AddLineAsync(invId!.Value, new(item.Id, piece.Id, 4), user);
        var (posted, summary) = await sales.PostInvoiceAsync(invId.Value, user, handOverRepCashNow: true);
        Assert.True(posted.Success, posted.ErrorMessage);
        Assert.Null(summary!.HandoverError);
        Assert.Equal(summary.AmountPaidNow, summary.HandedOverToBox);
        Assert.True(summary.AmountPaidNow > 0);
        Assert.Equal(walletBefore, await reps.GetWalletBalanceAsync(repId));                  // لم يبقَ شيء في المحفظة
        Assert.Equal(boxBefore + summary.AmountPaidNow, await cash.GetBalanceAsync(boxId));   // دخل الصندوق مباشرة

        // بدون الخيار: يبقى النقد في المحفظة كالعادة
        var (_, inv2) = await sales.CreateInvoiceAsync(new(direct.Id, van.Id, DateTime.Today, InvoicePaymentMethod.Cash), user);
        await sales.AddLineAsync(inv2!.Value, new(item.Id, piece.Id, 2), user);
        var (_, s2) = await sales.PostInvoiceAsync(inv2.Value, user);
        Assert.Equal(0, s2!.HandedOverToBox);
        Assert.Equal(walletBefore + s2.AmountPaidNow, await reps.GetWalletBalanceAsync(repId));

        // تنظيف: ما بقي في السيارة يعود للمخزن والنقد يُسلَّم، فلا يتأثر اختبار المندوب الآخر
        var left = await Balance(db, item.Id, van.Id);
        if (left > 0) Assert.True((await reps.ReturnFromVanAsync(van.Id, fg.Id, new[] { new StockLineInput(item.Id, null, left) }, user)).Success);
        Assert.True((await reps.RecordCashHandoverAsync(repId, s2.AmountPaidNow, DateTime.Today, user)).Success);
    }
}
