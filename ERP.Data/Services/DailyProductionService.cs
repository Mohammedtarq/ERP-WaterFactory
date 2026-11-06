using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <param name="Packs">عدد العبوات بوحدة التعبئة المختارة (شرنك 20، كارتون 40...).</param>
/// <param name="CustomRecipeId">الاسم الخاص/الستيكر: يستبدل الليبل العام بالخاص من الوصفة.</param>
public record DailyProductionLineInput(int FinishedItemId, int PackagingLevelId, decimal Packs, int? CustomRecipeId = null, string? Color = null);

/// <summary>احتياج مادة لإنتاج اليوم مقابل المتاح منها.</summary>
public record MaterialNeedRow(string ItemName, decimal Required, decimal Available)
{
    public decimal Short => Math.Max(0, Required - Available);
    public bool IsShort => Short > 0;
}

/// <summary>سطر منتج مسجّل: التشغيلة والقطع وكلفة المواد الفعلية.</summary>
public record DailyProductionLineResult(int LineId, string ItemName, string? RecipeName, string BatchNumber, decimal Packs, decimal Pieces,
                                        decimal MaterialCost, decimal UnitCost, decimal SettledShortage);

/// <summary>
/// "إنتاج اليوم" (الوضع البسيط): تاريخ وسطور (المنتج، الستيكر، عدد العبوات) ← في معاملة واحدة:
/// صرف المواد من الوصفة (الليبل الخاص بدل العام)، وكلفة فعلية لكل سطر من المتوسط المرجّح لحظة الصرف،
/// وتشغيلة باسم اليوم، ودخول مخزن المنتج التام بالكلفة، وتسوية أي بيع سبق الإنتاج.
/// في الخلفية يُنشأ أمر إنتاج مكتمل، فتبقى تقارير الأوامر والتشغيلات كما هي.
/// </summary>
public class DailyProductionService
{
    private readonly ProjectDbContext _db;
    public DailyProductionService(ProjectDbContext db) => _db = db;

