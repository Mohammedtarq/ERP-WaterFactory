using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.ViewModels.Reps;
using ERP.Presentation.ViewModels.Shell;
using ERP.Presentation.ViewModels.Warehouse;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>
/// «سيارات المندوبين»: بطاقة لكل مندوب بدل تبويب لكل سيارة — حمولة اليوم بوحداتها، الرصيد، حالة التسوية،
/// وتفاصيل السيارة تحت البطاقات؛ والمخازن لم تعد تفتح تبويبًا لكل سيارة.
/// </summary>
[Collection("app")]
public class RepVansScreenTests
{
    private readonly AppFixture _f;
    public RepVansScreenTests(AppFixture f) => _f = f;

    private static async Task Open(ModuleViewModel module, SectionViewModel section)
    {
        module.SelectedTab = section;
        await module.IdleAsync();
        await section.IdleAsync();
    }

    [Fact]
    public async Task One_board_for_all_vans_with_status_load_and_detail()
    {
        int loadedVan, idleVan;
        await using (var db = _f.NewDb())
        {
            var branch = await db.Branches.FirstAsync();
            var admin = await db.Users.FirstAsync(u => u.Username == AppFixture.AdminUser);
            var store = new Warehouse { BranchId = branch.Id, Name = "مخزن لوحة السيارات", WarehouseType = WarehouseType.FinishedGoods };
            var water = new Item { ItemCode = "BV-W", ItemName = "ماء لوحة السيارات", SalePrice = 250 };
            var ali = new Employee { FullName = "مندوب لوحة علي", IsSalesRep = true, BaseSalary = 500_000 };
            var hasan = new Employee { FullName = "مندوب لوحة حسن", IsSalesRep = true, BaseSalary = 500_000 };
            db.AddRange(store, water, ali, hasan);
            await db.SaveChangesAsync();
            var shrink = new ItemPackagingLevel { ItemId = water.Id, LevelName = "شرنك", EquivalentBaseUnits = 20 };
            var carton = new ItemPackagingLevel { ItemId = water.Id, LevelName = "كارتون", EquivalentBaseUnits = 40 };
            db.ItemPackagingLevels.AddRange(new ItemPackagingLevel { ItemId = water.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 }, shrink, carton);
            db.StockTransactions.Add(new StockTransaction { ItemId = water.Id, WarehouseId = store.Id, QuantityBaseUnits = 2_000, UnitCost = 100,
                                                            TransactionType = StockTransactionType.Receipt, CreatedByUserId = admin.Id });
            var vanA = new Warehouse { BranchId = branch.Id, Name = "سيارة لوحة علي", WarehouseType = WarehouseType.RepVan, OwnerEmployeeId = ali.Id };
            var vanH = new Warehouse { BranchId = branch.Id, Name = "سيارة لوحة حسن", WarehouseType = WarehouseType.RepVan, OwnerEmployeeId = hasan.Id };
            db.Warehouses.AddRange(vanA, vanH);
            db.RepTerritories.Add(new RepTerritory { EmployeeId = ali.Id, TerritoryName = "الزبير" });
            await db.SaveChangesAsync();

            var ops = new RepOperationsService(db);
            var (created, order) = await ops.CreateLoadOrderAsync(vanA.Id, store.Id, DateTime.Today, new[]
            {
                new RepLoadLineInput(water.Id, shrink.Id, 10),
                new RepLoadLineInput(water.Id, carton.Id, 5),
            }, null, admin.Id);
            Assert.True(created.Success, created.ErrorMessage);
            Assert.True((await ops.PrepareLoadOrderAsync(order!.Id, null, admin.Id)).result.Success);
            (loadedVan, idleVan) = (vanA.Id, vanH.Id);
        }

        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);

        // المخازن: لا تبويب لكل سيارة، بل شاشة واحدة
        var wh = shell.Open<WarehouseModuleViewModel>(ModuleCode.Warehouse);
        await wh.RefreshWorkspacesAsync();
        Assert.DoesNotContain(wh.Workspaces, w => w.WarehouseType == WarehouseType.RepVan);
        Assert.Contains(wh.Vans, wh.Tabs);

        // المندوبون ← سيارات المندوبين: بطاقة لكل مندوب
        var reps = shell.Open<RepsModuleViewModel>(ModuleCode.Reps);
        var board = reps.Vans;
        await Open(reps, board);
        var aliCard = board.Cards.Single(c => c.VanWarehouseId == loadedVan);
        var hasanCard = board.Cards.Single(c => c.VanWarehouseId == idleVan);
        Assert.Equal(RepVanStatus.Pending, aliCard.Status);
        Assert.Equal("5 كارتون ماء لوحة السيارات، 10 شرنك ماء لوحة السيارات", aliCard.TodayLoadText);   // الأكبر أولًا
        Assert.Equal(400m, aliCard.BalancePieces);
        Assert.Equal("الزبير", aliCard.Territories);
        Assert.Equal(RepVanStatus.Idle, hasanCard.Status);

        // فلتر «تحتاج تسوية» والبحث
        board.Filter = board.Filters.Single(f => f.Value == RepVanFilter.NeedsSettlement);
        Assert.Contains(board.Cards, c => c.VanWarehouseId == loadedVan);
        Assert.DoesNotContain(board.Cards, c => c.VanWarehouseId == idleVan);
        board.Filter = board.Filters[0];
        board.Search = "حسن";
        Assert.Equal(idleVan, board.Cards.Single(c => c.RepName.Contains("لوحة")).VanWarehouseId);
        board.Search = "";

        // الضغط على البطاقة يعرض شاشة السيارة نفسها تحتها
        board.SelectedCard = board.Cards.Single(c => c.VanWarehouseId == loadedVan);
        await board.IdleAsync();
        Assert.NotNull(board.Detail);
        Assert.Equal(loadedVan, board.Detail!.WarehouseId);
        await board.Detail.IdleAsync();
        Assert.Contains(board.Detail.Balances, b => b.ItemCode == "BV-W" && b.Quantity == 400);

        board.PrintCommand.Execute(null);
        Assert.StartsWith("سيارات المندوبين", dialogs.Reports.Last().Title);
        Assert.Empty(dialogs.Errors);
    }
}
