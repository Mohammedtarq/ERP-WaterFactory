using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.ViewModels.Sales;
using ERP.Presentation.ViewModels.Shell;
using ERP.Presentation.ViewModels.Suppliers;
using ERP.Presentation.ViewModels.Warehouse;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>
/// ملاحظات التجربة (الدفعة ج): حد التنبيه يُضبط من داخل كل مخزن بعبوة الصنف ويُقارن برصيد ذلك المخزن وحده،
/// وحد بطاقة الصنف افتراضي في مخزنه الرئيسي فقط؛ ومتغيرات «مقترح الشراء» تُعدَّل من الجدول نفسه.
/// </summary>
[Collection("app")]
public class TrialFixesBatchCTests
{
    private readonly AppFixture _f;
    public TrialFixesBatchCTests(AppFixture f) => _f = f;

    [Fact]
    public async Task Alert_thresholds_are_set_inside_each_warehouse_in_packs_and_reorder_variables_are_editable()
    {
        int productId, materialId, secondFgId, mainFgId;
        await using (var db = _f.NewDb())
        {
            var admin = await db.Users.FirstAsync(u => u.Username == AppFixture.AdminUser);
            var branch = await db.Branches.FirstAsync();
            if (!await db.Warehouses.AnyAsync(w => w.WarehouseType == WarehouseType.RawMaterial && w.IsActive))
                db.Warehouses.Add(new Warehouse { BranchId = branch.Id, Name = "مخزن مواد الدفعة ج", WarehouseType = WarehouseType.RawMaterial });
            var second = new Warehouse { BranchId = branch.Id, Name = "مخزن منتج ثانٍ للدفعة ج", WarehouseType = WarehouseType.FinishedGoods };
            var product = new Item { ItemCode = "TFC-C40", ItemName = "ماء الدفعة ج كارتون", SalePrice = 250, MinStockAlertLevel = 400 };   // 10 كراتين افتراضيًا
            var material = new Item { ItemCode = "TFC-CAP", ItemName = "سدادة الدفعة ج", SourcingMethod = SourcingMethod.Purchased, CostPrice = 5, LeadTimeDays = 5 };
            db.AddRange(second, product, material);
            await db.SaveChangesAsync();
            db.ItemPackagingLevels.AddRange(new ItemPackagingLevel { ItemId = product.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 },
                                            new ItemPackagingLevel { ItemId = product.Id, LevelName = "كارتون", EquivalentBaseUnits = 40 },
                                            new ItemPackagingLevel { ItemId = material.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 });
            var main = await new StockAlertService(db).HomeWarehouseIdAsync(SourcingMethod.Manufactured);
            db.StockTransactions.AddRange(
                new StockTransaction { ItemId = product.Id, WarehouseId = main!.Value, QuantityBaseUnits = 600, UnitCost = 100, TransactionType = StockTransactionType.Receipt, CreatedByUserId = admin.Id },
                new StockTransaction { ItemId = product.Id, WarehouseId = second.Id, QuantityBaseUnits = 80, UnitCost = 100, TransactionType = StockTransactionType.Receipt, CreatedByUserId = admin.Id });
            await db.SaveChangesAsync();
            (productId, materialId, secondFgId, mainFgId) = (product.Id, material.Id, second.Id, main.Value);
        }

        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var wh = shell.Open<WarehouseModuleViewModel>(ModuleCode.Warehouse);
        await wh.IdleAsync();

        // المخزن الرئيسي: حد بطاقة الصنف (400 قطعة) يظهر 10 كارتون «من بطاقة الصنف»، والرصيد 15 كارتون ليس منخفضًا
        var mainWs = wh.Workspace(mainFgId);
        wh.SelectedTab = mainWs;
        await wh.LastActivation;
        await mainWs.IdleAsync();
        Assert.True(mainWs.HasAlerts);
        var row = mainWs.Alerts.Rows.Single(r => r.ItemId == productId);
        Assert.Equal(("كارتون", 10m, "من بطاقة الصنف", "15 كارتون", false), (row.UnitName, row.Threshold, row.SourceText, row.BalanceText, row.IsLow));

        // من داخل المخزن: بحث ثم رفع الحد إلى 20 كارتون ← منخفض، ويظهر في الجرس باسم المخزن
        mainWs.Alerts.Filter = "الدفعة ج";
        Assert.Contains(row, mainWs.Alerts.VisibleRows);
        Assert.DoesNotContain(mainWs.Alerts.VisibleRows, r => !r.ItemName.Contains("الدفعة ج"));
        row.Threshold = 20;
        Assert.Equal("معدَّل — لم يُحفظ", row.SourceText);
        await mainWs.Alerts.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        row = mainWs.Alerts.Rows.Single(r => r.ItemId == productId);
        Assert.Equal((20m, "خاص بالمخزن", true), (row.Threshold, row.SourceText, row.IsLow));
        await using (var db = _f.NewDb())
        {
            Assert.Equal(800m, (await db.WarehouseItemAlerts.SingleAsync(a => a.WarehouseId == mainFgId && a.ItemId == productId)).MinQuantity);
            var low = await new StockAlertService(db).LowAsync();
            Assert.Contains(low, l => l.ItemId == productId && l.WarehouseId == mainFgId && l.MinText == "20 كارتون" && l.BalanceText == "15 كارتون");
            Assert.DoesNotContain(low, l => l.ItemId == productId && l.WarehouseId == secondFgId);   // الحد الافتراضي لا يسري في المخزن الثاني
        }

        // المخزن الثاني: لا حد افتراضي فيه؛ يُضبط حده الخاص ويُقارن برصيده وحده (2 كارتون)
        var secondWs = wh.Workspace(secondFgId);
        wh.SelectedTab = secondWs;
        await wh.LastActivation;
        await secondWs.IdleAsync();
        var row2 = secondWs.Alerts.Rows.Single(r => r.ItemId == productId);
        Assert.Equal((null, "", "2 كارتون"), (row2.Threshold, row2.SourceText, row2.BalanceText));
        row2.Threshold = 3;
        await secondWs.Alerts.SaveCommand.ExecuteAsync();
        await secondWs.IdleAsync();                                          // الحفظ يحدّث الأرصدة ومؤشر «عند حد التنبيه»
        Assert.True(secondWs.Alerts.Rows.Single(r => r.ItemId == productId).IsLow);
        Assert.True(secondWs.LowCount >= 1);
        Assert.Contains(secondWs.Balances, b => b.ItemId == productId && b.BelowAlert);

        // إلغاء الحد الخاص بالمخزن الرئيسي يعيد حد بطاقة الصنف
        wh.SelectedTab = mainWs;
        await wh.LastActivation;
        await mainWs.IdleAsync();
        mainWs.Alerts.Rows.Single(r => r.ItemId == productId).Threshold = null;
        await mainWs.Alerts.SaveCommand.ExecuteAsync();
        Assert.Equal((10m, "من بطاقة الصنف"), (mainWs.Alerts.Rows.Single(r => r.ItemId == productId).Threshold, mainWs.Alerts.Rows.Single(r => r.ItemId == productId).SourceText));

        // شاشة «إعدادات التنبيهات»: نفس المحرر لأي مخزن، وقائمة المنخفض في كل المخازن
        var settings = wh.Section<StockAlertsSectionViewModel>();
        wh.SelectedTab = settings;
        await wh.LastActivation;
        await settings.IdleAsync();
        Assert.Contains(settings.Low, l => l.ItemId == productId && l.WarehouseId == secondFgId);
        settings.Warehouse = settings.Warehouses.Single(w => w.Id == secondFgId);
        await settings.IdleAsync();
        await settings.Editor.LoadAsync(secondFgId);
        Assert.Equal(3m, settings.Editor.Rows.Single(r => r.ItemId == productId).Threshold);

        // (7) مقترح الشراء: مدة التجهيز وحد التنبيه يُعدَّلان من الجدول ويُعاد الحساب
        var sup = shell.Open<SuppliersModuleViewModel>(ModuleCode.Suppliers);
        sup.SelectedTab = sup.Reorder;
        await sup.LastActivation;
        await sup.Reorder.IdleAsync();
        var cap = sup.Reorder.Rows.Single(r => r.ItemId == materialId);
        Assert.Equal((5, (decimal?)null, false), (cap.LeadTimeDays, cap.AlertLevel, cap.IsChanged));
        cap.LeadTimeDays = 12;
        cap.AlertLevel = 500;
        Assert.True(cap.IsChanged);
        await sup.Reorder.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        cap = sup.Reorder.Rows.Single(r => r.ItemId == materialId);
        Assert.Equal((12, 500m, 500m, "نفد"), (cap.Row.LeadTimeDays, cap.Row.AlertLevel, cap.Row.ReorderPoint, cap.Row.Status));
        Assert.Equal(500m, cap.Row.SuggestedQuantity);
        sup.Reorder.SafetyDays = 10;
        await sup.Reorder.IdleAsync();
        Assert.Contains("10 أيام أمان", sup.Reorder.FormulaText);
        await using (var db = _f.NewDb())
        {
            Assert.Equal(12, (await db.Items.SingleAsync(i => i.Id == materialId)).LeadTimeDays);
            var rawId = await new StockAlertService(db).HomeWarehouseIdAsync(SourcingMethod.Purchased);
            Assert.Equal(500m, (await db.WarehouseItemAlerts.SingleAsync(a => a.ItemId == materialId)).MinQuantity);
            Assert.Equal(rawId, (await db.WarehouseItemAlerts.SingleAsync(a => a.ItemId == materialId)).WarehouseId);
        }
        await shell.IdleAllAsync();
        Assert.Empty(_f.Unhandled);
    }

