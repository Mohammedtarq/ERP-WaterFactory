using ERP.Data.ProjectDb.Entities;
using ERP.Presentation.ViewModels.HR;
using ERP.Presentation.ViewModels.Reps;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>
/// حافز المندوب كما يستخدمه المدير: مبلغ الشرنك والكارتون من الموارد البشرية،
/// ثم جدول مبيعات المندوب والراجع بالعدد والمبلغ المتجمع في قسم المندوبين، وطباعته.
/// </summary>
[Collection("app")]
public class RepIncentiveScreenTests
{
    private readonly AppFixture _f;
    public RepIncentiveScreenTests(AppFixture f) => _f = f;

    private static async Task Open(ModuleViewModel module, SectionViewModel section)
    {
        module.SelectedTab = section;
        await module.IdleAsync();
        await section.IdleAsync();
    }

    [Fact]
    public async Task Rates_by_pack_then_monthly_table_of_loaded_returned_and_amount()
    {
        int repId;
        await using (var db = _f.NewDb())
        {
            var branch = await db.Branches.FirstAsync();
            var admin = await db.Users.FirstAsync(u => u.Username == AppFixture.AdminUser);
            var water = new Item { ItemCode = "RI-W", ItemName = "ماء حافز شاشة", SalePrice = 250 };
            db.Items.Add(water);
            var rep = new Employee { FullName = "مندوب حافز شاشة", IsSalesRep = true, BaseSalary = 500_000 };
            db.Employees.Add(rep);
            var wh = new Warehouse { BranchId = branch.Id, Name = "مخزن حافز شاشة", WarehouseType = WarehouseType.FinishedGoods };
            db.Warehouses.Add(wh);
            await db.SaveChangesAsync();
            var shrink = new ItemPackagingLevel { ItemId = water.Id, LevelName = "شرنك", EquivalentBaseUnits = 20 };
            var carton = new ItemPackagingLevel { ItemId = water.Id, LevelName = "كارتون", EquivalentBaseUnits = 40 };
            db.ItemPackagingLevels.AddRange(new ItemPackagingLevel { ItemId = water.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 }, shrink, carton);
            await db.SaveChangesAsync();
            var n = 0;
            void Doc(StockDocumentType type, decimal shrinks, decimal cartons)
            {
                var d = new StockDocument { DocumentNumber = $"RI-{++n}", DocumentType = type, WarehouseId = wh.Id, DocumentDate = DateTime.Today,
                                            RepEmployeeId = rep.Id, CreatedByUserId = admin.Id };
                d.Lines.Add(new StockDocumentLine { ItemId = water.Id, PackagingLevelId = shrink.Id, QuantityInLevel = shrinks, QuantityBaseUnits = shrinks * 20 });
                d.Lines.Add(new StockDocumentLine { ItemId = water.Id, PackagingLevelId = carton.Id, QuantityInLevel = cartons, QuantityBaseUnits = cartons * 40 });
                db.StockDocuments.Add(d);
            }
            Doc(StockDocumentType.RepLoad, 30, 12);
            Doc(StockDocumentType.RepReturn, 5, 2);
            await db.SaveChangesAsync();
            repId = rep.Id;
        }

        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);

        // 1) الموارد البشرية ← حافز المندوب: مبلغ لكل شرنك ولكل كارتون
        var hr = shell.Open<HrModuleViewModel>(ModuleCode.HR);
        var ratesScreen = hr.Section<RepIncentiveRatesSectionViewModel>();
        await Open(hr, ratesScreen);
        var mine = ratesScreen.Rows.Where(r => r.ItemName == "ماء حافز شاشة").ToList();
        Assert.Equal(new[] { "كارتون", "شرنك" }, mine.Select(r => r.LevelName));
        mine[0].Rate = 300;
        mine[1].Rate = 150;
        await ratesScreen.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Contains(ratesScreen.Rows, r => r.ItemName == "ماء حافز شاشة" && r.LevelName == "كارتون" && r.Rate == 300);
        Assert.All(ratesScreen.Rows, r => Assert.False(r.IsChanged));

        // إضافة عبوة غير ظاهرة (القطعة) من أعلى الشاشة، والبحث، ثم إلغاؤها بصفر (ملاحظة التجربة 8)
        ratesScreen.NewItem = ratesScreen.Products.Single(p => p.ItemName == "ماء حافز شاشة");
        await ratesScreen.IdleAsync();
        ratesScreen.NewLevel = ratesScreen.NewLevels.Single(l => l.LevelName == "قطعة");
        ratesScreen.NewRate = 10;
        ratesScreen.AddCommand.Execute(null);
        var piece = ratesScreen.Rows.Single(r => r.ItemName == "ماء حافز شاشة" && r.LevelName == "قطعة");
        Assert.Equal("جديد — لم يُحفظ", piece.StateText);
        ratesScreen.Filter = "حافز شاشة";
        Assert.All(ratesScreen.VisibleRows, r => Assert.Contains("حافز شاشة", r.ItemName));
        await ratesScreen.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Contains(ratesScreen.Rows, r => r.LevelName == "قطعة" && r.ItemName == "ماء حافز شاشة" && r.Rate == 10 && r.StateText == "مفعّل");
        ratesScreen.Rows.Single(r => r.LevelName == "قطعة" && r.ItemName == "ماء حافز شاشة").Rate = 0;
        await ratesScreen.SaveCommand.ExecuteAsync();
        Assert.DoesNotContain(ratesScreen.Rows, r => r.LevelName == "قطعة" && r.ItemName == "ماء حافز شاشة");

        // 2) المندوبون ← حوافز المندوبين: المحمّل والراجع والمباع بالعدد، والمبلغ المتجمع
        var reps = shell.Open<RepsModuleViewModel>(ModuleCode.Reps);
        var table = reps.Incentives;
        await Open(reps, table);
        table.Rep = table.RepOptions.Single(o => o.Value == repId);
        await table.IdleAsync();
        var shrinkRow = table.Rows.Single(r => r.LevelName == "شرنك");
        var cartonRow = table.Rows.Single(r => r.LevelName == "كارتون");
        Assert.Equal((30m, 5m, 25m, 3_750m), (shrinkRow.Loaded, shrinkRow.Returned, shrinkRow.Net, shrinkRow.Amount));
        Assert.Equal((12m, 2m, 10m, 3_000m), (cartonRow.Loaded, cartonRow.Returned, cartonRow.Net, cartonRow.Amount));
        Assert.Equal(6_750m, table.Totals.Single().Amount);
        Assert.Equal(6_750m, table.TotalAmount);

        table.PrintCommand.Execute(null);
        var report = dialogs.Reports.Last();
        Assert.StartsWith("حوافز المندوبين", report.Title);
        Assert.Equal(2, report.Rows.Count);
        Assert.Empty(dialogs.Errors);
    }
}
