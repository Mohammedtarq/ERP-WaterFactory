using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

public record QcInput(int QualityTestId, string MeasuredValue, bool? ManualPass = null);

/// <summary>صنف في أمر إنتاج جديد: المنتج، الكمية، الوصفة المخصصة، ورقم دفعة معدَّل (فارغ = تلقائي).</summary>
public record ProductionLineInput(int FinishedItemId, decimal Quantity, int? CustomRecipeId = null, string? BatchNumber = null);

/// <summary>سطر أمر إنتاج للعرض في شاشات الفحص والتعبئة (كل سطر بدفعته).</summary>
public class ProductionLineRow
{
    public int LineId { get; init; }
    public int OrderId { get; init; }
    public int LineNo { get; init; }
    public string MONumber { get; init; } = "";
    public int FinishedItemId { get; init; }
    public string FinishedItemName { get; init; } = "";
    public string? RecipeName { get; init; }
    public decimal QuantityToProduce { get; init; }
    public decimal PackedQuantity { get; init; }
    public string? OutputBatch { get; init; }
    public QCOverallResult? LastQc { get; init; }
    public ProductionOrderStatus Status { get; init; }
    public string? MachineName { get; init; }
    public string StageText { get; init; } = "";
}

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
    public string? MachineName { get; init; }
    public QCOverallResult? LastQc { get; init; }
    public string StageText { get; init; } = "";
    public int LinesCount { get; init; } = 1;
    public IReadOnlyList<ProductionLineRow> Lines { get; init; } = Array.Empty<ProductionLineRow>();
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

    /// <summary>أمر إنتاج بصنف واحد (اختصار للأمر متعدد الأصناف).</summary>
    public Task<(FinanceOperationResult result, int? orderId)> CreateOrderAsync(
        int finishedItemId, decimal quantity, int? customRecipeId, int rawWarehouseId, int? machineId, int userId, string? batchNumber = null) =>
        CreateOrderAsync(new[] { new ProductionLineInput(finishedItemId, quantity, customRecipeId, batchNumber) }, rawWarehouseId, machineId, userId);

    /// <summary>
    /// أمر إنتاج متعدد الأصناف: لكل صنف وصفته (قائمة المواد + الوصفة المخصصة) وكميته ودفعته الخاصة.
    /// مكونات كل صنف تُحفظ منفصلة (مرتبطة بسطره)، والمواد المشتركة تُجمَّع عند العرض وفحص التوفر.
    /// </summary>
    public async Task<(FinanceOperationResult result, int? orderId)> CreateOrderAsync(
        IReadOnlyList<ProductionLineInput> inputs, int rawWarehouseId, int? machineId, int userId)
    {
        if (inputs.Count == 0) return (FinanceOperationResult.Fail("أضف صنفًا واحدًا على الأقل للأمر"), null);
        if (inputs.Any(i => i.Quantity <= 0)) return (FinanceOperationResult.Fail("كمية الإنتاج يجب أن تكون أكبر من صفر لكل صنف"), null);
        if (inputs.GroupBy(i => i.FinishedItemId).Any(g => g.Count() > 1))
            return (FinanceOperationResult.Fail("الصنف مكرر في الأمر — اجمع كميته في سطر واحد"), null);
        var customBatches = inputs.Where(i => !string.IsNullOrWhiteSpace(i.BatchNumber)).Select(i => i.BatchNumber!.Trim()).ToList();
        if (customBatches.Count != customBatches.Distinct().Count()) return (FinanceOperationResult.Fail("رقم الدفعة مكرر بين أصناف الأمر"), null);
        var raw = await _db.Warehouses.FindAsync(rawWarehouseId);
        if (raw is null) return (FinanceOperationResult.Fail("اختر مخزن المواد الأولية"), null);
        var machine = machineId is int mid ? await _db.Machines.FindAsync(mid) : null;
        if (machine is null || !machine.IsActive) return (FinanceOperationResult.Fail("اختر الماكينة التي سيُصرف لها ويُنتج عليها (الإنتاج ← الماكينات)"), null);

        var recipes = new List<(ProductionLineInput input, int bomId, List<(int rawItemId, decimal perUnit)> lines)>();
        foreach (var i in inputs)
        {
            var (lines, error) = await MergeRecipeAsync(i.FinishedItemId, i.CustomRecipeId);
            if (error is not null)
            {
                var name = await _db.Items.Where(x => x.Id == i.FinishedItemId).Select(x => x.ItemName).FirstOrDefaultAsync();
                return (FinanceOperationResult.Fail(inputs.Count > 1 ? $"{name}: {error}" : error), null);
            }
            var bomId = await _db.BillOfMaterials.Where(b => b.FinishedItemId == i.FinishedItemId && b.IsActive).Select(b => b.Id).FirstAsync();
            recipes.Add((i, bomId, lines));
        }
        foreach (var number in customBatches)
        {
            var batchError = await ValidateBatchNumberAsync(number, null);
            if (batchError is not null) return (FinanceOperationResult.Fail(batchError), null);
        }

        var first = recipes[0];
        var order = new ProductionOrder
        {
            MONumber = $"MO-{await _db.ProductionOrders.CountAsync() + 1:D5}",
            FinishedItemId = first.input.FinishedItemId, BOMId = first.bomId, CustomRecipeId = first.input.CustomRecipeId,
            QuantityToProduce = inputs.Sum(i => i.Quantity), RawMaterialsWarehouseId = rawWarehouseId, MachineId = machine.Id, CreatedByUserId = userId
        };
        await using var tx = await _db.Database.BeginTransactionAsync();
        _db.ProductionOrders.Add(order);
        await _db.SaveChangesAsync();

        var no = 0;
        foreach (var (input, bomId, lines) in recipes)
        {
            var line = new ProductionOrderLine
            {
                ProductionOrderId = order.Id, LineNo = ++no, FinishedItemId = input.FinishedItemId, BOMId = bomId,
                CustomRecipeId = input.CustomRecipeId, QuantityToProduce = input.Quantity
            };
            _db.ProductionOrderLines.Add(line);
            await _db.SaveChangesAsync();
            foreach (var (rawItemId, perUnit) in lines)
                _db.ProductionOrderConsumptions.Add(new ProductionOrderConsumption
                {
                    ProductionOrderId = order.Id, ProductionOrderLineId = line.Id, RawMaterialItemId = rawItemId,
                    QuantityRequired = Math.Round(perUnit * input.Quantity, 4)
                });

            // رقم الدفعة: المولَّد تلقائيًا، أو ما عدّله المستخدم قبل الإنشاء (يُحفظ الأصلي ويُسجَّل التعديل)
            var generated = await NextBatchNumberAsync();
            var chosen = string.IsNullOrWhiteSpace(input.BatchNumber) ? generated : input.BatchNumber.Trim();
            var batch = new ItemBatch
            {
                ItemId = input.FinishedItemId, BatchNumber = chosen, ProductionOrderId = order.Id,
                OriginalBatchNumber = chosen == generated ? null : generated
            };
            _db.ItemBatches.Add(batch);
            await _db.SaveChangesAsync();
            line.OutputBatchId = batch.Id;
            if (line.LineNo == 1) order.OutputBatchId = batch.Id;
            if (chosen != generated)
                _db.BatchNumberChanges.Add(new BatchNumberChange { BatchId = batch.Id, OldNumber = generated, NewNumber = chosen, Reason = "تعديل عند إنشاء الأمر", ChangedByUserId = userId });
            await _db.SaveChangesAsync();
        }
        await tx.CommitAsync();
        return (FinanceOperationResult.Ok(), order.Id);
    }

    /// <summary>الرقم التلقائي التالي لدفعة إنتاج: B + تاريخ اليوم + تسلسل يومي (مثال B261002-003).</summary>
    public async Task<string> NextBatchNumberAsync()
    {
        var prefix = $"B{DateTime.Today:yyMMdd}-";
        var used = await _db.ItemBatches.Where(b => b.ProductionOrderId != null && (b.BatchNumber.StartsWith(prefix) || (b.OriginalBatchNumber != null && b.OriginalBatchNumber.StartsWith(prefix))))
                                        .Select(b => new { b.BatchNumber, b.OriginalBatchNumber }).ToListAsync();
        var max = used.SelectMany(u => new[] { u.BatchNumber, u.OriginalBatchNumber })
                      .Where(n => n != null && n.StartsWith(prefix))
                      .Select(n => int.TryParse(n![prefix.Length..], out var x) ? x : 0)
                      .DefaultIfEmpty(0).Max();
        return $"{prefix}{max + 1:D3}";
    }

    private async Task<string?> ValidateBatchNumberAsync(string number, int? exceptBatchId)
    {
        if (string.IsNullOrWhiteSpace(number)) return "أدخل رقم الدفعة";
        if (number.Length > 50) return "رقم الدفعة طويل جدًا (50 حرفًا كحد أقصى)";
        var except = exceptBatchId ?? 0;
        if (await _db.ItemBatches.AnyAsync(b => b.ProductionOrderId != null && b.BatchNumber == number && b.Id != except))
            return $"رقم الدفعة \"{number}\" مستخدم لدفعة إنتاج أخرى — أرقام الدفعات يجب أن تكون فريدة";
        return null;
    }

    /// <summary>
    /// تعديل رقم دفعة سطر (صنف) في أمر إنتاج: يُحفظ الرقم الأصلي (المولَّد) ويُسجَّل كل تعديل بمن غيّره ومتى.
    /// الدفعة نفسها تُعاد تسميتها، فتبقى الحركات ونتائج المختبر والتعبئة مرتبطة بها.
    /// </summary>
    public async Task<FinanceOperationResult> ChangeLineBatchNumberAsync(int lineId, string newNumber, string? reason, int userId)
    {
        var line = await _db.ProductionOrderLines.Include(l => l.OutputBatch).Include(l => l.ProductionOrder).FirstOrDefaultAsync(l => l.Id == lineId);
        if (line?.OutputBatch is null) return FinanceOperationResult.Fail("السطر بلا دفعة");
        if (line.ProductionOrder.Status == ProductionOrderStatus.Cancelled) return FinanceOperationResult.Fail("الأمر ملغى");
        newNumber = (newNumber ?? "").Trim();
        if (newNumber == line.OutputBatch.BatchNumber) return FinanceOperationResult.Fail("الرقم الجديد مطابق للحالي");
        var error = await ValidateBatchNumberAsync(newNumber, line.OutputBatch.Id);
        if (error is not null) return FinanceOperationResult.Fail(error);
        _db.BatchNumberChanges.Add(new BatchNumberChange
        {
            BatchId = line.OutputBatch.Id, OldNumber = line.OutputBatch.BatchNumber, NewNumber = newNumber,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(), ChangedByUserId = userId
        });
        line.OutputBatch.OriginalBatchNumber ??= line.OutputBatch.BatchNumber;
        line.OutputBatch.BatchNumber = newNumber;
        await _db.SaveChangesAsync();
        return FinanceOperationResult.Ok();
    }

    /// <summary>تعديل رقم دفعة الصنف الأول في الأمر (الأوامر ذات الصنف الواحد).</summary>
    public async Task<FinanceOperationResult> ChangeBatchNumberAsync(int orderId, string newNumber, string? reason, int userId)
    {
        var lineId = await _db.ProductionOrderLines.Where(l => l.ProductionOrderId == orderId).OrderBy(l => l.LineNo).Select(l => (int?)l.Id).FirstOrDefaultAsync();
        return lineId is null ? FinanceOperationResult.Fail("الأمر بلا دفعة") : await ChangeLineBatchNumberAsync(lineId.Value, newNumber, reason, userId);
    }

    /// <summary>سجل تعديلات أرقام دفعات كل أصناف الأمر.</summary>
    public Task<List<BatchNumberChange>> GetBatchHistoryAsync(int orderId) =>
        _db.BatchNumberChanges.AsNoTracking().Include(c => c.ChangedByUser).Include(c => c.Batch)
           .Where(c => _db.ProductionOrderLines.Any(l => l.ProductionOrderId == orderId && l.OutputBatchId == c.BatchId))
           .OrderBy(c => c.Id).ToListAsync();

    /// <summary>المختبر يربط النتيجة بأمر الإنتاج (وصنفه) عبر رقم الدفعة فقط.</summary>
    public async Task<(FinanceOperationResult result, QCOverallResult? overall)> RecordQcByBatchAsync(string batchNumber, IReadOnlyCollection<QcInput> inputs, int userId)
    {
        batchNumber = (batchNumber ?? "").Trim();
        var lineId = await _db.ProductionOrderLines.Where(l => l.OutputBatch != null && l.OutputBatch.BatchNumber == batchNumber)
                                                   .Select(l => (int?)l.Id).FirstOrDefaultAsync();
        if (lineId is null) return (FinanceOperationResult.Fail($"لا توجد دفعة إنتاج بالرقم \"{batchNumber}\""), null);
        return await RecordQcForLineAsync(lineId.Value, inputs, userId);
    }

    /// <summary>
    /// بدء التشغيل = صرف مواد لأمر إنتاج: تُنقل المواد الأولية (بترتيب الصلاحية، من كل مخازن المواد الأولية)
    /// إلى رصيد "تحت التصنيع" للماكينة — كله أو لا شيء. في الأمر متعدد الأصناف يُفحص التوفر على المجموع
    /// للمواد المشتركة أولًا، ثم يُصرف لكل صنف على حدة بترتيب الصلاحية دون استخدام نفس الكمية مرتين.
    /// </summary>
    public async Task<FinanceOperationResult> StartAsync(int orderId, int userId)
    {
        await using var tx = await _db.Database.BeginTransactionAsync();
        var order = await _db.ProductionOrders.Include(o => o.Consumptions).Include(o => o.Machine)
                                              .Include(o => o.Lines).ThenInclude(l => l.FinishedItem)
                                              .FirstOrDefaultAsync(o => o.Id == orderId);
        if (order is null) return FinanceOperationResult.Fail("أمر الإنتاج غير موجود");
        if (order.Status != ProductionOrderStatus.Draft) return FinanceOperationResult.Fail("لا يُبدأ إلا أمر في حالة مسودة");
        if (order.Machine is null) return FinanceOperationResult.Fail("الأمر غير مرتبط بماكينة — ألغه وأنشئ أمرًا جديدًا مع اختيار الماكينة");

        // فحص التوفر على مجموع كل مادة عبر الأصناف (نفس محرك "احتياجات التصنيع")
        var totals = order.Consumptions.GroupBy(c => c.RawMaterialItemId).ToDictionary(g => g.Key, g => g.Sum(c => c.QuantityRequired));
        var availability = await new MaterialAvailabilityService(_db).GetAsync(totals.Keys.ToList(), order.RawMaterialsWarehouseId);
        foreach (var (itemId, required) in totals)
        {
            var a = availability[itemId];
            if (a.Available >= required) continue;
            var users = order.Consumptions.Where(c => c.RawMaterialItemId == itemId && c.ProductionOrderLineId != null)
                             .Select(c => order.Lines.First(l => l.Id == c.ProductionOrderLineId).FinishedItem.ItemName).Distinct().ToList();
            var msg = $"الرصيد غير كافٍ للصنف \"{a.ItemName}\": المطلوب {required:0.###}"
                      + (users.Count > 1 ? $" (مشترك بين: {string.Join("، ", users)})" : "")
                      + $"، المتاح في مخازن المواد الأولية {a.Available:0.###}";
            if (a.ElsewhereQuantity > 0) msg += $" (يوجد {a.ElsewhereQuantity:0.###} في مخازن أخرى — انقله إلى مخزن المواد الأولية أولًا)";
            return FinanceOperationResult.Fail(msg);
        }

        foreach (var c in order.Consumptions.OrderBy(c => c.ProductionOrderLineId).ThenBy(c => c.Id))
        {
            var error = await IssueToMachineAsync(order, c.RawMaterialItemId, c.QuantityRequired, userId);
            if (error is not null) return FinanceOperationResult.Fail(error);
        }

        // الدفعات أُنشئت مع الأمر بأرقامها؛ تاريخ التصنيع والصلاحية من يوم التشغيل
        foreach (var line in order.Lines)
        {
            var batch = line.OutputBatchId is int bid ? await _db.ItemBatches.FindAsync(bid) : null;
            if (batch is null)
            {
                batch = new ItemBatch { ItemId = line.FinishedItemId, BatchNumber = await NextBatchNumberAsync(), ProductionOrderId = order.Id };
                _db.ItemBatches.Add(batch);
                await _db.SaveChangesAsync();
                line.OutputBatchId = batch.Id;
                if (line.LineNo == 1) order.OutputBatchId = batch.Id;
            }
            batch.ManufactureDate = DateTime.Today;
            batch.ExpiryDate = DateTime.Today.AddMonths(DefaultShelfLifeMonths);
        }
        order.Status = ProductionOrderStatus.InProgress;
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return FinanceOperationResult.Ok();
    }

    /// <summary>
    /// صرف مادة لأمر إنتاج: من مخازن المواد الأولية (نفس محرك التوفر المستخدم في "احتياجات التصنيع"، FEFO)
    /// إلى رصيد تحت التصنيع للماكينة، بنفس التشغيلة، وكلا الطرفين مرجعهما الأمر.
    /// </summary>
    private async Task<string?> IssueToMachineAsync(ProductionOrder order, int rawItemId, decimal quantity, int userId)
    {
        var (alloc, error) = await new MaterialAvailabilityService(_db).AllocateAsync(rawItemId, quantity, order.RawMaterialsWarehouseId);
        if (error is not null) return error;
        foreach (var a in alloc)
        {
            _db.StockTransactions.Add(new StockTransaction { ItemId = rawItemId, WarehouseId = a.WarehouseId, BatchId = a.BatchId, QuantityBaseUnits = -a.Quantity,
                                                             TransactionType = StockTransactionType.WipIssue, ReferenceTable = "ProductionOrders", ReferenceId = order.Id, CreatedByUserId = userId });
            _db.StockTransactions.Add(new StockTransaction { ItemId = rawItemId, WarehouseId = order.Machine!.WipWarehouseId, BatchId = a.BatchId, QuantityBaseUnits = a.Quantity,
                                                             TransactionType = StockTransactionType.WipIssue, ReferenceTable = "ProductionOrders", ReferenceId = order.Id, CreatedByUserId = userId });
        }
        await _db.SaveChangesAsync();
        return null;
    }

    /// <summary>صرف إضافي لأمر قيد التشغيل (مثلًا بعد تالف أكبر من المتوقع) — يذهب لتحت تصنيع ماكينة الأمر.</summary>
    public async Task<FinanceOperationResult> IssueAdditionalAsync(int orderId, int rawItemId, decimal quantity, int userId)
    {
        if (quantity <= 0) return FinanceOperationResult.Fail("الكمية يجب أن تكون أكبر من صفر");
        await using var tx = await _db.Database.BeginTransactionAsync();
        var order = await _db.ProductionOrders.Include(o => o.Consumptions).Include(o => o.Machine).FirstOrDefaultAsync(o => o.Id == orderId);
        if (order is null) return FinanceOperationResult.Fail("أمر الإنتاج غير موجود");
        if (order.Status != ProductionOrderStatus.InProgress || order.Machine is null)
            return FinanceOperationResult.Fail("الصرف الإضافي يكون لأمر قيد التشغيل مرتبط بماكينة");
        if (order.Consumptions.All(c => c.RawMaterialItemId != rawItemId))
            return FinanceOperationResult.Fail("المادة ليست من مكونات هذا الأمر");
        var error = await IssueToMachineAsync(order, rawItemId, quantity, userId);
        if (error is not null) return FinanceOperationResult.Fail(error);
        await tx.CommitAsync();
        return FinanceOperationResult.Ok();
    }

    /// <summary>
    /// الاستهلاك الفعلي لصنف = مكوناته (قائمة مواده) × الكمية المُنتَجة منه فعلًا، يُخصم من تحت تصنيع ماكينة الأمر.
    /// يرفض إن لم يكفِ رصيد الماكينة (يُصرف إضافي أولًا) دون أي خصم جزئي.
    /// </summary>
    /// <summary>كلفة المواد المستهلكة في آخر تعبئة (بالمتوسط المرجّح لحظة الاستهلاك) — تصبح كلفة القطع المعبّأة.</summary>
    private decimal _lastConsumedCost;

    private async Task<string?> ConsumeForProducedAsync(ProductionOrder order, ProductionOrderLine line, decimal pieces, int userId)
    {
        _lastConsumedCost = 0;
        if (order.Machine is null) return null;   // أمر قديم: استُهلكت مواده كاملة عند البدء
        var plan = new List<(ProductionOrderConsumption c, decimal qty)>();
        foreach (var c in order.Consumptions.Where(c => c.ProductionOrderLineId == line.Id))
        {
            var qty = Math.Round(c.QuantityRequired / line.QuantityToProduce * pieces, 3);
            if (qty <= 0) continue;
            var balance = await LedgerHelper.BalanceAsync(_db, c.RawMaterialItemId, order.Machine.WipWarehouseId);
            if (balance < qty)
            {
                var name = await _db.Items.Where(i => i.Id == c.RawMaterialItemId).Select(i => i.ItemName).FirstAsync();
                return $"رصيد تحت التصنيع في {order.Machine.Name} لا يكفي من \"{name}\": المطلوب للإنتاج {qty:0.###}، المتبقي {balance:0.###} — اصرف كمية إضافية للأمر أولًا";
            }
            plan.Add((c, qty));
        }
        foreach (var (c, qty) in plan)
        {
            var (alloc, error) = await LedgerHelper.AllocateAsync(_db, c.RawMaterialItemId, order.Machine.WipWarehouseId, null, qty);
            if (error is not null) return error;
            _lastConsumedCost += qty * (await _db.Items.Where(i => i.Id == c.RawMaterialItemId).Select(i => i.CostPrice).FirstAsync() ?? 0);
            foreach (var (batchId, q) in alloc)
                _db.StockTransactions.Add(new StockTransaction { ItemId = c.RawMaterialItemId, WarehouseId = order.Machine.WipWarehouseId, BatchId = batchId,
                                                                 QuantityBaseUnits = -q, TransactionType = StockTransactionType.ProductionConsume,
                                                                 ReferenceTable = "ProductionOrders", ReferenceId = order.Id, CreatedByUserId = userId });
            c.QuantityConsumed += qty;
            await _db.SaveChangesAsync();   // حتى يرى التوزيع التالي لنفس المادة (صنف آخر) الرصيد بعد الخصم
        }
        return null;
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

    /// <summary>فحص دفعة الصنف الأول في الأمر (الأوامر ذات الصنف الواحد).</summary>
    public async Task<(FinanceOperationResult result, QCOverallResult? overall)> RecordQcAsync(int orderId, IReadOnlyCollection<QcInput> inputs, int userId)
    {
        var lineId = await _db.ProductionOrderLines.Where(l => l.ProductionOrderId == orderId).OrderBy(l => l.LineNo).Select(l => (int?)l.Id).FirstOrDefaultAsync();
        return lineId is null ? (FinanceOperationResult.Fail("أمر الإنتاج غير موجود"), null) : await RecordQcForLineAsync(lineId.Value, inputs, userId);
    }

    /// <summary>فحص دفعة صنف: فشل اختبار واحد فقط يرفض الدفعة كاملة (القاعدة المتفق عليها).</summary>
    public async Task<(FinanceOperationResult result, QCOverallResult? overall)> RecordQcForLineAsync(int lineId, IReadOnlyCollection<QcInput> inputs, int userId)
    {
        var line = await _db.ProductionOrderLines.Include(l => l.ProductionOrder).FirstOrDefaultAsync(l => l.Id == lineId);
        if (line is null) return (FinanceOperationResult.Fail("أمر الإنتاج غير موجود"), null);
        if (line.ProductionOrder.Status != ProductionOrderStatus.InProgress || line.OutputBatchId is null)
            return (FinanceOperationResult.Fail("الفحص يكون لأمر قيد التشغيل"), null);

        var tests = (await GetApplicableTestsAsync(line.FinishedItemId)).ToDictionary(t => t.Id);
        if (tests.Count == 0) return (FinanceOperationResult.Fail("لا توجد اختبارات جودة معرّفة لهذا المنتج — أضفها من تبويب اختبارات الجودة"), null);
        var missing = tests.Keys.Except(inputs.Where(i => !string.IsNullOrWhiteSpace(i.MeasuredValue) || i.ManualPass is not null).Select(i => i.QualityTestId)).ToList();
        if (missing.Count > 0) return (FinanceOperationResult.Fail("أدخل نتيجة كل الاختبارات: " + string.Join("، ", missing.Select(id => tests[id].TestName))), null);

        var result = new QCBatchResult { ProductionOrderId = line.ProductionOrderId, BatchId = line.OutputBatchId.Value, TestedByUserId = userId };
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

    private async Task<decimal> PackedLineQuantityAsync(int lineId) =>
        await _db.PackingOrders.Where(p => p.ProductionOrderLineId == lineId)
                               .SumAsync(p => (decimal?)(p.UnitsPackaged * p.PackagingLevel.EquivalentBaseUnits)) ?? 0;

    /// <summary>
    /// أمر التعبئة: الصنف يُعرف من وحدة التعبئة المختارة (لكل صنف سطر واحد في الأمر). يُسمح فقط بعد نجاح آخر
    /// فحص لدفعة ذلك الصنف، ويُدخل الوحدات المعبّأة لمخزن المنتج التام بدفعته، دون تجاوز كمية الصنف.
    /// يكتمل الأمر تلقائيًا عند تعبئة كامل كميات أصنافه.
    /// </summary>
    public async Task<FinanceOperationResult> PackAsync(int orderId, int packagingLevelId, decimal units, int finishedGoodsWarehouseId, int userId)
    {
        if (units <= 0) return FinanceOperationResult.Fail("عدد الوحدات المعبّأة يجب أن يكون أكبر من صفر");
        await using var tx = await _db.Database.BeginTransactionAsync();
        var order = await _db.ProductionOrders.Include(o => o.Consumptions).Include(o => o.Machine).Include(o => o.Lines)
                                              .FirstOrDefaultAsync(o => o.Id == orderId);
        if (order is null) return FinanceOperationResult.Fail("أمر الإنتاج غير موجود");
        if (order.Status != ProductionOrderStatus.InProgress) return FinanceOperationResult.Fail("التعبئة تكون لأمر قيد التشغيل");

        var level = await _db.ItemPackagingLevels.FirstOrDefaultAsync(l => l.Id == packagingLevelId);
        var line = level is null ? null : order.Lines.FirstOrDefault(l => l.FinishedItemId == level.ItemId);
        if (level is null || line is null) return FinanceOperationResult.Fail("وحدة التعبئة لا تخص أي صنف في هذا الأمر");

        var lastQc = await _db.QCBatchResults.Where(q => q.BatchId == line.OutputBatchId).OrderByDescending(q => q.Id)
                                             .Select(q => (QCOverallResult?)q.OverallResult).FirstOrDefaultAsync();
        if (lastQc is null) return FinanceOperationResult.Fail("لا يمكن التعبئة قبل فحص المختبر");
        if (lastQc == QCOverallResult.Rejected) return FinanceOperationResult.Fail("الدفعة مرفوضة من المختبر — لا يمكن تعبئتها");

        var fg = await _db.Warehouses.FindAsync(finishedGoodsWarehouseId);
        if (fg is null || fg.WarehouseType is WarehouseType.Damaged or WarehouseType.WorkInProcess) return FinanceOperationResult.Fail("اختر مخزن المنتج التام");

        var pieces = units * level.EquivalentBaseUnits;
        var packed = await PackedLineQuantityAsync(line.Id);
        if (packed + pieces > line.QuantityToProduce)
            return FinanceOperationResult.Fail($"التعبئة ({units:N0} {level.LevelName} = {pieces:N0} قطعة) تتجاوز كمية الأمر: المتبقي {line.QuantityToProduce - packed:N0} قطعة فقط");

        var consumeError = await ConsumeForProducedAsync(order, line, pieces, userId);
        if (consumeError is not null) return FinanceOperationResult.Fail(consumeError);

        _db.PackingOrders.Add(new PackingOrder { ProductionOrderId = orderId, ProductionOrderLineId = line.Id, PackagingLevelId = level.Id, UnitsPackaged = units,
                                                 ResultingFinishedGoodsWarehouseId = fg.Id, CreatedByUserId = userId });
        _db.StockTransactions.Add(new StockTransaction { ItemId = line.FinishedItemId, WarehouseId = fg.Id, BatchId = line.OutputBatchId,
                                                         QuantityBaseUnits = pieces, TransactionType = StockTransactionType.ProductionOutput,
                                                         UnitCost = _lastConsumedCost > 0 ? Math.Round(_lastConsumedCost / pieces, 4) : null,
                                                         ReferenceTable = "ProductionOrders", ReferenceId = orderId, CreatedByUserId = userId });
        await _db.SaveChangesAsync();
        var allPacked = true;
        foreach (var l in order.Lines)
            if (await PackedLineQuantityAsync(l.Id) < l.QuantityToProduce) { allPacked = false; break; }
        if (allPacked) order.Status = ProductionOrderStatus.Completed;
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return FinanceOperationResult.Ok();
    }

    /// <summary>إغلاق أمر قيد التشغيل بما عُبّئ فعلًا (المتبقي من المواد يبقى على الماكينة).</summary>
    public async Task<FinanceOperationResult> CompleteAsync(int orderId)
    {
        var order = await _db.ProductionOrders.FirstOrDefaultAsync(o => o.Id == orderId);
        if (order?.Status != ProductionOrderStatus.InProgress) return FinanceOperationResult.Fail("الإغلاق يكون لأمر قيد التشغيل");
        if (await PackedQuantityAsync(orderId) == 0) return FinanceOperationResult.Fail("لم تُعبّأ أي كمية بعد؛ استخدم الإلغاء بدل الإغلاق");
        order.Status = ProductionOrderStatus.Completed;
        await _db.SaveChangesAsync();
        return FinanceOperationResult.Ok();
    }

    /// <summary>الإلغاء: المسودة بلا أثر؛ الأمر قيد التشغيل تبقى مواده المصروفة على الماكينة (تالف أو إرجاع يدويًا).</summary>
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

    private static string Stage(ProductionOrderStatus status, QCOverallResult? qc) => status switch
    {
        ProductionOrderStatus.Draft => "مسودة — بانتظار بدء التشغيل",
        ProductionOrderStatus.InProgress when qc is null => "قيد التشغيل — بانتظار فحص المختبر",
        ProductionOrderStatus.InProgress when qc == QCOverallResult.Rejected => "مرفوضة من المختبر",
        ProductionOrderStatus.InProgress => "ناجحة بالمختبر — بانتظار التعبئة",
        ProductionOrderStatus.Completed => "مكتمل",
        _ => "ملغى"
    };

    /// <summary>كل سطور الأوامر (صنف + دفعة) — لشاشتي الفحص والتعبئة.</summary>
    public async Task<List<ProductionLineRow>> GetLinesAsync(int? orderId = null)
    {
        var lines = await _db.ProductionOrderLines.AsNoTracking()
            .Where(l => orderId == null || l.ProductionOrderId == orderId)
            .Select(l => new
            {
                l.Id, l.ProductionOrderId, l.LineNo, l.ProductionOrder.MONumber, l.FinishedItemId, Item = l.FinishedItem.ItemName,
                Recipe = l.CustomRecipe != null ? l.CustomRecipe.Name : null, l.QuantityToProduce, l.ProductionOrder.Status,
                Batch = l.OutputBatch != null ? l.OutputBatch.BatchNumber : null,
                Machine = l.ProductionOrder.Machine != null ? l.ProductionOrder.Machine.Name : null,
                LastQc = _db.QCBatchResults.Where(q => q.BatchId == l.OutputBatchId).OrderByDescending(q => q.Id).Select(q => (QCOverallResult?)q.OverallResult).FirstOrDefault(),
                Packed = _db.PackingOrders.Where(p => p.ProductionOrderLineId == l.Id).Sum(p => (decimal?)(p.UnitsPackaged * p.PackagingLevel.EquivalentBaseUnits)) ?? 0
            })
            .OrderByDescending(l => l.ProductionOrderId).ThenBy(l => l.LineNo).ToListAsync();
        return lines.Select(l => new ProductionLineRow
        {
            LineId = l.Id, OrderId = l.ProductionOrderId, LineNo = l.LineNo, MONumber = l.MONumber, FinishedItemId = l.FinishedItemId,
            FinishedItemName = l.Item, RecipeName = l.Recipe, QuantityToProduce = l.QuantityToProduce, PackedQuantity = l.Packed,
            OutputBatch = l.Batch, LastQc = l.LastQc, Status = l.Status, MachineName = l.Machine,
            StageText = l.Status == ProductionOrderStatus.InProgress && l.Packed >= l.QuantityToProduce ? "عُبّئ بالكامل" : Stage(l.Status, l.LastQc)
        }).ToList();
    }

    public async Task<List<ProductionOrderRow>> GetOrdersAsync()
    {
        var orders = await _db.ProductionOrders.AsNoTracking()
            .Select(o => new { o.Id, o.MONumber, o.QuantityToProduce, o.Status, Machine = o.Machine != null ? o.Machine.Name : null })
            .OrderByDescending(o => o.Id).ToListAsync();
        var lines = (await GetLinesAsync()).ToLookup(l => l.OrderId);

        return orders.Select(o =>
        {
            var mine = lines[o.Id].OrderBy(l => l.LineNo).ToList();
            // نتيجة المختبر على مستوى الأمر: مرفوض إن رُفضت دفعة، ناجح إن نجحت كل الدفعات
            QCOverallResult? qc = mine.Any(l => l.LastQc == QCOverallResult.Rejected) ? QCOverallResult.Rejected
                                : mine.Count > 0 && mine.All(l => l.LastQc == QCOverallResult.Passed) ? QCOverallResult.Passed : null;
            return new ProductionOrderRow
            {
                Id = o.Id, MONumber = o.MONumber, Status = o.Status, MachineName = o.Machine, QuantityToProduce = o.QuantityToProduce,
                FinishedItemName = string.Join(" + ", mine.Select(l => l.FinishedItemName)),
                RecipeName = mine.Any(l => l.RecipeName != null) ? string.Join("، ", mine.Where(l => l.RecipeName != null).Select(l => l.RecipeName)) : null,
                OutputBatch = mine.Count == 0 ? null : string.Join("، ", mine.Select(l => l.OutputBatch)),
                PackedQuantity = mine.Sum(l => l.PackedQuantity), LastQc = qc, LinesCount = mine.Count, Lines = mine,
                StageText = Stage(o.Status, qc)
            };
        }).ToList();
    }
}