    public async Task<(FinanceOperationResult result, int? orderId, List<DailyProductionLineResult> lines)> RecordAsync(
        DateTime date, IReadOnlyList<DailyProductionLineInput> inputs, int userId, int? rawWarehouseId = null, int? finishedWarehouseId = null)
    {
        var none = new List<DailyProductionLineResult>();
        var valid = inputs.Where(i => i.Packs > 0).ToList();
        if (valid.Count == 0) return (FinanceOperationResult.Fail("أضف منتجًا واحدًا على الأقل بعدد عبوات أكبر من صفر"), null, none);
        if (date.Date > DateTime.Today) return (FinanceOperationResult.Fail("لا يُسجَّل إنتاج بتاريخ مستقبلي"), null, none);
        if (await new PeriodLockService(_db).GetLockedThroughAsync() is DateTime locked && date.Date <= locked)
            return (FinanceOperationResult.Fail($"الفترة حتى {locked:yyyy-MM-dd} مقفلة. لا يُسجَّل إنتاج بتاريخ داخلها إلا بعد فتحها من المدير."), null, none);

        var fg = finishedWarehouseId is int f ? await _db.Warehouses.FindAsync(f)
                 : await _db.Warehouses.Where(w => w.IsActive && w.WarehouseType == WarehouseType.FinishedGoods).OrderBy(w => w.Id).FirstOrDefaultAsync();
        if (fg is null || fg.WarehouseType != WarehouseType.FinishedGoods) return (FinanceOperationResult.Fail("لا يوجد مخزن منتج تام"), null, none);
        var availability = new MaterialAvailabilityService(_db);
        var raw = rawWarehouseId ?? (await availability.SourceWarehousesAsync()).Select(w => (int?)w.Id).FirstOrDefault();
        if (raw is null) return (FinanceOperationResult.Fail("لا يوجد مخزن مواد أولية"), null, none);

        // ---- الوصفات والكميات، وفحص كفاية المواد على مجموع اليوم ----
        var (plans, planError) = await PlanAsync(valid);
        if (planError is not null) return (FinanceOperationResult.Fail(planError), null, none);
        var shortages = (await NeedsAsync(plans, raw.Value)).Where(n => n.Short > 0)
                              .Select(n => $"«{n.ItemName}»: المطلوب {n.Required:N0}، المتاح {n.Available:N0}").ToList();
        if (shortages.Count > 0)
            return (FinanceOperationResult.Fail("المواد الأولية لا تكفي إنتاج اليوم — سجّل الشراء أو صحّح الرصيد بالجرد أولًا:\n" + string.Join("\n", shortages)), null, none);
        var production = new ProductionService(_db);

        var stamp = date.Date == DateTime.Today ? DateTime.UtcNow : DateTime.SpecifyKind(date.Date.AddHours(9), DateTimeKind.Utc);
        var ownTx = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync() : null;
        try
        {
            var first = plans[0];
            var order = new ProductionOrder
            {
                MONumber = $"MO-{await _db.ProductionOrders.CountAsync() + 1:D5}",
                FinishedItemId = first.item.Id, BOMId = first.bomId, CustomRecipeId = first.input.CustomRecipeId,
                QuantityToProduce = plans.Sum(p => p.pieces), RawMaterialsWarehouseId = raw.Value, Status = ProductionOrderStatus.Completed,
                CreatedByUserId = userId
            };
            _db.ProductionOrders.Add(order);
            await _db.SaveChangesAsync();

            var results = new List<DailyProductionLineResult>();
            var lineNo = 0;
            foreach (var p in plans)
            {
                var line = new ProductionOrderLine
                {
                    ProductionOrderId = order.Id, LineNo = ++lineNo, FinishedItemId = p.item.Id, BOMId = p.bomId,
                    CustomRecipeId = p.input.CustomRecipeId, QuantityToProduce = p.pieces, PackagingLevelId = p.level.Id, Packs = p.input.Packs
                };
                _db.ProductionOrderLines.Add(line);
                await _db.SaveChangesAsync();

                // صرف المواد بالمتوسط المرجّح لحظة الصرف: كلفة السطر الفعلية
                decimal cost = 0;
                foreach (var (rawItemId, qty) in p.materials)
                {
                    var unitCost = await _db.Items.Where(x => x.Id == rawItemId).Select(x => x.CostPrice).FirstAsync();
                    var (alloc, error) = await availability.AllocateAsync(rawItemId, qty, raw);
                    if (error is not null) throw new DailyAbort(error);
                    foreach (var a in alloc)
                        _db.StockTransactions.Add(new StockTransaction
                        {
                            ItemId = rawItemId, WarehouseId = a.WarehouseId, BatchId = a.BatchId, QuantityBaseUnits = -a.Quantity, UnitCost = unitCost,
                            TransactionType = StockTransactionType.ProductionConsume, ReferenceTable = "ProductionOrders", ReferenceId = order.Id,
                            TransactionDate = stamp, CreatedByUserId = userId
                        });
                    _db.ProductionOrderConsumptions.Add(new ProductionOrderConsumption
                    {
                        ProductionOrderId = order.Id, ProductionOrderLineId = line.Id, RawMaterialItemId = rawItemId,
                        QuantityRequired = qty, QuantityConsumed = qty
                    });
                    cost += qty * (unitCost ?? 0);
                    await _db.SaveChangesAsync();   // حتى يرى توزيع المادة نفسها في سطر لاحق الرصيد بعد الخصم
                }

                var recipeName = p.input.CustomRecipeId is int rid ? await _db.CustomRecipes.Where(r => r.Id == rid).Select(r => r.Name).FirstAsync() : null;
                var batch = new ItemBatch
                {
                    ItemId = p.item.Id, BatchNumber = await production.NextBatchNumberAsync(), ProductionOrderId = order.Id,
                    CustomRecipeId = p.input.CustomRecipeId,
                    ManufactureDate = date.Date, ExpiryDate = date.Date.AddMonths(ProductionService.DefaultShelfLifeMonths)
                };
                _db.ItemBatches.Add(batch);
                await _db.SaveChangesAsync();
                line.OutputBatchId = batch.Id;
                if (line.LineNo == 1) order.OutputBatchId = batch.Id;

                var unit = p.pieces > 0 && cost > 0 ? Math.Round(cost / p.pieces, 4) : (decimal?)null;
                _db.StockTransactions.Add(new StockTransaction
                {
                    ItemId = p.item.Id, WarehouseId = fg.Id, BatchId = batch.Id, QuantityBaseUnits = p.pieces, UnitCost = unit,
                    TransactionType = StockTransactionType.ProductionOutput, ReferenceTable = "ProductionOrders", ReferenceId = order.Id,
                    TransactionDate = stamp, CreatedByUserId = userId
                });
                await _db.SaveChangesAsync();

                // العجز المبيع مسبقًا عجز في الأساسي: لا يُسوّى من تشغيلة مطعم أو مناسبة
                var settled = p.input.CustomRecipeId is null ? await new PendingProductionService(_db).SettleAsync(p.item.Id, fg.Id, batch.Id, userId) : 0;
                results.Add(new DailyProductionLineResult(line.Id, p.item.ItemName, recipeName, batch.BatchNumber, p.input.Packs, p.pieces,
                                                          Math.Round(cost, 2), unit ?? 0, settled));
            }
            await _db.SaveChangesAsync();
            await new AuditService(_db).LogAsync(userId, "Post", "ProductionOrders", order.Id,
                $"إنتاج يوم {date:yyyy-MM-dd}: {string.Join("، ", results.Select(r => $"{r.ItemName} {r.Packs:N0}"))}");
            if (ownTx is not null) await ownTx.CommitAsync();
            return (FinanceOperationResult.Ok(), order.Id, results);
        }
        catch (DailyAbort ex)
        {
            if (ownTx is not null) await ownTx.RollbackAsync();
            _db.ChangeTracker.Clear();
            return (FinanceOperationResult.Fail(ex.Message), null, none);
        }
        finally
        {
            if (ownTx is not null) await ownTx.DisposeAsync();
        }
    }

