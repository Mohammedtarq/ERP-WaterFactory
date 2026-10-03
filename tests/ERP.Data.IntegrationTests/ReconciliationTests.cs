using ERP.Data.ProjectDb.Entities;
using ERP.Data.Security;
using ERP.Data.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>المطابقة الدورية: التقييم (الكلفة/سعر البيع للمنتج التام)، مطابقة الأساس، الفائض وتوزيعه، سحوبات الشركاء، الخسارة.</summary>
[Collection("provisioned")]
public class ReconciliationTests
{
    private readonly ProvisionedFixture _f;
    public ReconciliationTests(ProvisionedFixture f) => _f = f;

    [Fact]
    public async Task Baseline_then_surplus_distributed_by_share_withdrawals_and_loss()
    {
        Assert.True(_f.Install.Success, _f.Install.ErrorMessage);
        await using var db = _f.NewDb();
        var admin = _f.AdminLocalId;
        var svc = new ReconciliationService(db);

        // صنفان معروفا القيمة: مادة كلفتها 10، ومنتج وصفته 2 من المادة (كلفة 20) وسعر بيعه 50
        var raw = new Item { ItemCode = "RC-RAW", ItemName = "مادة المطابقة", SourcingMethod = SourcingMethod.Purchased, CostPrice = 10 };
        var fg = new Item { ItemCode = "RC-FG", ItemName = "منتج المطابقة", SourcingMethod = SourcingMethod.Manufactured, SalePrice = 50 };
        db.Items.AddRange(raw, fg);
        await db.SaveChangesAsync();
        db.BillOfMaterials.Add(new BillOfMaterials { FinishedItemId = fg.Id, Name = "وصفة المطابقة", IsActive = true,
                                                     Lines = { new BOMLine { RawMaterialItemId = raw.Id, QuantityPerUnit = 2 } } });
        var rawWh = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.RawMaterial);
        var fgWh = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.FinishedGoods);
        var damagedWh = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.Damaged);
        void Stock(Item i, int wh, decimal q) => db.StockTransactions.Add(new StockTransaction
            { ItemId = i.Id, WarehouseId = wh, QuantityBaseUnits = q, TransactionType = StockTransactionType.Receipt, CreatedByUserId = admin });
        Stock(raw, rawWh.Id, 100);
        Stock(fg, fgWh.Id, 10);
        Stock(raw, damagedWh.Id, 999);          // التالف لا يُقيَّم
        await db.SaveChangesAsync();

        Assert.Equal(20m, (await svc.ManufacturedUnitCostsAsync())[fg.Id]);
        var atCost = await svc.ComputeAsync(DateTime.Today, FinishedGoodsValuation.Cost);
        var atSale = await svc.ComputeAsync(DateTime.Today, FinishedGoodsValuation.SalePrice);
        Assert.Contains(atCost.Lines, l => l.Section == ReconciliationService.RawSection && l.Description.Contains("RC-RAW") && l.Quantity == 100 && l.Value == 1_000);
        Assert.Contains(atCost.Lines, l => l.Section == ReconciliationService.FgSection && l.Description.Contains("RC-FG") && l.UnitValue == 20 && l.Value == 200);
        Assert.Contains(atSale.Lines, l => l.Section == ReconciliationService.FgSection && l.Description.Contains("RC-FG") && l.UnitValue == 50 && l.Value == 500);
        Assert.DoesNotContain(atCost.Lines, l => l.Quantity == 999);
        Assert.Equal(atCost.TotalAssets - atCost.TotalLiabilities, atCost.NetAssets);

        // الشركاء: الاسم فريد، والمجموع لا يتجاوز 100%، والإدارة للأدمن
        var (p1, mohammed) = await svc.SavePartnerAsync(null, "محمد", 50, true, true, "المدير", admin);
        Assert.True(p1.Success, p1.ErrorMessage);
        var (_, second) = await svc.SavePartnerAsync(null, "الشريك الثاني", 30, false, true, null, admin);
        Assert.False((await svc.SavePartnerAsync(null, "الشريك الثالث", 25, false, true, null, admin)).result.Success);   // 105%
        Assert.False((await svc.SavePartnerAsync(null, "محمد", 10, false, true, null, admin)).result.Success);            // مكرر
        var clerkRole = await db.Roles.FirstAsync(r => r.Name == "موظف مبيعات");
        var clerk = new User { Username = "rec_clerk", PasswordHash = PasswordHasher.Hash("x"), RoleId = clerkRole.Id };
        db.Users.Add(clerk);
        await db.SaveChangesAsync();
        Assert.False((await svc.SavePartnerAsync(null, "غير مخوّل", 5, false, true, null, clerk.Id)).result.Success);
        var (_, third) = await svc.SavePartnerAsync(null, "الشريك الثالث", 20, false, true, null, admin);

        // مطابقة الأساس: بلا فائض ولا توزيع
        Assert.False((await svc.PostAsync(DateTime.Today, FinishedGoodsValuation.Cost, null, clerk.Id)).result.Success);
        var (b, baseline) = await svc.PostAsync(DateTime.Today, FinishedGoodsValuation.Cost, "مطابقة الأساس", admin);
        Assert.True(b.Success, b.ErrorMessage);
        Assert.True(baseline!.IsBaseline);
        Assert.Equal(0, baseline.Surplus);
        Assert.StartsWith($"REC-{DateTime.Today.Year}-", baseline.ReconNumber);
        Assert.False(await db.PartnerTransactions.AnyAsync(t => t.ReconciliationId == baseline.Id));
        Assert.Contains(await db.AssetReconciliationLines.Where(l => l.ReconciliationId == baseline.Id).ToListAsync(), l => l.Description.Contains("RC-RAW"));

        // بين المطابقتين: مخزون +50 (قيمة 500) وإيداع مالك 100,000 في الصندوق (ليس ربحًا)
        Stock(raw, rawWh.Id, 50);
        await db.SaveChangesAsync();
        var boxId = await db.CashBoxes.Where(x => x.IsActive && x.IsDefault).Select(x => x.Id).FirstAsync();
        Assert.True((await new CashBoxService(db).DepositAsync(boxId, 100_000, DateTime.Today, "المالك", null, admin)).result.Success);
        var next = await svc.ComputeAsync(DateTime.Today, FinishedGoodsValuation.Cost);
        Assert.Equal(baseline.Id, next.PreviousId);
        Assert.Equal(100_000, next.OwnerDeposits);
        Assert.Equal(500, next.Surplus);
        Assert.Equal(new[] { 250m, 150m, 100m }, next.Shares.OrderByDescending(s => s.Amount).Select(s => s.Amount));

        var (r2, recon2) = await svc.PostAsync(DateTime.Today, FinishedGoodsValuation.Cost, null, admin);
        Assert.True(r2.Success, r2.ErrorMessage);
        Assert.Equal(500, recon2!.Surplus);
        var partners = await svc.GetPartnersAsync();
        Assert.Equal(250, partners.Single(p => p.Id == mohammed!.Id).Balance);
        Assert.True(partners.Single(p => p.Id == mohammed!.Id).IsManager);
        Assert.Equal(150, partners.Single(p => p.Id == second!.Id).Balance);
        Assert.Equal(100, partners.Single(p => p.Id == third!.Id).Balance);
        var je = await db.JournalEntryLines.Include(l => l.Account).Where(l => l.JournalEntryId == recon2.JournalEntryId).ToListAsync();
        Assert.Equal(500, je.Single(l => l.Account.AccountCode == "3103").Credit);
        Assert.Equal(500, je.Single(l => l.Account.AccountCode == "3104").Debit);

        // سحب أرباح: لا يتجاوز رصيد الشريك، ويخرج من الصندوق، ولا يُعد خسارة في المطابقة التالية
        Assert.False((await svc.WithdrawAsync(mohammed!.Id, 251, DateTime.Today, null, admin, boxId)).result.Success);
        var boxBefore = await new CashBoxService(db).GetBalanceAsync(boxId);
        var (w, wtx) = await svc.WithdrawAsync(mohammed.Id, 200, DateTime.Today, "سحب شهري", admin, boxId);
        Assert.True(w.Success, w.ErrorMessage);
        Assert.Equal(boxBefore - 200, await new CashBoxService(db).GetBalanceAsync(boxId));
        Assert.Equal(CashBoxTxType.PartnerWithdrawal, (await db.CashBoxTransactions.SingleAsync(t => t.ReferenceTable == "PartnerTransactions" && t.ReferenceId == wtx!.Id)).TxType);
        var afterWithdrawal = await svc.ComputeAsync(DateTime.Today, FinishedGoodsValuation.Cost);
        Assert.Equal(200, afterWithdrawal.PartnerWithdrawals);
        Assert.Equal(0, afterWithdrawal.Surplus);

        var statement = await svc.GetPartnerStatementAsync(mohammed.Id);
        Assert.Equal(2, statement.Count);
        Assert.Equal(recon2.ReconNumber, statement[0].Reference);
        Assert.Equal(50, statement[^1].Balance);

        // خسارة: نقص مخزون 30 (−300) يوزَّع سالبًا بنفس النسب
        Stock(raw, rawWh.Id, -30);
        await db.SaveChangesAsync();
        var (r3, recon3) = await svc.PostAsync(DateTime.Today, FinishedGoodsValuation.Cost, "خسارة تجريبية", admin);
        Assert.True(r3.Success, r3.ErrorMessage);
        Assert.Equal(-300, recon3!.Surplus);
        var shares3 = await svc.GetSharesAsync(recon3.Id);
        Assert.Equal(-150, shares3.Single(s => s.PartnerId == mohammed.Id).Amount);
        Assert.Equal(-100, (await svc.GetPartnersAsync()).Single(p => p.Id == mohammed.Id).Balance);   // 250 − 200 − 150
        var je3 = await db.JournalEntryLines.Include(l => l.Account).Where(l => l.JournalEntryId == recon3.JournalEntryId).ToListAsync();
        Assert.Equal(300, je3.Single(l => l.Account.AccountCode == "3103").Debit);

        // رصيد افتتاحي، ولا مطابقة بتاريخ أقدم، والنسب الناقصة تمنع التوزيع
        Assert.True((await svc.OpeningAsync(third!.Id, 1_000, DateTime.Today, "من نظام الرحمة", admin)).result.Success);
        Assert.Equal(1_000 + 100 - 60, (await svc.GetPartnersAsync()).Single(p => p.Id == third.Id).Balance);
        Assert.False((await svc.PostAsync(DateTime.Today.AddDays(-1), FinishedGoodsValuation.Cost, null, admin)).result.Success);
        Assert.True((await svc.SavePartnerAsync(third.Id, "الشريك الثالث", 10, false, true, null, admin)).result.Success);   // المجموع 90%
        Stock(raw, rawWh.Id, 10);
        await db.SaveChangesAsync();
        var incomplete = await svc.PostAsync(DateTime.Today, FinishedGoodsValuation.Cost, null, admin);
        Assert.False(incomplete.result.Success);
        Assert.Contains("100%", incomplete.result.ErrorMessage);
        Assert.Equal(3, (await svc.GetReconciliationsAsync()).Count);
    }
}
