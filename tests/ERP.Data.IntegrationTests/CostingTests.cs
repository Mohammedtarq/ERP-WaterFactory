using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>المرحلة م1: المتوسط المرجّح، وفاتورة الشراء بوحدات المورد، ومقترح الشراء.</summary>
[Collection("controls")]
public class CostingTests
{
    private readonly ControlsFixture _f;
    public CostingTests(ControlsFixture f) => _f = f;

    [Fact]
    public async Task Purchase_invoice_in_cartons_updates_stock_debt_and_weighted_average()
    {
        await using var db = _f.NewDb(_f.AdminId);
        var raw = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.RawMaterial);
        var supplier = await db.Suppliers.FirstAsync();
        var item = new Item { ItemCode = "RM-AVG", ItemName = "امبولة اختبار المتوسط", SourcingMethod = SourcingMethod.Purchased, UnitWeightGrams = 20 };
        db.Items.Add(item);
        await db.SaveChangesAsync();
        var piece = new ItemPackagingLevel { ItemId = item.Id, LevelName = "قطعة", ContainsQuantity = 1, EquivalentBaseUnits = 1 };
        db.ItemPackagingLevels.Add(piece);
        await db.SaveChangesAsync();
        db.ItemPackagingLevels.Add(new ItemPackagingLevel { ItemId = item.Id, LevelName = "كرتون", ParentLevelId = piece.Id, ContainsQuantity = 1000, EquivalentBaseUnits = 1000 });
        await db.SaveChangesAsync();

        var svc = new SupplierPurchasingService(db);
        var units = await svc.GetPurchaseUnitsAsync(item.Id);
        Assert.Contains(units, u => u.Label == "كرتون" && u.PiecesPerUnit == 1000);
        Assert.Contains(units, u => u.Label == "طن" && u.PiecesPerUnit == 50_000);   // 1,000,000 غم ÷ 20 غم

        // الشراء الأول: 3 كراتين × 30,000 = 90,000 ← 3,000 قطعة بكلفة 30 للقطعة
        var (r1, receipt1) = await svc.PurchaseInvoiceAsync(supplier.Id, raw.Id, DateTime.Today, "INV-77",
            new[] { new PurchaseInvoiceLineInput(item.Id, "كرتون", 1000, 3, 30_000) }, 0, _f.AdminId);
        Assert.True(r1.Success, r1.ErrorMessage);
        Assert.Equal(30m, (await db.Items.AsNoTracking().FirstAsync(i => i.Id == item.Id)).CostPrice);
        var line = await db.GoodsReceiptLines.AsNoTracking().FirstAsync(l => l.GoodsReceiptId == receipt1);
        Assert.Equal(("كرتون", 3m, 30_000m, 3000m), (line.PurchaseUnit, line.PurchaseQuantity!.Value, line.PurchaseUnitPrice!.Value, line.QuantityReceived));

        // الشراء الثاني بسعر أعلى: 1,000 قطعة × 42 ← المتوسط = (3000×30 + 1000×42) ÷ 4000 = 33
        var (r2, _) = await svc.PurchaseInvoiceAsync(supplier.Id, raw.Id, DateTime.Today, "INV-78",
            new[] { new PurchaseInvoiceLineInput(item.Id, "قطعة", 1, 1000, 42) }, 10_000, _f.AdminId);
        Assert.True(r2.Success, r2.ErrorMessage);
        Assert.Equal(33m, (await db.Items.AsNoTracking().FirstAsync(i => i.Id == item.Id)).CostPrice);
        Assert.Equal(4000m, await db.StockTransactions.Where(t => t.ItemId == item.Id).SumAsync(t => t.QuantityBaseUnits));

        // الذمة: قيد الاستلام بالقيمة الدقيقة، والدفعة النقدية سند صرف للمورد
        var receiptEntries = await db.JournalEntries.Where(j => j.Description!.Contains("INV") || j.EntryType == JournalEntryType.AutoPurchase)
            .Include(j => j.Lines).ThenInclude(l => l.Account).ToListAsync();
        Assert.Contains(receiptEntries, j => j.Lines.Any(l => l.Account.AccountCode == "2101" && l.Credit == 90_000));
        Assert.True(await db.Vouchers.AnyAsync(v => v.PartyType == VoucherPartyType.Supplier && v.PartyId == supplier.Id && v.Amount == 10_000));

        // أي صرف بعد ذلك يحمل كلفة المتوسط الساري
        db.StockTransactions.Add(new StockTransaction { ItemId = item.Id, WarehouseId = raw.Id, QuantityBaseUnits = -900, TransactionType = StockTransactionType.Issue,
                                                        ReferenceTable = "Test", CreatedByUserId = _f.AdminId });
        await db.SaveChangesAsync();
        var issue = await db.StockTransactions.AsNoTracking().Where(t => t.ItemId == item.Id && t.TransactionType == StockTransactionType.Issue).SingleAsync();
        Assert.Equal(33m, issue.UnitCost);
        Assert.Equal(33m, (await db.Items.AsNoTracking().FirstAsync(i => i.Id == item.Id)).CostPrice);   // الصرف لا يغيّر المتوسط

        // مقترح الشراء: 900 قطعة في 30 يومًا = 30 يوميًا؛ مدة التجهيز 20 يومًا ← نقطة الطلب 30 × 27 = 810، والرصيد 3100 يكفي
        var tracked = await db.Items.FirstAsync(i => i.Id == item.Id);
        tracked.LeadTimeDays = 20;
        await db.SaveChangesAsync();
        var row = (await new ReorderService(db).SuggestAsync()).Single(x => x.ItemId == item.Id);
        Assert.Equal(30m, row.DailyUse);
        Assert.Equal(810m, row.ReorderPoint);
        Assert.False(row.NeedsOrder);
        tracked.MinStockAlertLevel = 5000;                                   // حد التنبيه أكبر من الرصيد ← اطلب الآن
        await db.SaveChangesAsync();
        row = (await new ReorderService(db).SuggestAsync()).Single(x => x.ItemId == item.Id);
        Assert.True(row.NeedsOrder);
        Assert.Equal("اطلب الآن", row.Status);
        Assert.Equal(5000m - 3100m, row.SuggestedQuantity);
    }

    [Fact]
    public async Task Purchase_invoice_rejects_bad_input_without_side_effects()
    {
        await using var db = _f.NewDb(_f.AdminId);
        var raw = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.RawMaterial);
        var supplier = await db.Suppliers.FirstAsync();
        var item = await db.Items.FirstAsync(i => i.ItemCode == "RM-CAP");
        var before = await db.GoodsReceipts.CountAsync();
        var svc = new SupplierPurchasingService(db);
        Assert.False((await svc.PurchaseInvoiceAsync(supplier.Id, raw.Id, DateTime.Today, null, Array.Empty<PurchaseInvoiceLineInput>(), 0, _f.AdminId)).result.Success);
        Assert.False((await svc.PurchaseInvoiceAsync(supplier.Id, raw.Id, DateTime.Today, null,
            new[] { new PurchaseInvoiceLineInput(item.Id, "قطعة", 1, 10, 5) }, 100, _f.AdminId)).result.Success);   // المدفوع أكبر من الإجمالي
        Assert.Equal(before, await db.GoodsReceipts.CountAsync());
    }
}