    private sealed record LinePlan(DailyProductionLineInput input, Item item, ItemPackagingLevel level, decimal pieces, int bomId, List<(int rawItemId, decimal qty)> materials);

    private async Task<(List<LinePlan> plans, string? error)> PlanAsync(IReadOnlyList<DailyProductionLineInput> inputs)
    {
        var plans = new List<LinePlan>();
        var production = new ProductionService(_db);
        foreach (var i in inputs)
        {
            var item = await _db.Items.AsNoTracking().FirstOrDefaultAsync(x => x.Id == i.FinishedItemId);
            var level = await _db.ItemPackagingLevels.AsNoTracking().FirstOrDefaultAsync(l => l.Id == i.PackagingLevelId && l.ItemId == i.FinishedItemId);
            if (item is null || level is null) return (plans, "المنتج أو وحدة التعبئة غير صحيحة");
            var (recipe, error) = await production.MergeRecipeAsync(i.FinishedItemId, i.CustomRecipeId);
            if (error is not null) return (plans, $"{item.ItemName}: {error}");
            var pieces = Math.Round(i.Packs * level.EquivalentBaseUnits, 3);
            var bomId = await _db.BillOfMaterials.Where(b => b.FinishedItemId == i.FinishedItemId && b.IsActive).Select(b => b.Id).FirstAsync();
            plans.Add(new LinePlan(i, item, level, pieces, bomId, recipe.Select(r => (r.rawItemId, Math.Round(r.perUnit * pieces, 3))).Where(r => r.Item2 > 0).ToList()));
        }
        return (plans, null);
    }

    private async Task<List<MaterialNeedRow>> NeedsAsync(List<LinePlan> plans, int rawWarehouseId)
    {
        var totals = plans.SelectMany(p => p.materials).GroupBy(m => m.rawItemId).ToDictionary(g => g.Key, g => g.Sum(m => m.qty));
        var avail = await new MaterialAvailabilityService(_db).GetAsync(totals.Keys.ToList(), rawWarehouseId);
        return totals.Select(t => new MaterialNeedRow(avail[t.Key].ItemName, t.Value, avail[t.Key].Available))
                     .OrderByDescending(n => n.Short > 0).ThenBy(n => n.ItemName).ToList();
    }

