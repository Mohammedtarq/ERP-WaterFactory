using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.ViewModels.Production;
using ERP.Presentation.ViewModels.Reps;
using ERP.Presentation.ViewModels.Sales;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>
/// ملاحظات التجربة الأولى (الدفعة أ): الشاشة تعود نظيفة عند الخروج منها دون حفظ وزر «تفريغ الشاشة»،
/// دين العميل يظهر في تحصيل المحفظة، أمر الشراء لمخزن المواد الأولية فقط، وأقسام الإنتاج مرقّمة بتسلسل العمل.
/// </summary>
[Collection("app")]
public class TrialFixesBatchATests
{
    private readonly AppFixture _f;
    public TrialFixesBatchATests(AppFixture f) => _f = f;

    [Fact]
    public async Task Screens_clear_on_leave_and_on_clear_button_wallet_shows_debt_and_production_steps_are_numbered()
    {
        int customerId;
        await using (var db = _f.NewDb())
        {
            var branch = await db.Branches.FirstAsync();
            var admin = await db.Users.FirstAsync(u => u.Username == AppFixture.AdminUser);
            var fg = await db.Warehouses.FirstOrDefaultAsync(w => w.WarehouseType == WarehouseType.FinishedGoods && w.IsActive);
            if (fg is null) db.Warehouses.Add(fg = new Warehouse { BranchId = branch.Id, Name = "مخزن الدفعة أ", WarehouseType = WarehouseType.FinishedGoods });
            var water = new Item { ItemCode = "TFA-S20", ItemName = "ماء الدفعة أ شرنك", SalePrice = 250, CostPrice = 100 };
            var customer = new Customer { Name = "زبون الدفعة أ" };
            var supplier = new Supplier { Name = "مورد الدفعة أ" };
            db.AddRange(water, customer, supplier);
            await db.SaveChangesAsync();
            var shrink = new ItemPackagingLevel { ItemId = water.Id, LevelName = "شرنك", EquivalentBaseUnits = 20 };
            db.ItemPackagingLevels.AddRange(new ItemPackagingLevel { ItemId = water.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 }, shrink);
            db.StockTransactions.Add(new StockTransaction { ItemId = water.Id, WarehouseId = fg.Id, QuantityBaseUnits = 200, UnitCost = 100,
                                                            TransactionType = StockTransactionType.Receipt, CreatedByUserId = admin.Id });
            await db.SaveChangesAsync();
            var sales = new SalesService(db);
            var (_, id) = await sales.CreateInvoiceAsync(new SalesInvoiceHeaderInput(customer.Id, fg.Id, DateTime.Today, InvoicePaymentMethod.Credit), admin.Id);
            Assert.True((await sales.AddLineAsync(id!.Value, new SalesInvoiceLineInput(water.Id, shrink.Id, 2), admin.Id)).Success);
            Assert.True((await sales.PostInvoiceAsync(id.Value, admin.Id)).result.Success);
            customerId = customer.Id;

            // (6) أمر الشراء لمخزن المواد الأولية فقط: الخدمة ترفض مخزن المنتج التام
            var po = await new SupplierPurchasingService(db).CreatePurchaseOrderAsync(supplier.Id, fg.Id, DateTime.Today, null, SupplierPaymentTerms.Cash, 0,
                new List<PurchaseOrderLineInput> { new(water.Id, 10, 100) }, admin.Id);
            Assert.False(po.Success);
            Assert.Contains("المواد الأولية", po.ErrorMessage);
        }

        var (shell, _) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);

        // (5) الخروج من الشاشة إلى تبويب آخر دون حفظ: تعود نظيفة
        var salesModule = shell.Open<SalesModuleViewModel>(ModuleCode.Sales);
        var deposits = salesModule.Deposits;
        salesModule.SelectedTab = deposits;
        await salesModule.LastActivation;
        await deposits.IdleAsync();
        deposits.Customer = deposits.Customers.Single(c => c.Id == customerId);
        deposits.Amount = 5_000;
        deposits.Notes = "لم يُحفظ";
        salesModule.SelectedTab = salesModule.Returns;
        await salesModule.LastActivation;
        salesModule.SelectedTab = deposits;
        await salesModule.LastActivation;
        await deposits.IdleAsync();
        Assert.Null(deposits.Customer);
        Assert.Equal(0m, deposits.Amount);
        Assert.Null(deposits.Notes);
        Assert.Equal(CustomerDepositsSectionViewModel.DefaultPurpose, deposits.Purpose);

        // ... وكذلك الخروج إلى وحدة أخرى ثم الرجوع
        deposits.Customer = deposits.Customers.Single(c => c.Id == customerId);
        deposits.Amount = 7_000;
        var reps = shell.Open<RepsModuleViewModel>(ModuleCode.Reps);
        Assert.Same(salesModule, shell.Open<SalesModuleViewModel>(ModuleCode.Sales));
        Assert.Same(deposits, salesModule.SelectedTab);
        await salesModule.LastActivation;
        await deposits.IdleAsync();
        Assert.Null(deposits.Customer);
        Assert.Equal(0m, deposits.Amount);

        // زر «تفريغ الشاشة» أعلى الوحدة
        Assert.True(salesModule.CanClearCurrent);
        deposits.Amount = 3_000;
        deposits.Customer = deposits.Customers.Single(c => c.Id == customerId);
        await salesModule.ClearCurrentCommand.ExecuteAsync();
        await deposits.IdleAsync();
        Assert.Equal(0m, deposits.Amount);
        Assert.Null(deposits.Customer);

        // (18) تحصيل دين في محفظة المندوب: دين العميل يظهر فور اختياره
        reps = shell.Open<RepsModuleViewModel>(ModuleCode.Reps);
        var wallet = reps.Wallet;
        reps.SelectedTab = wallet;
        await reps.LastActivation;
        await wallet.IdleAsync();
        wallet.Action = wallet.Actions.Single(a => a.Value == WalletAction.Collection);
        Assert.Equal("", wallet.CustomerDebtText);
        wallet.Customer = wallet.Customers.Single(c => c.Id == customerId);
        await wallet.IdleAsync();
        Assert.True(wallet.CustomerDebt > 0);
        Assert.Contains("دين العميل الآن", wallet.CustomerDebtText);

        // (15) أقسام الإنتاج مرقّمة بتسلسل العمل: الأوامر ← إنتاج اليوم ← المكائن ← الفحص ← التعبئة ← المعلّق
        var production = shell.Open<ProductionModuleViewModel>(ModuleCode.Production);
        var steps = production.Tabs.OfType<SectionViewModel>().Where(s => s.Step is not null).ToList();
        Assert.Equal(new int?[] { 1, 2, 3, 4, 5, 6 }, steps.Select(s => s.Step).ToArray());
        Assert.Same(production.Orders, steps[0]);
        Assert.Same(production.Daily, steps[1]);
        Assert.Same(production.Qc, steps[3]);
        Assert.Same(production.Packing, steps[4]);
        Assert.Equal($"1. {production.Orders.Title}", production.Orders.DisplayTitle);

        await shell.IdleAllAsync();
        Assert.Empty(_f.Unhandled);
    }
}
