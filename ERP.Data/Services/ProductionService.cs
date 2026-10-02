using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

public record QcInput(int QualityTestId, string MeasuredValue, bool? ManualPass = null);

public class ProductionOrderRow
{
    public int Id { get; init; }
    public string MONumber { get; init; } = "";
    public string FinishedItemName { get; init; } = "";
    public string? RecipeName { get; init; }
    public decimal QuantityToProduce { get; init; }
    public decimal PackedQuantity { get; init; }
    public ProductionOrderStatus Status { get; init; }
    public string? OutputBatch { get; init; }
    public QCOverallResult? LastQc { get; init; }
    public string StageText { get; init; } = "";
}

/// <summary>
/// دورة الإنتاج: أمر من قائمة المواد (مع الوصفة المخصصة إن وُجدت) ← بدء التشغيل (صرف المواد الأولية
/// وإنشاء تشغيلة الناتج) ← فحص المختبر (فشل اختبار واحد يرفض الدفعة) ← أمر التعبئة يُدخل الوحدات
/// المعبّأة لمخزن المنتج التام ← اكتمال الأمر تلقائيًا عند تعبئة كامل الكمية.
/// </summary>
public class ProductionService
{
    /// <summary>مدة الصلاحية الافتراضية لتشغيلة الإنتاج.</summary>
    public const int DefaultShelfLifeMonths = 12;

    private readonly ProjectDbContext _db;

    public ProductionService(ProjectDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// دمج قائمة المواد مع الوصفة المخصصة: مكوّن الوصفة الذي يحدد "يستبدل" يحل محل تلك المادة،
    /// وما لا يحدد يُضاف كمكوّن إضافي. النتيجة: كمية كل مادة لكل وحدة منتج.
    /// </summary>
    public async Task<(List<(int rawItemId, decimal perUnit)> lines, string? error)> MergeRecipeAsync(int finishedItemId, int? customRecipeId)
    {
        var bom = await _db.BillOfMaterials.AsNoTracking().Include(b => b.Lines)
            .FirstOrDefaultAsync(b => b.FinishedItemId == finishedItemId && b.IsActive);
        if (bom is null || bom.Lines.Count == 0) return (new(), "لا توجد قائمة مواد لهذا المنتج — أنشئها من المخازن ← إعداد قوائم المواد");

        var merged = bom.Lines.GroupBy(l => l.RawMaterialItemId).ToDictionary(g => g.Key, g => g.Sum(l => l.QuantityPerUnit));
        if (customRecipeId is int rid)
        {
            var recipe = await _db.CustomRecipes.AsNoTracking().Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == rid);
            if (recipe is null || !recipe.IsActive) return (new(), "الوصفة المخصصة غير موجودة أو غير فعّالة");
            if (recipe.FinishedItemId != finishedItemId) return (new(), "الوصفة المخصصة تخص منتجًا آخر");
            foreach (var l in recipe.Lines)
            {
                if (l.ReplacesRawMaterialItemId is int replaced) merged.Remove(replaced);
                merged[l.ComponentItemId] = merged.GetValueOrDefault(l.ComponentItemId) + l.QuantityPerUnit;
            }
        }
        return (merged.Select(kv => (kv.Key, kv.Value)).ToList(), null);
    }

    public async Task<(FinanceOperationResult result, int? orderId)> CreateOrderAsync(
        int finishedItemId, decimal quantity, int? customRecipeId, int rawWarehouseId, int userId)
    {
        if (quantity <= 0) return (FinanceOperationResult.Fail("كمية الإنتاج يجب أن تكون أكبر من صفر"), null);
        var raw = await _db.Warehouses.FindAsync(rawWarehouseId);
        if (raw is null) return (FinanceOperationResult.Fail("اختر مخزن المواد الأولية"), null);

        var (lines, error) = await MergeRecipeAsync(finishedItemId, customRecipeId);
        if (error is not null) return (FinanceOperationResult.Fail(error), null);
        var bomId = await _db.BillOfMaterials.Where(b => b.FinishedItemId == finishedItemId && b.IsActive).Select(b => b.Id).FirstAsync();

        var order = new ProductionOrder
        {
            MONumber = $"MO-{await _db.ProductionOrders.CountAsync() + 1:D5}",
            FinishedItemId = finishedItemId, BOMId = bomId, CustomRecipeId = customRecipeId, QuantityToProduce = quantity,
            RawMaterialsWarehouseId = rawWarehouseId, CreatedByUserId = userId
        };
        foreach (var (rawItemId, perUnit) in lines)
            order.Consumptions.Add(new ProductionOrderConsumption { RawMaterialItemId = rawItemId, QuantityRequired = Math.Round(perUnit * quantity, 4) });
        _db.ProductionOrders.Add(order);
        await _db.SaveChangesAsync();
        return (FinanceOperationResult.Ok(), order.Id);
    }