    [Fact]
    public async Task Selling_prices_screen_sets_general_agent_and_special_order_prices_by_pack()
    {
        int productId, recipeId, agentId, directId, fgId, shrinkId;
        await using (var db = _f.NewDb())
        {
            var product = new Item { ItemCode = "SPX-S20", ItemName = "ماء أسعار البيع شرنك", SalePrice = 200 };
            var agent = new Customer { Name = "وكيل أسعار البيع", CustomerType = CustomerType.Agent };
            var direct = new Customer { Name = "زبون أسعار البيع" };
            db.AddRange(product, agent, direct);
            await db.SaveChangesAsync();
            var shrink = new ItemPackagingLevel { ItemId = product.Id, LevelName = "شرنك", EquivalentBaseUnits = 20, IsSellableUnit = true };
            db.ItemPackagingLevels.AddRange(new ItemPackagingLevel { ItemId = product.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 }, shrink);
            var recipe = new CustomRecipe { FinishedItemId = product.Id, Name = "ليبل مطعم الأسعار" };
            db.CustomRecipes.Add(recipe);
            await db.SaveChangesAsync();
            var fg = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.FinishedGoods && w.IsActive);
            (productId, recipeId, agentId, directId, fgId, shrinkId) = (product.Id, recipe.Id, agent.Id, direct.Id, fg.Id, shrink.Id);
        }

        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var sales = shell.Open<SalesModuleViewModel>(ModuleCode.Sales);
        var prices = sales.Section<SellingPricesSectionViewModel>();
        sales.SelectedTab = prices;
        await sales.LastActivation;
        await prices.IdleAsync();

