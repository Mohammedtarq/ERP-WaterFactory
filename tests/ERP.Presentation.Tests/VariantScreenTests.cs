using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.ViewModels.Production;
using ERP.Presentation.ViewModels.Sales;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>
/// أمر يومي واحد بكل الأصناف كما يستخدمه المستخدم: متغير جديد من المعالج، وقالب أمر اليوم وتكراره،
/// وفحص المواد، ثم البيع يعرض المتغير والمتاح للعميل، وشاشة متغيرات المنتج.
/// </summary>
[Collection("app")]
public class VariantScreenTests
{
    private readonly AppFixture _f;
    public VariantScreenTests(AppFixture f) => _f = f;

    private static async Task Open(ModuleViewModel module, SectionViewModel section)
    {
        module.SelectedTab = section;
        await module.IdleAsync();
        await section.IdleAsync();
    }

    /// <summary>منتج بقائمة مواد بأدوار (امبولة، غطاء، لاصق) ومواد في مخزن مواد أولية.</summary>
    private async Task<(Item water, ItemPackagingLevel shrink, ItemPackagingLevel carton, Customer restaurant)> ArrangeAsync()
    {
        await using var db = _f.NewDb();
        var branch = await db.Branches.FirstAsync();
        var raw = await db.Warehouses.FirstOrDefaultAsync(w => w.WarehouseType == WarehouseType.RawMaterial);
        if (raw is null) db.Warehouses.Add(raw = new Warehouse { BranchId = branch.Id, Name = "مخزن المواد الأولية", WarehouseType = WarehouseType.RawMaterial, IsSellableStock = false });
        var admin = await db.Users.FirstAsync(u => u.Username == AppFixture.AdminUser);
        if (!await db.RolePermissions.AnyAsync(p => p.RoleId == admin.RoleId && p.ModuleCode == SpecialPermission.ReservedStock))
            db.RolePermissions.Add(new RolePermission { RoleId = admin.RoleId, ModuleCode = SpecialPermission.ReservedStock, CanView = true });
        Item Raw(string code, string name) => new() { ItemCode = code, ItemName = name, SourcingMethod = SourcingMethod.Purchased, CostPrice = 10 };
        var pre = Raw("SV-PRE", "امبولة شاشة"); var cap = Raw("SV-CAP", "سدادة شاشة"); var lbl = Raw("SV-LBL", "ليبل الرحمة شاشة");
        var water = new Item { ItemCode = "SV-W", ItemName = "ماء الرحمة شاشة", SalePrice = 250 };
        var restaurant = new Customer { Name = "مطعم الحسون شاشة" };
        db.Items.AddRange(pre, cap, lbl, water);
        db.Customers.Add(restaurant);
        await db.SaveChangesAsync();
        foreach (var r in new[] { pre, cap, lbl })
        {
            db.ItemPackagingLevels.Add(new ItemPackagingLevel { ItemId = r.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 });
            db.StockTransactions.Add(new StockTransaction { ItemId = r.Id, WarehouseId = raw.Id, QuantityBaseUnits = 2_000, UnitCost = 10,
                                                            TransactionType = StockTransactionType.Receipt, CreatedByUserId = admin.Id });
        }
        var piece = new ItemPackagingLevel { ItemId = water.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 };
        var shrink = new ItemPackagingLevel { ItemId = water.Id, LevelName = "شرنك", ContainsQuantity = 6, EquivalentBaseUnits = 6 };
        var carton = new ItemPackagingLevel { ItemId = water.Id, LevelName = "كارتون", ContainsQuantity = 12, EquivalentBaseUnits = 12 };
        db.ItemPackagingLevels.AddRange(piece, shrink, carton);
        var bom = new BillOfMaterials { FinishedItemId = water.Id };
        bom.Lines.Add(new BOMLine { RawMaterialItemId = pre.Id, QuantityPerUnit = 1, ComponentRole = "امبولة" });
        bom.Lines.Add(new BOMLine { RawMaterialItemId = cap.Id, QuantityPerUnit = 1, ComponentRole = "غطاء" });
        bom.Lines.Add(new BOMLine { RawMaterialItemId = lbl.Id, QuantityPerUnit = 1, ComponentRole = "لاصق" });
        db.BillOfMaterials.Add(bom);
        await db.SaveChangesAsync();
        return (water, shrink, carton, restaurant);
    }