    /// <summary>بدء التشغيل: يصرف كل المواد الأولية (بترتيب الصلاحية) وينشئ تشغيلة الناتج — كله أو لا شيء.</summary>
    public async Task<FinanceOperationResult> StartAsync(int orderId, int userId)
    {
        await using var tx = await _db.Database.BeginTransactionAsync();
        var order = await _db.ProductionOrders.Include(o => o.Consumptions).Include(o => o.FinishedItem).FirstOrDefaultAsync(o => o.Id == orderId);
        if (order is null) return FinanceOperationResult.Fail("أمر الإنتاج غير موجود");
        if (order.Status != ProductionOrderStatus.Draft) return FinanceOperationResult.Fail("لا يُبدأ إلا أمر في حالة مسودة");

        // نفس محرك التوفر المستخدم في "احتياجات التصنيع" ومعاينة الأمر — يصرف من كل مخازن المواد الأولية (FEFO)
        var availability = new MaterialAvailabilityService(_db);
        foreach (var c in order.Consumptions)
        {
            var (alloc, error) = await availability.AllocateAsync(c.RawMaterialItemId, c.QuantityRequired, order.RawMaterialsWarehouseId);
            if (error is not null) return FinanceOperationResult.Fail(error);
            foreach (var a in alloc)
                _db.StockTransactions.Add(new StockTransaction { ItemId = c.RawMaterialItemId, WarehouseId = a.WarehouseId, BatchId = a.BatchId,
                                                                 QuantityBaseUnits = -a.Quantity, TransactionType = StockTransactionType.ProductionConsume,
                                                                 ReferenceTable = "ProductionOrders", ReferenceId = order.Id, CreatedByUserId = userId });
            c.QuantityConsumed = c.QuantityRequired;
            await _db.SaveChangesAsync();
        }

        var batch = new ItemBatch
        {
            ItemId = order.FinishedItemId, BatchNumber = order.MONumber, ManufactureDate = DateTime.Today,
            ExpiryDate = DateTime.Today.AddMonths(DefaultShelfLifeMonths), ProductionOrderId = order.Id
        };
        _db.ItemBatches.Add(batch);
        await _db.SaveChangesAsync();
        order.OutputBatchId = batch.Id;
        order.Status = ProductionOrderStatus.InProgress;
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return FinanceOperationResult.Ok();
    }

    public Task<List<QualityTest>> GetApplicableTestsAsync(int finishedItemId) =>
        _db.QualityTests.AsNoTracking().Where(t => t.ApplicableItemId == null || t.ApplicableItemId == finishedItemId).OrderBy(t => t.TestName).ToListAsync();

