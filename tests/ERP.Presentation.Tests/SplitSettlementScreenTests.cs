using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.ViewModels.Finance;
using ERP.Presentation.ViewModels.Shell;
using ERP.Presentation.ViewModels.Warehouse;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>
/// تسوية المندوب المقسّمة: أمين المخزن يسوّي المنتجات من قسم المخازن (اسم موحد مع المتوفر، والكمية تظهر باختيار الصنف،
/// بلا حقل عبوة)، ثم تُكمل المالية النقد من قسمها على التسوية نفسها.
/// </summary>
[Collection("app")]
public class SplitSettlementScreenTests
{
    private readonly AppFixture _f;
    public SplitSettlementScreenTests(AppFixture f) => _f = f;

    private static async Task Open(ModuleViewModel module, SectionViewModel section)
    {
        module.SelectedTab = section;
        await module.IdleAsync();
        await section.IdleAsync();
    }

    [Fact]
    public async Task Store_keeper_settles_products_then_finance_settles_cash()
    {
        int vanId;
        await using (var db = _f.NewDb())
        {
            var branch = await db.Branches.FirstAsync();
            var admin = await db.Users.FirstAsync(u => u.Username == AppFixture.AdminUser);
            var store = new Warehouse { BranchId = branch.Id, Name = "مخزن مندوب التسوية المقسّمة", WarehouseType = WarehouseType.FinishedGoods };
            var shrinkItem = new Item { ItemCode = "SS-S20", ItemName = "ماء تسوية شرنك", SalePrice = 250 };
            var cartonItem = new Item { ItemCode = "SS-C40", ItemName = "ماء تسوية كارتون", SalePrice = 250 };
            var rep = new Employee { FullName = "مندوب التسوية المقسّمة", IsSalesRep = true, BaseSalary = 500_000 };
            db.AddRange(store, shrinkItem, cartonItem, rep);
            await db.SaveChangesAsync();
            var shrink = new ItemPackagingLevel { ItemId = shrinkItem.Id, LevelName = "شرنك", EquivalentBaseUnits = 20 };
            var carton = new ItemPackagingLevel { ItemId = cartonItem.Id, LevelName = "كارتون", EquivalentBaseUnits = 40 };
            db.ItemPackagingLevels.AddRange(new ItemPackagingLevel { ItemId = shrinkItem.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 }, shrink,
                                            new ItemPackagingLevel { ItemId = cartonItem.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 }, carton);
            foreach (var item in new[] { shrinkItem, cartonItem })
                db.StockTransactions.Add(new StockTransaction { ItemId = item.Id, WarehouseId = store.Id, QuantityBaseUnits = 2_000, UnitCost = 100,
                                                                TransactionType = StockTransactionType.Receipt, CreatedByUserId = admin.Id });
            var van = new Warehouse { BranchId = branch.Id, Name = "سيارة التسوية المقسّمة", WarehouseType = WarehouseType.RepVan, OwnerEmployeeId = rep.Id };
            db.Warehouses.Add(van);
            await db.SaveChangesAsync();
            var ops = new RepOperationsService(db);
            var (created, order) = await ops.CreateLoadOrderAsync(van.Id, store.Id, DateTime.Today, new[]
            {
                new RepLoadLineInput(shrinkItem.Id, shrink.Id, 10), new RepLoadLineInput(cartonItem.Id, carton.Id, 5)
            }, null, admin.Id);
            Assert.True(created.Success, created.ErrorMessage);
            Assert.True((await ops.PrepareLoadOrderAsync(order!.Id, null, admin.Id)).result.Success);
            db.RepWalletTransactions.Add(new RepWalletTransaction { EmployeeId = rep.Id, AmountIn = 8_000, Description = "نقد اختبار", TransactionDate = DateTime.Today });
            await db.SaveChangesAsync();
            vanId = van.Id;
        }

        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);

