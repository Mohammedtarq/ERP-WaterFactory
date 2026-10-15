using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Setup;

/// <summary>
/// يوم تجربة بشكل بيانات المعمل الحقيقية (أسماء وأرقام مصطنعة) فوق البيانات التجريبية، حتى تُرى الشاشات ممتلئة:
/// سدادات بألوان وليبل بأسماء زبائن (كل متغير مادة مستقلة)، منتج تام بطلبات خاصة (اسم خاص)،
/// مخزن تالف مختلط (تام + مواد أولية)، ومستندات متعددة الأسطر — لكشف أي اسم أو رقم مفقود في الصور.
/// </summary>
public static class TrialDayData
{
    public static async Task SeedAsync(string projectConnection)
    {
        await using var db = new ProjectDbContext(new DbContextOptionsBuilder<ProjectDbContext>().UseSqlServer(projectConnection).Options);
        var user = await db.Users.Select(u => u.Id).FirstAsync();
        var raw = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.RawMaterial);
        var fg = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.FinishedGoods);
        var w500 = await db.Items.FirstAsync(i => i.ItemCode == "W-500");
        var w1500 = await db.Items.FirstAsync(i => i.ItemCode == "W-1500");

        // مواد أولية: كل لون وكل ليبل زبون صنف مستقل (كما في نظام الرحمة)
        async Task<(Item item, int level)> Material(string code, string name)
        {
            var item = new Item { ItemCode = code, ItemName = name, SourcingMethod = SourcingMethod.Purchased, MinStockAlertLevel = 20_000 };
            db.Items.Add(item);
            await db.SaveChangesAsync();
            var piece = new ItemPackagingLevel { ItemId = item.Id, LevelName = "قطعة", ContainsQuantity = 1, EquivalentBaseUnits = 1, IsSellableUnit = false };
            db.ItemPackagingLevels.Add(piece);
            await db.SaveChangesAsync();
            return (item, piece.Id);
        }
        var white = await Material("RH-CAP-001", "سدادة — أبيض");
        var black = await Material("RH-CAP-002", "سدادة — أسود");
        var gold = await Material("RH-CAP-003", "سدادة — ذهبي");
        var hassoun = await Material("RH-LBL-130", "ليبل - مطعم الحسون");
        var wedding = await Material("RH-LBL-131", "ليبل - زواج سعيد");

        // أسماء خاصة على المنتج التام
        var customer = new Customer { Name = "مطعم الحسون", CustomerType = CustomerType.Direct, Province = "البصرة" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        var hsRecipe = new CustomRecipe { FinishedItemId = w500.Id, CustomerId = customer.Id, Name = "مطعم الحسون — كارتون 12×500", ShortName = "مطعم الحسون" };
        var zsRecipe = new CustomRecipe { FinishedItemId = w1500.Id, Name = "زواج سعيد — شرنك 6×1.5", ShortName = "زواج سعيد" };
        db.CustomRecipes.AddRange(hsRecipe, zsRecipe);
        await db.SaveChangesAsync();

        int Level(Item item, string name) => db.ItemPackagingLevels.Local.Concat(db.ItemPackagingLevels.Where(l => l.ItemId == item.Id).ToList())
            .First(l => l.ItemId == item.Id && l.LevelName == name).Id;
        var carton = Level(w500, "كارتون");
        var shrink = Level(w1500, "شرنك");

        var svc = new WarehouseDocumentService(db);
        async Task Doc(StockDocumentRequest r)
        {
            var (result, _) = await svc.CreateAsync(r);
            if (!result.Success) throw new InvalidOperationException($"بيانات يوم التجربة: {r.Type}: {result.ErrorMessage}");
        }
        var today = DateTime.Today;

        // استلام مواد أولية بمستند واحد متعدد الأسطر (أرقام بحجم أرصدة المعمل)
        await Doc(new StockDocumentRequest(StockDocumentType.Receipt, raw.Id, today, new[]
        {
            new StockDocumentLineInput(white.item.Id, white.level, 1_322_240, NewBatchNumber: "RH-OPEN"),
            new StockDocumentLineInput(black.item.Id, black.level, 1_390_932, NewBatchNumber: "RH-OPEN-B"),
            new StockDocumentLineInput(gold.item.Id, gold.level, 49_478, NewBatchNumber: "RH-OPEN-G"),
            new StockDocumentLineInput(hassoun.item.Id, hassoun.level, 16_000),
            new StockDocumentLineInput(wedding.item.Id, wedding.level, 71_736, NewBatchNumber: "RH-LBL-131"),
        }, user, PartyName: "مورد الأغطية والليبل"));

        // منتج تام بأسماء خاصة: كراتين الحسون وشرنكات زواج سعيد
        await Doc(new StockDocumentRequest(StockDocumentType.Receipt, fg.Id, today, new[]
        {
            new StockDocumentLineInput(w500.Id, carton, 40, NewBatchNumber: "HS-2610", NewBatchExpiry: today.AddMonths(12), CustomRecipeId: hsRecipe.Id),
            new StockDocumentLineInput(w1500.Id, shrink, 30, NewBatchNumber: "ZS-2610", NewBatchExpiry: today.AddMonths(12), CustomRecipeId: zsRecipe.Id),
        }, user, PartyName: "خط الإنتاج 1"));

        // تالف: من المنتج التام (يذهب لمخزن التالف) ومن المواد الأولية — فيصير مخزن التالف مختلطًا
        var hsBatch = await db.ItemBatches.Where(b => b.BatchNumber == "HS-2610").Select(b => b.Id).FirstAsync();
        var goldBatch = await db.ItemBatches.Where(b => b.BatchNumber == "RH-OPEN-G").Select(b => b.Id).FirstAsync();
        await Doc(new StockDocumentRequest(StockDocumentType.Damaged, fg.Id, today,
            new[] { new StockDocumentLineInput(w500.Id, carton, 2, BatchId: hsBatch) }, user, DamageReason: DamageReason.Warehouse));
        await Doc(new StockDocumentRequest(StockDocumentType.Damaged, raw.Id, today,
            new[] { new StockDocumentLineInput(gold.item.Id, gold.level, 1_200, BatchId: goldBatch) }, user, DamageReason: DamageReason.Production));

        // حد تنبيه لاسم خاص واحد (50 كرتون والرصيد 38 ← تحت الحد وفي الجرس)؛ زواج سعيد بلا حد (غير متابَع)
        db.VariantAlerts.Add(new VariantAlert { ItemId = w500.Id, CustomRecipeId = hsRecipe.Id, MinPacks = 50, UpdatedByUserId = user });
        await db.SaveChangesAsync();

        // استهلاك ليبل في الإنتاج (المواد الأولية لا تُصرف صرفًا حرًا، بل لأمر إنتاج)
        db.StockTransactions.Add(new StockTransaction { ItemId = hassoun.item.Id, WarehouseId = raw.Id, QuantityBaseUnits = -400,
                                                        TransactionType = StockTransactionType.ProductionConsume, CreatedByUserId = user });
        await db.SaveChangesAsync();
    }
}
