using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Security;
using ERP.Data.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>
/// أمر يومي واحد بكل الأصناف: المنتج نفسه بشرنك وكارتون، وبأسماء المطاعم وملصق مناسبة.
/// التشغيلة تحمل متغيرها، والبيع والتحميل لا يصرفان محجوز مطعم لغيره إلا بصلاحية.
/// منتج خاص بالاختبار فلا يمس أرصدة الاختبارات الأخرى.
/// </summary>
[Collection("controls")]
public class BatchVariantTests
{
    private readonly ControlsFixture _f;
    public BatchVariantTests(ControlsFixture f) => _f = f;

    private sealed record Setup(Item Water, ItemPackagingLevel Piece, ItemPackagingLevel Shrink, ItemPackagingLevel Carton,
                                Customer Hassoun, Customer Elias, Customer General, CustomRecipe HassounRecipe, CustomRecipe EliasRecipe,
                                CustomRecipe Wedding, Warehouse Fg);

    private async Task<Setup> ArrangeAsync(ProjectDbContext db, string tag)
    {
        var raw = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.RawMaterial);
        var fg = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.FinishedGoods);
        Item Raw(string code, string name) => new() { ItemCode = $"{code}-{tag}", ItemName = $"{name} {tag}", SourcingMethod = SourcingMethod.Purchased, CostPrice = 10 };
        var pre = Raw("VPRE", "امبولة"); var cap = Raw("VCAP", "سدادة عامة"); var lbl = Raw("VLBL", "ليبل الرحمة");
        var capH = Raw("VCAPH", "سدادة الحسون"); var lblH = Raw("VLBLH", "ليبل الحسون"); var lblE = Raw("VLBLE", "ليبل الياس"); var lblW = Raw("VLBLW", "ليبل زواج سعيد");
        var water = new Item { ItemCode = $"VW-{tag}", ItemName = $"ماء متغيرات {tag}", SalePrice = 250 };
        db.Items.AddRange(pre, cap, lbl, capH, lblH, lblE, lblW, water);
        await db.SaveChangesAsync();
        foreach (var r in new[] { pre, cap, lbl, capH, lblH, lblE, lblW })
            db.ItemPackagingLevels.Add(new ItemPackagingLevel { ItemId = r.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 });
        var piece = new ItemPackagingLevel { ItemId = water.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 };
        var shrink = new ItemPackagingLevel { ItemId = water.Id, LevelName = "شرنك", ContainsQuantity = 6, EquivalentBaseUnits = 6 };
        var carton = new ItemPackagingLevel { ItemId = water.Id, LevelName = "كارتون", ContainsQuantity = 12, EquivalentBaseUnits = 12 };
        db.ItemPackagingLevels.AddRange(piece, shrink, carton);
        var bom = new BillOfMaterials { FinishedItemId = water.Id };
        bom.Lines.Add(new BOMLine { RawMaterialItemId = pre.Id, QuantityPerUnit = 1, ComponentRole = "امبولة" });
        bom.Lines.Add(new BOMLine { RawMaterialItemId = cap.Id, QuantityPerUnit = 1, ComponentRole = "غطاء" });
        bom.Lines.Add(new BOMLine { RawMaterialItemId = lbl.Id, QuantityPerUnit = 1, ComponentRole = "لاصق" });
        db.BillOfMaterials.Add(bom);
        var hassoun = new Customer { Name = $"مطعم الحسون {tag}" };
        var elias = new Customer { Name = $"مطعم الياس {tag}" };
        var general = new Customer { Name = $"زبون عام {tag}" };
        db.Customers.AddRange(hassoun, elias, general);
        await db.SaveChangesAsync();
        foreach (var r in new[] { pre, cap, lbl, capH, lblH, lblE, lblW })
            db.StockTransactions.Add(new StockTransaction { ItemId = r.Id, WarehouseId = raw.Id, QuantityBaseUnits = 5_000, UnitCost = 10,
                                                            TransactionType = StockTransactionType.Receipt, CreatedByUserId = _f.AdminId });
        CustomRecipe Recipe(string name, Customer? c, params (Item with, Item replaces, string role)[] lines)
        {
            var recipe = new CustomRecipe { FinishedItemId = water.Id, CustomerId = c?.Id, Name = name };
            foreach (var (with, replaces, role) in lines)
                recipe.Lines.Add(new CustomRecipeLine { ComponentItemId = with.Id, ReplacesRawMaterialItemId = replaces.Id, ComponentLabel = role, QuantityPerUnit = 1 });
            return recipe;
        }
        var hRecipe = Recipe($"مطعم الحسون {tag}", hassoun, (capH, cap, "غطاء"), (lblH, lbl, "لاصق"));
        var eRecipe = Recipe($"مطعم الياس {tag}", elias, (lblE, lbl, "لاصق"));
        var wedding = Recipe($"زواج سعيد {tag}", null, (lblW, lbl, "لاصق"));
        db.CustomRecipes.AddRange(hRecipe, eRecipe, wedding);
        await db.SaveChangesAsync();
        return new Setup(water, piece, shrink, carton, hassoun, elias, general, hRecipe, eRecipe, wedding, fg);
    }

    private static async Task<decimal> FgAsync(ProjectDbContext db, Setup s, int? recipeId) =>
        await db.StockTransactions.Where(t => t.ItemId == s.Water.Id && t.WarehouseId == s.Fg.Id && (t.Batch == null ? null : t.Batch.CustomRecipeId) == recipeId)
                                  .SumAsync(t => (decimal?)t.QuantityBaseUnits) ?? 0;

    private async Task<User> SellerAsync(ProjectDbContext db, string name)
    {
        var role = await db.Roles.FirstAsync(r => r.Name == "محاسب");
        var user = new User { Username = name, PasswordHash = PasswordHasher.Hash("x"), RoleId = role.Id };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<(FinanceOperationResult result, int invoiceId)> SellAsync(ProjectDbContext db, Setup s, Customer c, decimal pieces, int userId, int? recipeId = null)
    {
        var sales = new SalesService(db);
        var (created, id) = await sales.CreateInvoiceAsync(new SalesInvoiceHeaderInput(c.Id, s.Fg.Id, DateTime.Today, InvoicePaymentMethod.Cash), userId);
        Assert.True(created.Success, created.ErrorMessage);
        var added = await sales.AddLineAsync(id!.Value, new SalesInvoiceLineInput(s.Water.Id, s.Piece.Id, pieces, CustomRecipeId: recipeId), userId);
        Assert.True(added.Success, added.ErrorMessage);
        var (posted, _) = await sales.PostInvoiceAsync(id.Value, userId);
        return (posted, id.Value);
    }

    /// <summary>الكمية المصروفة لكل متغير (0 = الأساسي).</summary>
    private static async Task<Dictionary<int, decimal>> IssuedByVariantAsync(ProjectDbContext db, int invoiceId) =>
        await db.StockTransactions.Where(t => t.ReferenceTable == "SalesInvoices" && t.ReferenceId == invoiceId)
            .GroupBy(t => t.Batch == null ? null : t.Batch.CustomRecipeId).Select(g => new { g.Key, Qty = -g.Sum(t => t.QuantityBaseUnits) })
            .ToDictionaryAsync(x => x.Key ?? 0, x => x.Qty);

    [Fact]
    public async Task One_daily_order_keeps_every_variant_apart_through_sales()
    {
        await using var db = _f.NewDb(_f.AdminId);
        var s = await ArrangeAsync(db, "S1");

        // الرحمة شرنك + الرحمة كارتون + مطعمان + مناسبة: المنتج نفسه خمس مرات في أمر واحد
        var (r, orderId, lines) = await new DailyProductionService(db).RecordAsync(DateTime.Today, new[]
        {
            new DailyProductionLineInput(s.Water.Id, s.Shrink.Id, 10),                             // 60
            new DailyProductionLineInput(s.Water.Id, s.Carton.Id, 5),                              // 60
            new DailyProductionLineInput(s.Water.Id, s.Carton.Id, 5, s.HassounRecipe.Id),          // 60
            new DailyProductionLineInput(s.Water.Id, s.Carton.Id, 2, s.EliasRecipe.Id),            // 24
            new DailyProductionLineInput(s.Water.Id, s.Shrink.Id, 5, s.Wedding.Id),                // 30
        }, _f.AdminId);
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(5, lines.Count);
        Assert.Equal(5, await db.ProductionOrderLines.CountAsync(l => l.ProductionOrderId == orderId));
        var batches = await db.ProductionOrderLines.AsNoTracking().Where(l => l.ProductionOrderId == orderId).OrderBy(l => l.LineNo)
                              .Select(l => l.OutputBatch!.CustomRecipeId).ToListAsync();
        Assert.Equal(new int?[] { null, null, s.HassounRecipe.Id, s.EliasRecipe.Id, s.Wedding.Id }, batches);
        Assert.Equal((120m, 60m, 24m, 30m), (await FgAsync(db, s, null), await FgAsync(db, s, s.HassounRecipe.Id), await FgAsync(db, s, s.EliasRecipe.Id), await FgAsync(db, s, s.Wedding.Id)));

        var seller = await SellerAsync(db, "seller_variants");

        // زبون عام: الأساسي فقط (120)، والرسالة تذكر المحجوز والمناسبة
        var (tooMuch, _) = await SellAsync(db, s, s.General, 130, seller.Id);
        Assert.False(tooMuch.Success);
        Assert.Contains("المتاح 120", tooMuch.ErrorMessage);
        Assert.Contains(s.HassounRecipe.Name, tooMuch.ErrorMessage);
        Assert.Contains(s.Wedding.Name, tooMuch.ErrorMessage);
        var (ok, inv1) = await SellAsync(db, s, s.General, 100, seller.Id);
        Assert.True(ok.Success, ok.ErrorMessage);
        Assert.Equal(new Dictionary<int, decimal> { [0] = 100 }, await IssuedByVariantAsync(db, inv1));

        // مطعم الحسون: محجوزه أولًا ثم الأساسي، ولا يأخذ محجوز الياس
        var (h, inv2) = await SellAsync(db, s, s.Hassoun, 70, seller.Id);
        Assert.True(h.Success, h.ErrorMessage);
        Assert.Equal(new Dictionary<int, decimal> { [s.HassounRecipe.Id] = 60, [0] = 10 }, await IssuedByVariantAsync(db, inv2));

        // المناسبة تُطلب بالاسم ويشتريها أي زبون
        var (w, inv3) = await SellAsync(db, s, s.General, 30, seller.Id, s.Wedding.Id);
        Assert.True(w.Success, w.ErrorMessage);
        Assert.Equal(new Dictionary<int, decimal> { [s.Wedding.Id] = 30 }, await IssuedByVariantAsync(db, inv3));

        // نفد الأساسي (10 باقية): بلا صلاحية لا يُصرف محجوز الياس لزبون عام
        var (blocked, _) = await SellAsync(db, s, s.General, 20, seller.Id);
        Assert.False(blocked.Success);
        Assert.Contains(s.EliasRecipe.Name, blocked.ErrorMessage);
        var (named, _) = await SellAsync(db, s, s.General, 5, seller.Id, s.EliasRecipe.Id);
        Assert.False(named.Success);

        // بالصلاحية الخاصة (المدير يملكها): الأساسي أولًا ثم محجوز غيره
        Assert.True(await SpecialPermission.HasAsync(db, _f.AdminId, SpecialPermission.ReservedStock));
        var (overridden, inv4) = await SellAsync(db, s, s.General, 20, _f.AdminId);
        Assert.True(overridden.Success, overridden.ErrorMessage);
        Assert.Equal(new Dictionary<int, decimal> { [0] = 10, [s.EliasRecipe.Id] = 10 }, await IssuedByVariantAsync(db, inv4));
    }

    [Fact]
    public async Task Load_order_takes_basic_stock_unless_a_variant_is_named()
    {
        await using var db = _f.NewDb(_f.AdminId);
        var s = await ArrangeAsync(db, "S2");
        var (r, _, _) = await new DailyProductionService(db).RecordAsync(DateTime.Today, new[]
        {
            new DailyProductionLineInput(s.Water.Id, s.Carton.Id, 2),
            new DailyProductionLineInput(s.Water.Id, s.Carton.Id, 3, s.HassounRecipe.Id),
        }, _f.AdminId);
        Assert.True(r.Success, r.ErrorMessage);

        var rep = new Employee { FullName = "مندوب متغيرات", IsSalesRep = true, BaseSalary = 500_000 };
        db.Employees.Add(rep);
        await db.SaveChangesAsync();
        var van = new Warehouse { BranchId = s.Fg.BranchId, Name = "سيارة متغيرات", WarehouseType = WarehouseType.RepVan, OwnerEmployeeId = rep.Id };
        db.Warehouses.Add(van);
        await db.SaveChangesAsync();
        var svc = new RepOperationsService(db);
        Task<decimal> Van(int? recipe) => db.StockTransactions.Where(t => t.WarehouseId == van.Id && (t.Batch == null ? null : t.Batch.CustomRecipeId) == recipe)
                                            .SumAsync(t => (decimal?)t.QuantityBaseUnits).ContinueWith(x => x.Result ?? 0);

        // حمولة عامة أكبر من الأساسي: لا تُكمَّل من محجوز المطعم
        var (c1, o1) = await svc.CreateLoadOrderAsync(van.Id, s.Fg.Id, DateTime.Today, new[] { new RepLoadLineInput(s.Water.Id, s.Carton.Id, 4) }, null, _f.AdminId);
        Assert.True(c1.Success, c1.ErrorMessage);
        var (p1, _) = await svc.PrepareLoadOrderAsync(o1!.Id, null, _f.AdminId);
        Assert.False(p1.Success);
        Assert.Contains(s.HassounRecipe.Name, p1.ErrorMessage);
        db.ChangeTracker.Clear();   // التجهيز الفاشل تراجع كله
        Assert.True((await svc.CancelLoadOrderAsync(o1.Id, "إعادة", _f.AdminId)).Success);

        // سطر عام + سطر باسم المطعم لتوصيله
        var (c2, o2) = await svc.CreateLoadOrderAsync(van.Id, s.Fg.Id, DateTime.Today, new[]
        {
            new RepLoadLineInput(s.Water.Id, s.Carton.Id, 2),
            new RepLoadLineInput(s.Water.Id, s.Carton.Id, 3, s.HassounRecipe.Id),
        }, null, _f.AdminId);
        Assert.True(c2.Success, c2.ErrorMessage);
        Assert.Equal(2, o2!.Lines.Count);
        var (p2, _) = await svc.PrepareLoadOrderAsync(o2.Id, null, _f.AdminId);
        Assert.True(p2.Success, p2.ErrorMessage);
        Assert.Equal((24m, 36m), (await Van(null), await Van(s.HassounRecipe.Id)));
    }
    [Fact]
    public async Task Templates_repeat_last_day_preview_wizard_report_and_batch_correction()
    {
        await using var db = _f.NewDb(_f.AdminId);
        var s = await ArrangeAsync(db, "S3");
        var lines = new[]
        {
            new DailyTemplateLine(s.Water.Id, s.Shrink.Id, null, 10),
            new DailyTemplateLine(s.Water.Id, s.Carton.Id, s.HassounRecipe.Id, 4),
        };

        // القالب يُحفظ بالاسم ويُستبدل كاملًا
        var templates = new DailyProductionTemplateService(db);
        Assert.False((await templates.SaveAsync(" ", lines, _f.AdminId)).result.Success);
        var (saved, tid) = await templates.SaveAsync("أمر الأحد S3", lines, _f.AdminId);
        Assert.True(saved.Success, saved.ErrorMessage);
        Assert.True((await templates.SaveAsync("أمر الأحد S3", lines.Reverse().ToArray(), _f.AdminId)).result.Success);
        Assert.Equal(lines.Reverse().ToList(), await templates.LinesAsync(tid!.Value));
        Assert.Single(await templates.ListAsync(), t => t.Name == "أمر الأحد S3");

        // المعاينة تذكر ليبل المطعم ومقداره
        var daily = new DailyProductionService(db);
        var inputs = lines.Select(l => new DailyProductionLineInput(l.FinishedItemId, l.PackagingLevelId, l.Packs, l.CustomRecipeId)).ToList();
        var (previewError, needs) = await daily.PreviewAsync(inputs);
        Assert.Null(previewError);
        Assert.Equal(48m, needs.Single(n => n.ItemName == "ليبل الحسون S3").Required);
        Assert.Equal(60m, needs.Single(n => n.ItemName == "ليبل الرحمة S3").Required);
        Assert.Equal(108m, needs.Single(n => n.ItemName == "امبولة S3").Required);
        Assert.All(needs, n => Assert.False(n.IsShort));

        // تكرار آخر إنتاج: الوحدة والعدد والمتغير كما أُدخلت
        var (r, _, _) = await daily.RecordAsync(DateTime.Today, inputs, _f.AdminId);
        Assert.True(r.Success, r.ErrorMessage);
        var (lastDate, last) = await templates.LastProductionAsync();
        Assert.Equal(DateTime.Today, lastDate);
        Assert.Equal(lines.ToList(), last);
        Assert.True((await templates.DeleteAsync(tid.Value)).Success);

        // متغير جديد بخطوة واحدة: ليبل جديد يُنشأ، والسدادة من صنف موجود
        var pack = new PackagingTemplateService(db);
        var capH = await db.Items.SingleAsync(i => i.ItemCode == "VCAPH-S3");
        var (created, recipeId) = await pack.CreateVariantAsync(new NewVariantRequest(s.Water.Id, s.General.Id, "مطعم تراث S3", new[]
        {
            new VariantRoleInput("لاصق", null, "ليبل مطعم تراث S3"),
            new VariantRoleInput("غطاء", capH.Id, null),
            new VariantRoleInput("امبولة", null, null),
        }));
        Assert.True(created.Success, created.ErrorMessage);
        var recipe = await db.CustomRecipes.AsNoTracking().Include(c => c.Lines).ThenInclude(l => l.ComponentItem).SingleAsync(c => c.Id == recipeId);
        Assert.Equal(2, recipe.Lines.Count);
        var newLabel = recipe.Lines.Single(l => l.ComponentLabel == "لاصق").ComponentItem;
        Assert.Equal(("ليبل مطعم تراث S3", SourcingMethod.Purchased, 10m), (newLabel.ItemName, newLabel.SourcingMethod, newLabel.CostPrice ?? 0));
        Assert.True(await db.ItemPackagingLevels.AnyAsync(l => l.ItemId == newLabel.Id && l.EquivalentBaseUnits == 1));
        Assert.False((await pack.CreateVariantAsync(new NewVariantRequest(s.Water.Id, null, "مطعم تراث S3", new[] { new VariantRoleInput("لاصق", capH.Id, null) }))).result.Success);
        Assert.False((await pack.CreateVariantAsync(new NewVariantRequest(s.Water.Id, null, "بلا تغيير S3", new[] { new VariantRoleInput("لاصق", null, null) }))).result.Success);

        // تقرير المتغيرات: الأساسي والمحجوز منفصلان بكلفتهما
        var report = new VariantStockService(db);
        var rows = (await report.SummaryAsync(DateTime.Today, DateTime.Today)).Where(x => x.ItemName == s.Water.ItemName).ToList();
        var basic = rows.Single(x => x.RecipeId == null);
        var hassoun = rows.Single(x => x.RecipeId == s.HassounRecipe.Id);
        Assert.Equal((60m, 60m, "أساسي"), (basic.Produced, basic.Balance, basic.Kind));
        Assert.Equal((48m, 48m, $"محجوز لـ {s.Hassoun.Name}"), (hassoun.Produced, hassoun.Balance, hassoun.Kind));
        Assert.Equal("4 كارتون", hassoun.BalanceText);
        Assert.Equal(30m, basic.UnitCost);                   // امبولة + سدادة + ليبل بكلفة 10
        Assert.Equal(30m, hassoun.UnitCost);

        // تصحيح متغير تشغيلة: بالصلاحية فقط، ومع السبب، ويُسجَّل
        var batch = (await report.BatchesAsync()).Single(b => b.ItemId == s.Water.Id && b.RecipeId == null);
        var seller = await SellerAsync(db, "seller_variants_fix");
        Assert.False((await report.SetBatchVariantAsync(batch.BatchId, s.EliasRecipe.Id, "خطأ إدخال", seller.Id)).Success);
        Assert.False((await report.SetBatchVariantAsync(batch.BatchId, s.EliasRecipe.Id, " ", _f.AdminId)).Success);
        Assert.True((await report.SetBatchVariantAsync(batch.BatchId, s.EliasRecipe.Id, "إنتاج الياس سُجّل أساسيًا", _f.AdminId)).Success);
        Assert.Equal(s.EliasRecipe.Id, (await db.ItemBatches.AsNoTracking().SingleAsync(b => b.Id == batch.BatchId)).CustomRecipeId);
        Assert.True(await db.AuditLogs.AnyAsync(a => a.TableName == "ItemBatches" && a.RecordId == batch.BatchId.ToString()));
        Assert.Equal(0m, await FgAsync(db, s, null));
    }
}
