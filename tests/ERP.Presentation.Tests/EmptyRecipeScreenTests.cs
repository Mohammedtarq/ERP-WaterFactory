using ERP.Data.ProjectDb.Entities;
using ERP.Presentation.ViewModels.Production;
using ERP.Presentation.ViewModels.Shell;
using ERP.Presentation.ViewModels.Warehouse;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>صنف أُضيفت له وصفة بالخطأ يخرج من قوائم الإنتاج بمجرد حذف مكوّنات وصفته (لا يبقى بوصفة فارغة).</summary>
[Collection("app")]
public class EmptyRecipeScreenTests
{
    private readonly AppFixture _f;
    public EmptyRecipeScreenTests(AppFixture f) => _f = f;

    private static async Task Open(ModuleViewModel module, SectionViewModel section)
    {
        module.SelectedTab = section;
        await module.IdleAsync();
        await module.LastActivation;
        await section.IdleAsync();
    }

    [Fact]
    public async Task Item_with_only_a_mistaken_recipe_leaves_production_lists_after_its_lines_are_deleted()
    {
        int itemId;
        await using (var db = _f.NewDb())
        {
            var raw = new Item { ItemCode = "RM-MIS", ItemName = "مادة اختبار الوصفة الخاطئة", SourcingMethod = SourcingMethod.Purchased };
            var wrong = new Item { ItemCode = "MIS-LBL", ItemName = "ليبل-اختبار الوصفة الخاطئة", SourcingMethod = SourcingMethod.Manufactured };
            db.Items.AddRange(raw, wrong);
            await db.SaveChangesAsync();
            var bom = new BillOfMaterials { FinishedItemId = wrong.Id };
            db.BillOfMaterials.Add(bom);
            await db.SaveChangesAsync();
            db.BOMLines.Add(new BOMLine { BOMId = bom.Id, RawMaterialItemId = raw.Id, QuantityPerUnit = 1 });
            await db.SaveChangesAsync();
            itemId = wrong.Id;
        }
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var prod = shell.Open<ProductionModuleViewModel>(ModuleCode.Production);
        await Open(prod, prod.Orders);
        Assert.Contains(prod.Orders.FinishedItems, i => i.Id == itemId);   // يظهر لأن له وصفة

        var wh = shell.Open<WarehouseModuleViewModel>(ModuleCode.Warehouse);
        var bomTab = wh.Section<BomSectionViewModel>();
        await Open(wh, bomTab);
        bomTab.FinishedItem = bomTab.AllItems.Single(i => i.Id == itemId);
        await bomTab.IdleAsync();
        Assert.Single(bomTab.Lines);
        await bomTab.DeleteLineCommand.ExecuteAsync(bomTab.Lines[0]);
        Assert.Empty(dialogs.Errors);
        Assert.Empty(bomTab.Lines);

        await Open(prod, prod.Orders);
        await prod.Orders.LoadAsync();
        Assert.DoesNotContain(prod.Orders.FinishedItems, i => i.Id == itemId);
        await using (var db = _f.NewDb())
            Assert.False(await db.BillOfMaterials.AnyAsync(b => b.FinishedItemId == itemId && b.IsActive));
    }
}