    /// <summary>معاينة قبل التسجيل: احتياج اليوم من كل مادة (ومنها الغطاء والليبل الخاص بكل اسم) مقابل المتاح.</summary>
    public async Task<(string? error, List<MaterialNeedRow> needs)> PreviewAsync(IReadOnlyList<DailyProductionLineInput> inputs, int? rawWarehouseId = null)
    {
        var valid = inputs.Where(i => i.Packs > 0).ToList();
        if (valid.Count == 0) return ("أضف منتجًا واحدًا على الأقل بعدد عبوات", new());
        var raw = rawWarehouseId ?? (await new MaterialAvailabilityService(_db).SourceWarehousesAsync()).Select(w => (int?)w.Id).FirstOrDefault();
        if (raw is null) return ("لا يوجد مخزن مواد أولية", new());
        var (plans, error) = await PlanAsync(valid);
        return error is not null ? (error, new()) : (null, await NeedsAsync(plans, raw.Value));
    }

    private sealed class DailyAbort(string message) : Exception(message);
}

/// <summary>
/// البيع بانتظار الإنتاج: ما بيع فوق الرصيد سجّله الترحيل عجزًا بلا تشغيلة؛ عند تسجيل الإنتاج يُسوّى
/// بنقل الكمية من التشغيلة الجديدة إلى العجز، فيعود رصيد كل تشغيلة صحيحًا.
/// </summary>
public class PendingProductionService
{
    private readonly ProjectDbContext _db;
    public PendingProductionService(ProjectDbContext db) => _db = db;

    /// <summary>يسوّي العجز المفتوح للصنف في المخزن من تشغيلة منتجة حديثًا (الأقدم أولًا). يعيد الكمية المسوّاة.</summary>
    public async Task<decimal> SettleAsync(int itemId, int warehouseId, int batchId, int userId)
    {
        var open = await _db.PendingProductionShortages.Where(s => s.ItemId == itemId && s.WarehouseId == warehouseId && s.SettledQuantity < s.Quantity)
                                                      .OrderBy(s => s.Id).ToListAsync();
        if (open.Count == 0) return 0;
        var available = await LedgerHelper.BalanceAsync(_db, itemId, warehouseId, batchId);
        decimal total = 0;
        foreach (var s in open)
        {
            if (available <= 0) break;
            var take = Math.Min(available, s.Quantity - s.SettledQuantity);
            _db.StockTransactions.Add(new StockTransaction { ItemId = itemId, WarehouseId = warehouseId, BatchId = batchId, QuantityBaseUnits = -take,
                                                             TransactionType = StockTransactionType.Transfer, ReferenceTable = "PendingProductionShortages",
                                                             ReferenceId = s.Id, CreatedByUserId = userId });
            _db.StockTransactions.Add(new StockTransaction { ItemId = itemId, WarehouseId = warehouseId, BatchId = null, QuantityBaseUnits = take,
                                                             TransactionType = StockTransactionType.Transfer, ReferenceTable = "PendingProductionShortages",
                                                             ReferenceId = s.Id, CreatedByUserId = userId });
            s.SettledQuantity += take;
            if (s.SettledQuantity >= s.Quantity) s.SettledAt = DateTime.UtcNow;
            available -= take;
            total += take;
        }
        await _db.SaveChangesAsync();
        return total;
    }

    public record OpenShortageRow(int Id, DateTime CreatedAt, string ItemName, string WarehouseName, string? InvoiceNumber, decimal Quantity, decimal Settled, decimal Open);

    public async Task<List<OpenShortageRow>> OpenAsync() =>
        (await _db.PendingProductionShortages.AsNoTracking().Where(s => s.SettledQuantity < s.Quantity).OrderBy(s => s.Id)
            .Select(s => new { s.Id, s.CreatedAt, s.Item.ItemName, Wh = s.Warehouse.Name, Inv = s.SalesInvoice != null ? s.SalesInvoice.InvoiceNumber : null, s.Quantity, s.SettledQuantity })
            .ToListAsync())
        .Select(s => new OpenShortageRow(s.Id, DateTime.SpecifyKind(s.CreatedAt, DateTimeKind.Utc).ToLocalTime(), s.ItemName, s.Wh, s.Inv, s.Quantity, s.SettledQuantity, s.Quantity - s.SettledQuantity))
        .ToList();
}

/// <summary>سطر جاهز لأمر اليوم (من قالب أو من آخر إنتاج).</summary>
public record DailyTemplateLine(int FinishedItemId, int PackagingLevelId, int? CustomRecipeId, decimal Packs);