        // المنتجات وطلباتها الخاصة فقط — لا مواد أولية — وبالعبوة
        await using (var db = _f.NewDb())
        {
            var purchased = await db.Items.Where(i => i.SourcingMethod == SourcingMethod.Purchased).Select(i => i.Id).ToListAsync();
            Assert.DoesNotContain(prices.Rows, r => purchased.Contains(r.Row.ItemId));
        }
        prices.Filter = "أسعار البيع";
        var basic = prices.VisibleRows.Single(r => r.Row.ItemId == productId && !r.IsVariant);
        var variant = prices.VisibleRows.Single(r => r.Row.RecipeId == recipeId);
        Assert.Equal(("شرنك (20)", 4_000m, "الأساسي"), (basic.PackText, basic.GeneralPack, basic.VariantText));
        Assert.Equal(("ليبل مطعم الأسعار", (decimal?)null, "العام 4,000 — للوكيل 4,000"), (variant.VariantText, variant.GeneralPack, variant.EffectiveText));
        Assert.False(prices.HasAgent);

        // السعر العام للعبوة: الأساسي 5,000 والطلب الخاص 6,000
        basic.GeneralPack = 5_000;
        variant.GeneralPack = 6_000;
        await prices.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);

        // سعر الوكيل: للأساسي 4,600، والطلب الخاص بلا سعر وكيل ← يأخذ عام الطلب الخاص
        prices.Agent = prices.Agents.Single(a => a.Id == agentId);
        await prices.IdleAsync();
        Assert.True(prices.HasAgent);
        basic = prices.VisibleRows.Single(r => r.Row.ItemId == productId && !r.IsVariant);
        variant = prices.VisibleRows.Single(r => r.Row.RecipeId == recipeId);
        basic.AgentPack = 4_600;
        Assert.Equal("العام 6,000 — للوكيل 6,000", variant.EffectiveText);
        await prices.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);

        async Task<decimal> PriceAsync(int customerId, int? recipe)
        {
            await using var db = _f.NewDb();
            var admin = await db.Users.FirstAsync(u => u.Username == AppFixture.AdminUser);
            var svc = new SalesService(db);
            var (_, id) = await svc.CreateInvoiceAsync(new SalesInvoiceHeaderInput(customerId, fgId, DateTime.Today, InvoicePaymentMethod.Credit), admin.Id);
            Assert.True((await svc.AddLineAsync(id!.Value, new SalesInvoiceLineInput(productId, shrinkId, 1, CustomRecipeId: recipe), admin.Id)).Success);
            var unit = await db.SalesInvoiceLines.Where(l => l.SalesInvoiceId == id).Select(l => l.UnitPrice).SingleAsync();
            Assert.Equal(unit, await svc.GetSuggestedUnitPriceAsync(customerId, productId, shrinkId, true, recipe));   // الاقتراح في الشاشة = ما تسعّره الفاتورة
            return unit;
        }
        Assert.Equal(5_000m, await PriceAsync(directId, null));
        Assert.Equal(6_000m, await PriceAsync(directId, recipeId));
        Assert.Equal(4_600m, await PriceAsync(agentId, null));
        Assert.Equal(6_000m, await PriceAsync(agentId, recipeId));

        // سعر وكيل خاص للطلب الخاص يغلب العام
        variant = prices.VisibleRows.Single(r => r.Row.RecipeId == recipeId);
        variant.AgentPack = 5_500;
        await prices.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Equal(5_500m, await PriceAsync(agentId, recipeId));
        Assert.Equal(6_000m, await PriceAsync(directId, recipeId));

        // إلغاء سعر الطلب الخاص العام (فارغ) يعيده لسعر الأساسي
        prices.Agent = null;
        await prices.IdleAsync();
        prices.Rows.Single(r => r.Row.RecipeId == recipeId).GeneralPack = null;
        await prices.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Equal(5_000m, await PriceAsync(directId, recipeId));
        await using (var db = _f.NewDb())
        {
            Assert.Equal(250m, (await db.Items.SingleAsync(i => i.Id == productId)).SalePrice);                 // محفوظ بالقطعة
            Assert.Equal(230m, (await db.AgentItemPrices.SingleAsync(p => p.ItemId == productId && p.CustomRecipeId == null)).AgentPrice);
            Assert.True(await db.AuditLogs.AnyAsync(a => a.TableName == "SellingPrices"));
        }
        await shell.IdleAllAsync();
        Assert.Empty(_f.Unhandled);
    }
}
