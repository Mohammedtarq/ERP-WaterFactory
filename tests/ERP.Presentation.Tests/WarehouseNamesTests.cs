using ERP.Data.ProjectDb.Entities;
using ERP.Presentation.ViewModels.Shell;
using ERP.Presentation.ViewModels.Warehouse;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>
/// اسم الصنف ظاهر في كل نوع مخزن (ملاحظة التجربة: أرصدة مخزن المواد الأولية بلا اسم مادة):
/// منتج تام ← العبوة | المحتوى | المتغير؛ مواد أولية أو مختلط (التالف) ← الاسم الكامل في كل صف؛
/// لا إجمالي يجمع وحدات مختلفة؛ كشف الحركة برصيد جارٍ لكل صنف؛ وكمية المستند باسم الصنف.
/// </summary>
[Collection("app")]
public class WarehouseNamesTests
{
    private readonly AppFixture _f;
    public WarehouseNamesTests(AppFixture f) => _f = f;

    private static async Task Open(ModuleViewModel module, SectionViewModel section)
    {
        module.SelectedTab = section;
        await module.IdleAsync();
        await module.LastActivation;
        await section.IdleAsync();
    }

    [Fact]
    public async Task Raw_and_mixed_warehouses_show_item_names_and_never_sum_different_units()
    {
        int rawWh, damagedWh;
        await using (var db = _f.NewDb())
        {
            var admin = await db.Users.Select(u => u.Id).FirstAsync();
            var branch = await db.Warehouses.Where(w => w.Id == _f.MainWarehouseId).Select(w => w.BranchId).FirstAsync();
            var rawStore = new Warehouse { BranchId = branch, Name = "مخزن المواد الأولية — أسماء", WarehouseType = WarehouseType.RawMaterial };
            var damagedStore = new Warehouse { BranchId = branch, Name = "مخزن التالف — أسماء", WarehouseType = WarehouseType.Damaged };
            db.Warehouses.AddRange(rawStore, damagedStore);
            var white = new Item { ItemCode = "WN-CAP-1", ItemName = "سدادة — أبيض (أسماء)", SourcingMethod = SourcingMethod.Purchased };
            var label = new Item { ItemCode = "WN-LBL-1", ItemName = "ليبل - مطعم ليالي (أسماء)", SourcingMethod = SourcingMethod.Purchased };
            db.Items.AddRange(white, label);
            await db.SaveChangesAsync();
            db.ItemPackagingLevels.AddRange(new ItemPackagingLevel { ItemId = white.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 },
                                            new ItemPackagingLevel { ItemId = label.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 });
            db.StockTransactions.AddRange(
                new StockTransaction { ItemId = white.Id, WarehouseId = rawStore.Id, QuantityBaseUnits = 4_449_330, TransactionType = StockTransactionType.Receipt, CreatedByUserId = admin },
                new StockTransaction { ItemId = label.Id, WarehouseId = rawStore.Id, QuantityBaseUnits = 16_000, TransactionType = StockTransactionType.Receipt, CreatedByUserId = admin },
                new StockTransaction { ItemId = label.Id, WarehouseId = rawStore.Id, QuantityBaseUnits = -400, TransactionType = StockTransactionType.ProductionConsume, CreatedByUserId = admin },
                // التالف مختلط: منتج تام ومادة أولية
                new StockTransaction { ItemId = _f.WaterItemId, WarehouseId = damagedStore.Id, QuantityBaseUnits = 24, TransactionType = StockTransactionType.Damaged, CreatedByUserId = admin },
                new StockTransaction { ItemId = white.Id, WarehouseId = damagedStore.Id, QuantityBaseUnits = 1_200, TransactionType = StockTransactionType.Damaged, CreatedByUserId = admin });
            await db.SaveChangesAsync();
            (rawWh, damagedWh) = (rawStore.Id, damagedStore.Id);
        }

        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var wh = shell.Open<WarehouseModuleViewModel>(ModuleCode.Warehouse);
        await wh.IdleAsync();
        await wh.RefreshWorkspacesAsync();

        // مخزن المواد الأولية: الاسم الكامل، لا أعمدة المنتج التام، ولا جمع قطع مواد مختلفة
        var raw = wh.Workspace(rawWh);
        await Open(wh, raw);
        Assert.True(raw.NameLayout);
        Assert.False(raw.PartsLayout);
        Assert.False(raw.MixedLayout);
        var cap = raw.Balances.Single(b => b.ItemCode == "WN-CAP-1");
        Assert.Equal("سدادة — أبيض (أسماء)", cap.DisplayName);
        Assert.Equal("4,449,330 قطعة", cap.Breakdown);                                 // بفواصل الآلاف
        Assert.Equal("2 مادة أولية", raw.TotalText);                                     // لا «4,465,330 قطعة»
        Assert.DoesNotContain("4,", raw.TotalText);
        Assert.EndsWith("مادة أولية", raw.TodayInText);                                // ولا «وارد اليوم» يجمع قطع مواد مختلفة

        // كشف الحركة لكل الأصناف: رصيد جارٍ لكل صنف على حدة واسمه ظاهر
        raw.LedgerItem = null;
        await raw.LoadReportsCommand.ExecuteAsync();
        await raw.IdleAsync();
        Assert.True(raw.LedgerNameLayout);
        var consumed = raw.Ledger.Single(r => r.ItemName == "ليبل - مطعم ليالي (أسماء)" && r.Out > 0);
        Assert.Equal("ليبل - مطعم ليالي (أسماء)", consumed.DisplayName);
        Assert.Equal("15,600 قطعة", consumed.BalanceText);
        Assert.All(raw.Ledger, r => Assert.False(string.IsNullOrEmpty(r.BalanceText)));

        // مخزن التالف المختلط: الاسم الموحد للتام والكامل للمادة، ووسم النوع
        var damaged = wh.Workspace(damagedWh);
        await Open(wh, damaged);
        Assert.True(damaged.NameLayout);
        Assert.True(damaged.MixedLayout);
        var water = damaged.Balances.Single(b => b.ItemId == _f.WaterItemId);
        Assert.True(water.IsFinished);
        Assert.Equal("تام", water.KindText);
        Assert.NotEqual(water.ItemName, water.DisplayName);                             // الاسم الموحد لا اسم الصنف الخام
        Assert.Equal("أولية", damaged.Balances.Single(b => b.ItemCode == "WN-CAP-1").KindText);
        Assert.EndsWith("+ 1 مادة أولية", damaged.TotalText);

        // القاعدة نفسها: أعمدة العبوة | المحتوى | المتغير فقط حين كل ما في المخزن منتج تام
        // (مخزن الاختبار المشترك قد يحوي مواد من اختبارات أخرى، فيُفحص الاتساق لا قيمة ثابتة)
        var fg = wh.Workspace(_f.MainWarehouseId);
        await Open(wh, fg);
        Assert.Equal(fg.Balances.Count > 0 && fg.Balances.All(b => b.IsFinished), fg.PartsLayout);
        Assert.NotEqual(fg.PartsLayout, fg.NameLayout);
        Assert.All(fg.Balances.Where(b => b.IsFinished), b => Assert.NotEqual(b.ItemName, b.DisplayName));

        // مستند إدخال من مخزن المواد الأولية: الكمية في القائمة باسم المادة
        await Open(wh, raw);
        raw.Operation = raw.OperationOptions.Single(o => o.Value == StockDocumentType.Receipt);
        raw.ShowAllItems = true;
        raw.LineItem = raw.ItemsLookup.Single(i => i.ItemCode == "WN-CAP-1");
        await raw.IdleAsync();
        raw.LineQuantity = 500;
        await raw.AddLineCommand.ExecuteAsync();
        raw.LineItem = raw.ItemsLookup.Single(i => i.ItemCode == "WN-LBL-1");
        await raw.IdleAsync();
        raw.LineQuantity = 300;
        await raw.AddLineCommand.ExecuteAsync();
        await raw.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        await raw.LoadReportsCommand.ExecuteAsync();
        await raw.IdleAsync();
        var doc = raw.Documents.First();
        Assert.True(doc.HasManyLines);
        Assert.StartsWith("▾ 2 أسطر", doc.QuantityText);
        Assert.Contains(doc.LineSummaries, l => l.Name == "سدادة — أبيض (أسماء)" && l.Quantity == "500 قطعة");
        Assert.Contains(doc.LineSummaries, l => l.Name == "ليبل - مطعم ليالي (أسماء)" && l.Quantity == "300 قطعة");
    }