/// <summary>أمر اليوم المعتاد: قوالب مسمّاة، وتكرار آخر إنتاج مسجّل.</summary>
public class DailyProductionTemplateService
{
    private readonly ProjectDbContext _db;
    public DailyProductionTemplateService(ProjectDbContext db) => _db = db;

    public Task<List<DailyProductionTemplate>> ListAsync() => _db.DailyProductionTemplates.AsNoTracking().OrderBy(t => t.Name).ToListAsync();

    public async Task<List<DailyTemplateLine>> LinesAsync(int templateId) =>
        await _db.DailyProductionTemplateLines.AsNoTracking().Where(l => l.TemplateId == templateId).OrderBy(l => l.LineNo)
                 .Select(l => new DailyTemplateLine(l.FinishedItemId, l.PackagingLevelId, l.CustomRecipeId, l.Packs)).ToListAsync();

    /// <summary>حفظ القالب بالاسم: الاسم الموجود يُستبدل سطوره كاملة.</summary>
    public async Task<(FinanceOperationResult result, int? templateId)> SaveAsync(string name, IReadOnlyList<DailyTemplateLine> lines, int userId)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return (FinanceOperationResult.Fail("اكتب اسم القالب (مثل: أمر الأحد)"), null);
        if (name.Length > 100) return (FinanceOperationResult.Fail("اسم القالب طويل"), null);
        var valid = lines.Where(l => l.Packs > 0).ToList();
        if (valid.Count == 0) return (FinanceOperationResult.Fail("القالب يحتاج سطرًا واحدًا على الأقل بعدد عبوات"), null);
        var levelIds = valid.Select(l => l.PackagingLevelId).Distinct().ToList();
        var levels = await _db.ItemPackagingLevels.Where(l => levelIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.ItemId);
        if (valid.Any(l => !levels.TryGetValue(l.PackagingLevelId, out var item) || item != l.FinishedItemId))
            return (FinanceOperationResult.Fail("وحدة التعبئة لا تخص المنتج"), null);

        var template = await _db.DailyProductionTemplates.Include(t => t.Lines).FirstOrDefaultAsync(t => t.Name == name);
        if (template is null) _db.DailyProductionTemplates.Add(template = new DailyProductionTemplate { Name = name, CreatedByUserId = userId });
        else _db.DailyProductionTemplateLines.RemoveRange(template.Lines);
        template.UpdatedAt = DateTime.UtcNow;
        var no = 0;
        foreach (var l in valid)
            template.Lines.Add(new DailyProductionTemplateLine { LineNo = ++no, FinishedItemId = l.FinishedItemId, PackagingLevelId = l.PackagingLevelId,
                                                                 CustomRecipeId = l.CustomRecipeId, Packs = l.Packs });
        await _db.SaveChangesAsync();
        return (FinanceOperationResult.Ok(), template.Id);
    }

    public async Task<FinanceOperationResult> DeleteAsync(int templateId)
    {
        var deleted = await _db.DailyProductionTemplates.Where(t => t.Id == templateId).ExecuteDeleteAsync();
        return deleted == 0 ? FinanceOperationResult.Fail("القالب غير موجود") : FinanceOperationResult.Ok();
    }

    /// <summary>سطور آخر إنتاج يوم مسجّل، بالوحدة والعدد كما أُدخلت.</summary>
    public async Task<(DateTime? date, List<DailyTemplateLine> lines)> LastProductionAsync()
    {
        var last = await _db.ProductionOrderLines.AsNoTracking().Where(l => l.PackagingLevelId != null && l.Packs != null)
                            .OrderByDescending(l => l.ProductionOrderId).Select(l => new { l.ProductionOrderId, Date = l.OutputBatch != null ? l.OutputBatch.ManufactureDate : null }).FirstOrDefaultAsync();
        if (last is null) return (null, new());
        var lines = await _db.ProductionOrderLines.AsNoTracking().Where(l => l.ProductionOrderId == last.ProductionOrderId).OrderBy(l => l.LineNo)
                             .Select(l => new DailyTemplateLine(l.FinishedItemId, l.PackagingLevelId!.Value, l.CustomRecipeId, l.Packs!.Value)).ToListAsync();
        return (last.Date, lines);
    }
}