        // ---- أمين المخزن: تسوية المنتجات ----
        var whs = shell.Open<WarehouseModuleViewModel>(ModuleCode.Warehouse);
        var prod = whs.ProductSettlement;
        await Open(whs, prod);
        prod.Van = prod.Vans.Single(v => v.Id == vanId);
        await prod.IdleAsync();
        prod.Store = prod.Stores.Single(w => w.Name == "مخزن مندوب التسوية المقسّمة");
        Assert.Equal(new decimal[] { 10, 5 }, prod.Choices.OrderByDescending(c => c.Available).Select(c => c.Available).ToArray());
        Assert.All(prod.Choices, c => Assert.DoesNotContain("؟", c.Name));
        Assert.Contains("متوفر 10", prod.Choices.Select(c => c.Display).First(d => d.Contains("شرنك")));

        var shrinkChoice = prod.Choices.Single(c => c.ItemId != 0 && c.Display.Contains("شرنك"));
        prod.LineChoice = shrinkChoice;
        Assert.Equal(10m, prod.LineQuantity);                                    // اختيار الصنف يملأ الكمية بالمتوفر
        prod.LineQuantity = 99;
        prod.AddReturnCommand.Execute(null);
        Assert.Contains(dialogs.Errors, e => e.Contains("أكبر من المتوفر"));
        dialogs.Errors.Clear();
        prod.LineQuantity = 3;
        prod.AddReturnCommand.Execute(null);
        Assert.Empty(dialogs.Errors);
        Assert.Equal(7m, prod.LineQuantity);                                     // المتبقي بعد الإضافة

        var cartonChoice = prod.Choices.Single(c => c.Display.Contains("كارتون"));
        prod.FreeChoice = cartonChoice;
        prod.FreeQuantity = 1;
        prod.AddFreeCommand.Execute(null);                                       // بلا سبب
        Assert.Contains(dialogs.Errors, e => e.Contains("سبب"));
        dialogs.Errors.Clear();
        prod.FreeReason = "هدية افتتاح";
        prod.AddFreeCommand.Execute(null);
        Assert.Single(prod.FreeGoods);

        await prod.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        var settled = prod.History.First(r => r.RepName == "مندوب التسوية المقسّمة");
        Assert.Equal((RepSettlementStatus.PendingCash, 60m, 40m), (settled.Status, settled.ReturnedPieces, settled.FreePieces));
        Assert.Contains("بانتظار النقد", settled.StatusText);
        Assert.Contains("بانتظار النقد", prod.StatusMessage);

        // ---- المالية: تسوية النقد على التسوية نفسها ----
        var fin = shell.Open<FinanceModuleViewModel>(ModuleCode.Finance);
        var cash = fin.CashSettlement;
        await Open(fin, cash);
        cash.Van = cash.Vans.Single(v => v.Id == vanId);
        await cash.IdleAsync();
        Assert.Equal(8_000m, cash.WalletBalance);
        Assert.Contains("سوّى أمين المخزن", cash.ProductsText);
        Assert.Contains(cash.PendingCash, p => p.SettlementNumber == settled.SettlementNumber);
        cash.ExpenseAmount = 1_000;
        cash.ExpenseDescription = "وقود";
        cash.AddExpenseCommand.Execute(null);
        Assert.Equal(7_000m, cash.Expected);
        cash.ReceivedCash = 0;      // النقد لا يدخل صندوقًا هنا كي لا تتأثر اختبارات الصناديق المشتركة (يغطيه اختبار الخدمة)
        await cash.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        var done = cash.History.Single(r => r.SettlementNumber == settled.SettlementNumber);
        Assert.Equal((RepSettlementStatus.Confirmed, 7_000m, 0m, 7_000m), (done.Status, done.ExpectedCash, done.ReceivedCash, done.Difference));
        Assert.DoesNotContain(cash.PendingCash, p => p.SettlementNumber == settled.SettlementNumber);
        Assert.Equal(7_000m, cash.WalletBalance);                                // الفرق في ذمة المندوب
    }
}