    [Fact]
    public async Task Variant_wizard_daily_template_sales_picker_and_variants_screen()
    {
        var (water, shrink, carton, restaurant) = await ArrangeAsync();
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var prod = shell.Open<ProductionModuleViewModel>(ModuleCode.Production);

        // 1) متغير جديد بخطوة واحدة: المطعم ← ليبل جديد باسمه
        var recipes = prod.Section<CustomRecipesSectionViewModel>();
        await Open(prod, recipes);
        recipes.OpenWizardCommand.Execute(null);
        Assert.True(recipes.IsWizardOpen);
        recipes.WizardProduct = recipes.FinishedItems.Single(i => i.Id == water.Id);
        await recipes.IdleAsync();
        Assert.Equal(new[] { "امبولة", "غطاء", "لاصق" }, recipes.WizardRoles.Select(r => r.Role).OrderBy(r => r).ToArray());
        await recipes.CreateVariantCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("اسم المتغير"));
        dialogs.Errors.Clear();
        recipes.WizardCustomer = recipes.WizardCustomers.Single(c => c.Id == restaurant.Id);
        Assert.Equal(restaurant.Name, recipes.WizardName);
        var label = recipes.WizardRoles.Single(r => r.Role == "لاصق");
        label.CreateNew = true;
        Assert.Equal($"لاصق {restaurant.Name}", label.NewItemName);
        await recipes.CreateVariantCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.False(recipes.IsWizardOpen);
        Assert.Equal(restaurant.Name, recipes.SelectedRecipe?.Name);
        await recipes.IdleAsync();
        Assert.Contains(recipes.Lines, l => l.ComponentName == $"لاصق {restaurant.Name}" && l.ReplacesName == "ليبل الرحمة شاشة");
        await using (var db = _f.NewDb())
        {
            var newLabel = await db.Items.SingleAsync(i => i.ItemName == $"لاصق {restaurant.Name}");
            var rawWh = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.RawMaterial);
            var admin = await db.Users.FirstAsync(u => u.Username == AppFixture.AdminUser);
            db.StockTransactions.Add(new StockTransaction { ItemId = newLabel.Id, WarehouseId = rawWh.Id, QuantityBaseUnits = 500, UnitCost = 10,
                                                            TransactionType = StockTransactionType.Receipt, CreatedByUserId = admin.Id });
            await db.SaveChangesAsync();
        }

        // 2) إنتاج اليوم: الأساسي شرنك + المطعم كارتون، ثم قالب وفحص مواد وتسجيل وتكرار
        var daily = prod.Daily;
        await Open(prod, daily);
        await daily.LoadAsync();
        var basicLine = daily.Lines[0];
        basicLine.Product = daily.Products.Single(p => p.Id == water.Id);
        Assert.Same(DailyProductionSectionViewModel.Basic, basicLine.Recipe);
        Assert.Equal(DailyProductionSectionViewModel.Basic, basicLine.Recipes[0]);
        basicLine.Level = basicLine.Levels.Single(l => l.Id == shrink.Id);
        basicLine.Packs = 10;
        daily.AddLineCommand.Execute(null);
        var restaurantLine = daily.Lines[1];
        restaurantLine.Product = basicLine.Product;
        restaurantLine.Recipe = restaurantLine.Recipes.Single(r => r.Name == restaurant.Name);
        restaurantLine.Level = restaurantLine.Levels.Single(l => l.Id == carton.Id);
        restaurantLine.Packs = 5;
        Assert.Equal(120m, daily.TotalPieces);

        daily.TemplateName = "أمر الشاشة";
        await daily.SaveTemplateCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Equal("أمر الشاشة", daily.SelectedTemplate?.Name);

        await daily.PreviewCommand.ExecuteAsync();
        Assert.False(daily.HasShortage);
        Assert.Equal(60m, daily.Needs.Single(n => n.ItemName == $"لاصق {restaurant.Name}").Required);
        Assert.Equal(60m, daily.Needs.Single(n => n.ItemName == "ليبل الرحمة شاشة").Required);

        dialogs.ConfirmAnswer = true;
        await daily.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Equal(new[] { null, restaurant.Name }, daily.LastResult.Select(r => r.RecipeName).ToArray());
        Assert.Single(daily.Lines);

        await daily.ApplyTemplateCommand.ExecuteAsync();
        Assert.Equal(2, daily.Lines.Count);
        Assert.Equal(restaurant.Name, daily.Lines[1].Recipe!.Name);
        daily.Lines.Clear();
        await daily.RepeatLastCommand.ExecuteAsync();
        Assert.Equal(new[] { (shrink.Id, 10m, (int?)null), (carton.Id, 5m, (int?)daily.Lines[1].RecipeId) },
                     daily.Lines.Select(l => (l.Level!.Id, l.Packs, l.RecipeId)).ToArray());
        Assert.NotNull(daily.Lines[1].RecipeId);
        await daily.DeleteTemplateCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.DoesNotContain(daily.Templates, t => t.Name == "أمر الشاشة");

        // 3) البيع (موظف بلا صلاحية المحجوز): التشغيلة تحمل اسم المتغير، والمتاح للزبون العام لا يشمل محجوز المطعم
        var (clerkShell, _) = await _f.LoginAsync(AppFixture.ClerkUser, AppFixture.ClerkPassword);
        var sales = clerkShell.Open<SalesModuleViewModel>(ModuleCode.Sales);
        var inv = sales.Invoice;
        await Open(sales, inv);
        inv.Customer = inv.Customers.Single(c => c.Id == _f.DirectId);
        inv.Warehouse = inv.Warehouses.Single(w => w.Id == _f.MainWarehouseId);
        inv.LineItem = inv.ItemsLookup.Single(i => i.Id == water.Id);
        await inv.IdleAsync();
        Assert.Contains(inv.BatchOptions, b => b.RecipeId != null && b.Label.Contains(restaurant.Name));
        Assert.Contains(inv.BatchOptions, b => b.BatchId != null && b.ShortLabel.Contains(restaurant.Name));
        Assert.Equal(60m, inv.LineAvailable);
        inv.Customer = inv.Customers.Single(c => c.Id == restaurant.Id);
        await inv.IdleAsync();
        Assert.Equal(120m, inv.LineAvailable);
        inv.LineItem = null;
        inv.Customer = null;

        // 4) متغيرات المنتج: الأساسي والمحجوز منفصلان، والتشغيلات بأرصدتها
        var variants = prod.Section<VariantStockSectionViewModel>();
        await Open(prod, variants);
        Assert.Contains(variants.Rows, r => r.ItemName == water.ItemName && r.RecipeId == null && r.Balance == 60 && r.BalanceText == "5 كارتون");   // بالوحدة الأكبر
        Assert.Contains(variants.Rows, r => r.ItemName == water.ItemName && r.Kind == $"محجوز لـ {restaurant.Name}" && r.Balance == 60);
        Assert.True(variants.CanCorrect);
        variants.SelectedBatch = variants.Batches.First(b => b.ItemId == water.Id && b.RecipeId == null);
        Assert.Same(DailyProductionSectionViewModel.Basic, variants.NewVariant);
        variants.NewVariant = variants.VariantOptions.Single(r => r.Name == restaurant.Name);
        await variants.CorrectCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("سبب التصحيح"));
        dialogs.Errors.Clear();
        variants.Reason = "تجربة التصحيح";
        await variants.CorrectCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.DoesNotContain(variants.Rows, r => r.ItemName == water.ItemName && r.RecipeId == null && r.Balance > 0);
        variants.PrintCommand.Execute(null);
        Assert.StartsWith("متغيرات المنتج", dialogs.Reports.Last().Title);
    }
}