    /// <summary>
    /// نتيجة اختبار واحد: الرقمي ضمن الحدين = ناجح؛ الوصفي يطابق المعيار = ناجح (أو حسب قرار الفاحص اليدوي).
    /// </summary>
    public static QCLineResult Evaluate(QualityTest test, string measured, bool? manualPass)
    {
        if (manualPass is bool m) return m ? QCLineResult.Pass : QCLineResult.Fail;
        if (test.StandardMin is not null || test.StandardMax is not null)
        {
            if (!decimal.TryParse(measured.Trim(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var v))
                return QCLineResult.Fail;
            return (test.StandardMin is null || v >= test.StandardMin) && (test.StandardMax is null || v <= test.StandardMax)
                ? QCLineResult.Pass : QCLineResult.Fail;
        }
        return string.Equals(measured.Trim(), test.StandardText?.Trim(), StringComparison.OrdinalIgnoreCase) ? QCLineResult.Pass : QCLineResult.Fail;
    }

    /// <summary>فحص الدفعة: فشل اختبار واحد فقط يرفض الدفعة كاملة (القاعدة المتفق عليها).</summary>
    public async Task<(FinanceOperationResult result, QCOverallResult? overall)> RecordQcAsync(int orderId, IReadOnlyCollection<QcInput> inputs, int userId)
    {
        var order = await _db.ProductionOrders.FirstOrDefaultAsync(o => o.Id == orderId);
        if (order is null) return (FinanceOperationResult.Fail("أمر الإنتاج غير موجود"), null);
        if (order.Status != ProductionOrderStatus.InProgress || order.OutputBatchId is null)
            return (FinanceOperationResult.Fail("الفحص يكون لأمر قيد التشغيل"), null);

        var tests = (await GetApplicableTestsAsync(order.FinishedItemId)).ToDictionary(t => t.Id);
        if (tests.Count == 0) return (FinanceOperationResult.Fail("لا توجد اختبارات جودة معرّفة لهذا المنتج — أضفها من تبويب اختبارات الجودة"), null);
        var missing = tests.Keys.Except(inputs.Where(i => !string.IsNullOrWhiteSpace(i.MeasuredValue) || i.ManualPass is not null).Select(i => i.QualityTestId)).ToList();
        if (missing.Count > 0) return (FinanceOperationResult.Fail("أدخل نتيجة كل الاختبارات: " + string.Join("، ", missing.Select(id => tests[id].TestName))), null);

        var result = new QCBatchResult { ProductionOrderId = orderId, BatchId = order.OutputBatchId.Value, TestedByUserId = userId };
        foreach (var i in inputs.Where(i => tests.ContainsKey(i.QualityTestId)))
            result.ResultLines.Add(new QCTestResultLine { QualityTestId = i.QualityTestId, MeasuredValue = i.MeasuredValue.Trim(),
                                                          Result = Evaluate(tests[i.QualityTestId], i.MeasuredValue, i.ManualPass) });
        result.OverallResult = result.ResultLines.Any(l => l.Result == QCLineResult.Fail) ? QCOverallResult.Rejected : QCOverallResult.Passed;
        _db.QCBatchResults.Add(result);
        await _db.SaveChangesAsync();
        return (FinanceOperationResult.Ok(), result.OverallResult);
    }

    public async Task<decimal> PackedQuantityAsync(int orderId) =>
        await _db.PackingOrders.Where(p => p.ProductionOrderId == orderId)
                               .SumAsync(p => (decimal?)(p.UnitsPackaged * p.PackagingLevel.EquivalentBaseUnits)) ?? 0;

    /// <summary>
    /// أمر التعبئة: يُسمح فقط بعد نجاح آخر فحص مختبر. يُدخل الوحدات المعبّأة (بالقطعة) لمخزن المنتج التام
    /// بتشغيلة الإنتاج، ولا يتجاوز مجموع المعبّأ كمية الأمر. يكتمل الأمر تلقائيًا عند تعبئة كامل الكمية.
    /// </summary>
    public async Task<FinanceOperationResult> PackAsync(int orderId, int packagingLevelId, decimal units, int finishedGoodsWarehouseId, int userId)
    {
        if (units <= 0) return FinanceOperationResult.Fail("عدد الوحدات المعبّأة يجب أن يكون أكبر من صفر");
        await using var tx = await _db.Database.BeginTransactionAsync();
        var order = await _db.ProductionOrders.FirstOrDefaultAsync(o => o.Id == orderId);
        if (order is null) return FinanceOperationResult.Fail("أمر الإنتاج غير موجود");
        if (order.Status != ProductionOrderStatus.InProgress) return FinanceOperationResult.Fail("التعبئة تكون لأمر قيد التشغيل");

        var lastQc = await _db.QCBatchResults.Where(q => q.ProductionOrderId == orderId).OrderByDescending(q => q.Id)
                                             .Select(q => (QCOverallResult?)q.OverallResult).FirstOrDefaultAsync();
        if (lastQc is null) return FinanceOperationResult.Fail("لا يمكن التعبئة قبل فحص المختبر");
        if (lastQc == QCOverallResult.Rejected) return FinanceOperationResult.Fail("الدفعة مرفوضة من المختبر — لا يمكن تعبئتها");

        var level = await _db.ItemPackagingLevels.FirstOrDefaultAsync(l => l.Id == packagingLevelId && l.ItemId == order.FinishedItemId);
        if (level is null) return FinanceOperationResult.Fail("وحدة التعبئة لا تخص هذا المنتج");
        var fg = await _db.Warehouses.FindAsync(finishedGoodsWarehouseId);
        if (fg is null || fg.WarehouseType == WarehouseType.Damaged) return FinanceOperationResult.Fail("اختر مخزن المنتج التام");

        var pieces = units * level.EquivalentBaseUnits;
        var packed = await PackedQuantityAsync(orderId);
        if (packed + pieces > order.QuantityToProduce)
            return FinanceOperationResult.Fail($"التعبئة تتجاوز كمية الأمر: المتبقي {order.QuantityToProduce - packed:N0} قطعة فقط");

        _db.PackingOrders.Add(new PackingOrder { ProductionOrderId = orderId, PackagingLevelId = level.Id, UnitsPackaged = units,
                                                 ResultingFinishedGoodsWarehouseId = fg.Id, CreatedByUserId = userId });
        _db.StockTransactions.Add(new StockTransaction { ItemId = order.FinishedItemId, WarehouseId = fg.Id, BatchId = order.OutputBatchId,
                                                         QuantityBaseUnits = pieces, TransactionType = StockTransactionType.ProductionOutput,
                                                         ReferenceTable = "ProductionOrders", ReferenceId = orderId, CreatedByUserId = userId });
        if (packed + pieces == order.QuantityToProduce) order.Status = ProductionOrderStatus.Completed;
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return FinanceOperationResult.Ok();
    }

    /// <summary>إغلاق أمر قيد التشغيل بما عُبّئ فعلًا (الفرق = هدر إنتاج).</summary>
    public async Task<FinanceOperationResult> CompleteAsync(int orderId)
    {
        var order = await _db.ProductionOrders.FirstOrDefaultAsync(o => o.Id == orderId);
        if (order?.Status != ProductionOrderStatus.InProgress) return FinanceOperationResult.Fail("الإغلاق يكون لأمر قيد التشغيل");
        if (await PackedQuantityAsync(orderId) == 0) return FinanceOperationResult.Fail("لم تُعبّأ أي كمية بعد؛ استخدم الإلغاء بدل الإغلاق");
        order.Status = ProductionOrderStatus.Completed;
        await _db.SaveChangesAsync();
        return FinanceOperationResult.Ok();
    }

    /// <summary>الإلغاء: المسودة بلا أثر؛ الأمر قيد التشغيل تُعتبر مواده المصروفة هدرًا (لا تُعاد تلقائيًا).</summary>
    public async Task<FinanceOperationResult> CancelAsync(int orderId)
    {
        var order = await _db.ProductionOrders.FirstOrDefaultAsync(o => o.Id == orderId);
        if (order is null) return FinanceOperationResult.Fail("أمر الإنتاج غير موجود");
        if (order.Status is ProductionOrderStatus.Completed or ProductionOrderStatus.Cancelled)
            return FinanceOperationResult.Fail("الأمر مكتمل أو ملغى مسبقًا");
        if (order.Status == ProductionOrderStatus.InProgress && await PackedQuantityAsync(orderId) > 0)
            return FinanceOperationResult.Fail("عُبّئت كمية من هذا الأمر؛ أغلقه بدل إلغائه");
        order.Status = ProductionOrderStatus.Cancelled;
        await _db.SaveChangesAsync();
        return FinanceOperationResult.Ok();
    }

    public async Task<List<ProductionOrderRow>> GetOrdersAsync()
    {
        var orders = await _db.ProductionOrders.AsNoTracking()
            .Select(o => new
            {
                o.Id, o.MONumber, Item = o.FinishedItem.ItemName, Recipe = o.CustomRecipe != null ? o.CustomRecipe.Name : null,
                o.QuantityToProduce, o.Status, Batch = o.OutputBatch != null ? o.OutputBatch.BatchNumber : null,
                LastQc = _db.QCBatchResults.Where(q => q.ProductionOrderId == o.Id).OrderByDescending(q => q.Id).Select(q => (QCOverallResult?)q.OverallResult).FirstOrDefault(),
                Packed = _db.PackingOrders.Where(p => p.ProductionOrderId == o.Id).Sum(p => (decimal?)(p.UnitsPackaged * p.PackagingLevel.EquivalentBaseUnits)) ?? 0
            })
            .OrderByDescending(o => o.Id).ToListAsync();

        return orders.Select(o => new ProductionOrderRow
        {
            Id = o.Id, MONumber = o.MONumber, FinishedItemName = o.Item, RecipeName = o.Recipe, QuantityToProduce = o.QuantityToProduce,
            PackedQuantity = o.Packed, Status = o.Status, OutputBatch = o.Batch, LastQc = o.LastQc,
            StageText = o.Status switch
            {
                ProductionOrderStatus.Draft => "مسودة — بانتظار بدء التشغيل",
                ProductionOrderStatus.InProgress when o.LastQc is null => "قيد التشغيل — بانتظار فحص المختبر",
                ProductionOrderStatus.InProgress when o.LastQc == QCOverallResult.Rejected => "مرفوضة من المختبر",
                ProductionOrderStatus.InProgress => "ناجحة بالمختبر — بانتظار التعبئة",
                ProductionOrderStatus.Completed => "مكتمل",
                _ => "ملغى"
            }
        }).ToList();
    }
}