    [Fact]
    public async Task Manual_finished_receipt_picks_the_special_name_variant()
    {
        int recipeId;
        await using (var db = _f.NewDb())
        {
            var r = new CustomRecipe { FinishedItemId = _f.WaterItemId, Name = "طلب خاص — مطعم المتغير", ShortName = "مطعم المتغير" };
            db.CustomRecipes.Add(r);
            await db.SaveChangesAsync();
            recipeId = r.Id;
        }
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var wh = shell.Open<WarehouseModuleViewModel>(ModuleCode.Warehouse);
        await wh.IdleAsync();
        var fg = wh.Workspace(_f.MainWarehouseId);
        await Open(wh, fg);
        fg.Operation = fg.OperationOptions.Single(o => o.Value == StockDocumentType.Receipt);
        fg.LineItem = null;
        fg.LineItem = fg.ItemsLookup.Single(i => i.Id == _f.WaterItemId);
        await fg.IdleAsync();
        Assert.True(fg.ShowVariant);                                                  // المنتج التام بطلب خاص: قائمة «المتغير»
        Assert.Null(fg.VariantOptions[0].RecipeId);
        Assert.EndsWith("(أساسي)", fg.VariantOptions[0].Label);
        fg.LineVariant = fg.VariantOptions.Single(v => v.RecipeId == recipeId);
        await fg.IdleAsync();
        Assert.All(fg.BatchOptions.Where(b => b.BatchId is not null), b => Assert.Equal(recipeId, b.RecipeId));   // تشغيلات هذا الاسم فقط
        fg.LineQuantity = 3;
        await fg.AddLineCommand.ExecuteAsync();
        Assert.Contains("مطعم المتغير", fg.Lines.Single().ItemName);
        await fg.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);

        // سُجّل على تشغيلة تحمل المتغير (تلقائية لعدم كتابة رقم) لا كأساسي
        await using var check = _f.NewDb();
        var tx = await check.StockTransactions.Include(t => t.Batch)
            .Where(t => t.ReferenceTable == "StockDocuments" && t.ReferenceId == fg.LastDocument!.Id).SingleAsync();
        Assert.Equal(recipeId, tx.Batch!.CustomRecipeId);
        Assert.StartsWith($"V{recipeId}-", tx.Batch.BatchNumber);

        // المواد الأولية لا تعرض قائمة المتغير
        fg.LineItem = null;
        await fg.IdleAsync();
        Assert.False(fg.ShowVariant);
    }
}
