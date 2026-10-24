using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.ViewModels.Sales;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>شاشة «مرتجع زبون»: زبون أعاد شرنكين منها واحد تالف، خصم من دينه، ثم الطباعة.</summary>
[Collection("app")]
public class CustomerReturnScreenTests
{
    private readonly AppFixture _f;
    public CustomerReturnScreenTests(AppFixture f) => _f = f;

    [Fact]
    public async Task Return_from_screen_credits_customer_and_prints()
    {
        int customerId, fgId;
        await using (var db = _f.NewDb())
        {
            var branch = await db.Branches.FirstAsync();
            var admin = await db.Users.FirstAsync(u => u.Username == AppFixture.AdminUser);
            var fg = await db.Warehouses.FirstOrDefaultAsync(w => w.WarehouseType == WarehouseType.FinishedGoods && w.IsActive);
            if (fg is null) db.Warehouses.Add(fg = new Warehouse { BranchId = branch.Id, Name = "مخزن مرتجع الشاشة", WarehouseType = WarehouseType.FinishedGoods });
            if (!await db.Warehouses.AnyAsync(w => w.WarehouseType == WarehouseType.Damaged && w.IsActive))
                db.Warehouses.Add(new Warehouse { BranchId = branch.Id, Name = "تالف مرتجع الشاشة", WarehouseType = WarehouseType.Damaged, IsSellableStock = false });
            var water = new Item { ItemCode = "CRS-S20", ItemName = "ماء مرتجع الشاشة شرنك", SalePrice = 250, CostPrice = 100 };
            var customer = new Customer { Name = "زبون مرتجع الشاشة" };
            db.AddRange(water, customer);
            await db.SaveChangesAsync();
            var shrink = new ItemPackagingLevel { ItemId = water.Id, LevelName = "شرنك", EquivalentBaseUnits = 20 };
            db.ItemPackagingLevels.AddRange(new ItemPackagingLevel { ItemId = water.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 }, shrink);
            db.StockTransactions.Add(new StockTransaction { ItemId = water.Id, WarehouseId = fg.Id, QuantityBaseUnits = 500, UnitCost = 100,
                                                            TransactionType = StockTransactionType.Receipt, CreatedByUserId = admin.Id });
            await db.SaveChangesAsync();
            var sales = new SalesService(db);
            var (_, id) = await sales.CreateInvoiceAsync(new SalesInvoiceHeaderInput(customer.Id, fg.Id, DateTime.Today, InvoicePaymentMethod.Credit), admin.Id);
            Assert.True((await sales.AddLineAsync(id!.Value, new SalesInvoiceLineInput(water.Id, shrink.Id, 2), admin.Id)).Success);
            Assert.True((await sales.PostInvoiceAsync(id.Value, admin.Id)).result.Success);
            (customerId, fgId) = (customer.Id, fg.Id);
        }

        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var salesModule = shell.Open<SalesModuleViewModel>(ModuleCode.Sales);
        var screen = salesModule.Returns;
        salesModule.SelectedTab = screen;
        await salesModule.IdleAsync();
        await screen.IdleAsync();

        screen.Customer = screen.Customers.Single(c => c.Id == customerId);
        screen.Warehouse = screen.Warehouses.Single(w => w.Id == fgId);
        var line = screen.Lines.Single();
        line.Product = screen.Products.Single(p => p.ItemCode == "CRS-S20");
        Assert.Equal("شرنك", line.Level!.LevelName);                  // العبوة أولًا
        line.Quantity = 2;
        line.Damaged = 1;
        await screen.SaveCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("سبب المرتجع"));
        screen.Reason = "تسرب في العبوة";
        await screen.SaveCommand.ExecuteAsync();
        Assert.Contains("10,000", screen.StatusMessage);
        var row = screen.Recent.First(r => r.CustomerName == "زبون مرتجع الشاشة");
        Assert.Equal("خصم من الدين", row.SettlementText);
        Assert.Contains("تالف 1", row.Items);

        screen.PrintCommand.Execute(row);
        Assert.StartsWith("مرتجع زبون", dialogs.Reports.Last().Title);
    }
}
