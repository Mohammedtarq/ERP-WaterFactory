using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>سطر في ورقة الجرد: رصيد النظام ووحدات العدّ المتاحة للصنف (باليت، كرتون، قطعة...).</summary>
public class StocktakeSheetRow
{
    public int ItemId { get; init; }
    public string ItemCode { get; init; } = "";
    public string ItemName { get; init; } = "";
    public decimal SystemQuantity { get; init; }
    public decimal? UnitCost { get; init; }
    /// <summary>وحدات العدّ من الأكبر إلى القطعة: (الاسم، كم قطعة فيها).</summary>
    public IReadOnlyList<(string Name, decimal Pieces)> Units { get; init; } = Array.Empty<(string, decimal)>();
}

/// <param name="Counts">ما عُدّ بكل وحدة: (اسم الوحدة، كم قطعة فيها، العدد).</param>
public record StocktakeLineInput(int ItemId, IReadOnlyList<(string Unit, decimal PiecesPerUnit, decimal Count)> Counts, string? Notes = null)
{
    public decimal Pieces => Counts.Sum(c => c.PiecesPerUnit * c.Count);
    public string Detail => string.Join(" + ", Counts.Where(c => c.Count != 0).Select(c => $"{c.Count:#,0.###} {c.Unit}"));
}

public record StocktakeResult(int CountId, string CountNumber, int ItemsCounted, int ItemsWithVariance, decimal ShortageValue, decimal SurplusValue);

/// <summary>
/// الجرد السريع: يُعدّ ما يُرى (باليت كامل + كراتين + قطع مفردة، الشرنك بالرولات) والنظام يحوّل إلى قطع.
/// الفرق يُسجَّل "فرق جرد" بالكلفة — منفصلًا عن التلف — والنقص يُخصم من التشغيلات الأقدم أولًا.
/// </summary>
public class StocktakeService
{
    private readonly ProjectDbContext _db;
    public StocktakeService(ProjectDbContext db) => _db = db;

    /// <summary>ورقة جرد المخزن: كل صنف له رصيد أو حركة فيه، مع وحدات العدّ من هيكلية التعبئة.</summary>
    public async Task<List<StocktakeSheetRow>> SheetAsync(int warehouseId, IReadOnlyCollection<int>? onlyItems = null)
    {
        var balances = await _db.StockTransactions.Where(t => t.WarehouseId == warehouseId)
            .GroupBy(t => t.ItemId).Select(g => new { g.Key, Qty = g.Sum(t => t.QuantityBaseUnits) }).ToDictionaryAsync(x => x.Key, x => x.Qty);
        var ids = balances.Keys.Where(k => onlyItems is null || onlyItems.Contains(k)).ToList();
        var items = await _db.Items.AsNoTracking().Where(i => ids.Contains(i.Id) && i.IsActive).OrderBy(i => i.ItemCode).ToListAsync();
        var levels = await _db.ItemPackagingLevels.AsNoTracking().Where(l => ids.Contains(l.ItemId)).ToListAsync();
        return items.Select(i =>
        {
            var units = levels.Where(l => l.ItemId == i.Id).OrderByDescending(l => l.EquivalentBaseUnits)
                              .Select(l => (l.LevelName, l.EquivalentBaseUnits)).DistinctBy(u => u.LevelName).ToList();
            if (!units.Any(u => u.EquivalentBaseUnits == 1)) units.Add((i.BaseUnitName, 1));
            return new StocktakeSheetRow
            {
                ItemId = i.Id, ItemCode = i.ItemCode, ItemName = i.ItemName, SystemQuantity = balances[i.Id], UnitCost = i.CostPrice,
                Units = units.Select(u => (u.Item1, u.EquivalentBaseUnits)).ToList()
            };
        }).ToList();
    }

    public async Task<(FinanceOperationResult result, StocktakeResult? summary)> PostAsync(int warehouseId, DateTime countDate, string? notes,
                                                                                         IReadOnlyList<StocktakeLineInput> lines, int userId)
    {
        if (lines.Count == 0) return (FinanceOperationResult.Fail("لا توجد أصناف معدودة"), null);
        if (lines.Any(l => l.Pieces < 0)) return (FinanceOperationResult.Fail("العدد لا يكون سالبًا"), null);
        if (lines.GroupBy(l => l.ItemId).Any(g => g.Count() > 1)) return (FinanceOperationResult.Fail("الصنف مكرر في الجرد"), null);
        if (countDate.Date > DateTime.Today) return (FinanceOperationResult.Fail("لا يُسجَّل جرد بتاريخ مستقبلي"), null);
        if (!await _db.Warehouses.AnyAsync(w => w.Id == warehouseId)) return (FinanceOperationResult.Fail("اختر المخزن"), null);

        var ownTx = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync() : null;
        try
        {
            var count = new StockCount
            {
                CountNumber = $"SC-{countDate:yyyyMMdd}-{await _db.StockCounts.CountAsync() + 1:D4}", WarehouseId = warehouseId,
                CountDate = countDate.Date, Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(), CreatedByUserId = userId
            };
            _db.StockCounts.Add(count);
            await _db.SaveChangesAsync();

            int withVariance = 0;
            foreach (var l in lines)
            {
                var system = await LedgerHelper.BalanceAsync(_db, l.ItemId, warehouseId);
                var cost = await _db.Items.Where(i => i.Id == l.ItemId).Select(i => i.CostPrice).FirstAsync();
                var variance = Math.Round(l.Pieces - system, 3);
                count.Lines.Add(new StockCountLine
                {
                    ItemId = l.ItemId, SystemQuantity = system, CountedQuantity = l.Pieces, CountDetail = l.Detail.Length > 200 ? l.Detail[..200] : l.Detail,
                    UnitCost = cost, VarianceValue = Math.Round(variance * (cost ?? 0), 2), Notes = l.Notes
                });
                if (variance == 0) continue;
                withVariance++;
                if (variance < 0)
                {
                    // النقص يُخصم من التشغيلات الأقدم أولًا؛ ما زاد عن مجموع التشغيلات الموجبة يُخصم بلا تشغيلة
                    var need = -variance;
                    var positive = await _db.StockTransactions.Where(t => t.ItemId == l.ItemId && t.WarehouseId == warehouseId)
                        .GroupBy(t => new { t.BatchId, Expiry = t.Batch != null ? t.Batch.ExpiryDate : null })
                        .Select(g => new { g.Key.BatchId, g.Key.Expiry, Qty = g.Sum(t => t.QuantityBaseUnits) }).ToListAsync();
                    foreach (var b in positive.Where(b => b.Qty > 0).OrderBy(b => b.Expiry is null).ThenBy(b => b.Expiry).ThenBy(b => b.BatchId))
                    {
                        if (need <= 0) break;
                        var take = Math.Min(need, b.Qty);
                        AddVariance(l.ItemId, warehouseId, b.BatchId, -take, count.Id, userId);
                        need -= take;
                    }
                    if (need > 0) AddVariance(l.ItemId, warehouseId, null, -need, count.Id, userId);
                    count.ShortageValue += Math.Round(-variance * (cost ?? 0), 2);
                }
                else
                {
                    AddVariance(l.ItemId, warehouseId, null, variance, count.Id, userId);
                    count.SurplusValue += Math.Round(variance * (cost ?? 0), 2);
                }
            }
            await _db.SaveChangesAsync();
            if (ownTx is not null) await ownTx.CommitAsync();
            return (FinanceOperationResult.Ok(), new StocktakeResult(count.Id, count.CountNumber, lines.Count, withVariance, count.ShortageValue, count.SurplusValue));
        }
        catch (DbUpdateException ex) when (FinanceService.BusinessError(ex) is string msg)
        {
            if (ownTx is not null) await ownTx.RollbackAsync();
            _db.ChangeTracker.Clear();
            return (FinanceOperationResult.Fail(msg), null);
        }
        finally
        {
            if (ownTx is not null) await ownTx.DisposeAsync();
        }
    }

    private void AddVariance(int itemId, int warehouseId, int? batchId, decimal qty, int countId, int userId) =>
        _db.StockTransactions.Add(new StockTransaction
        {
            ItemId = itemId, WarehouseId = warehouseId, BatchId = batchId, QuantityBaseUnits = qty,
            TransactionType = StockTransactionType.StocktakeVariance, ReferenceTable = "StockCounts", ReferenceId = countId, CreatedByUserId = userId
        });
}

/// <summary>تقارير المرحلة م2: جرد الإنتاج الشهري، والخسائر (تلف، مسحوب مجاني، فرق جرد) بالكلفة.</summary>
public class ProductionStockReports
{
    private readonly ProjectDbContext _db;
    public ProductionStockReports(ProjectDbContext db) => _db = db;

    public record ProductionDayRow(DateTime Date, string ItemName, decimal Pieces, string PackLabel, decimal Packs, decimal Cost);

    /// <summary>جرد الإنتاج الشهري: يومًا بيوم لكل منتج، بالعبوات والقطع وكلفة الإنتاج الفعلية.</summary>
    public async Task<List<ProductionDayRow>> MonthlyProductionAsync(int year, int month)
    {
        var from = new DateTime(year, month, 1);
        var to = from.AddMonths(1);
        var rows = await _db.StockTransactions.AsNoTracking()
            .Where(t => t.TransactionType == StockTransactionType.ProductionOutput && t.TransactionDate >= from.AddDays(-1) && t.TransactionDate < to.AddDays(1))
            .Select(t => new { t.TransactionDate, t.ItemId, t.Item.ItemName, t.QuantityBaseUnits, t.UnitCost }).ToListAsync();
        var ids = rows.Select(r => r.ItemId).Distinct().ToList();
        var packs = (await _db.ItemPackagingLevels.AsNoTracking().Where(l => ids.Contains(l.ItemId) && l.IsSellableUnit).ToListAsync())
            .GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.EquivalentBaseUnits).First());
        return rows.Select(r => new { r, Local = DateTime.SpecifyKind(r.TransactionDate, DateTimeKind.Utc).ToLocalTime().Date })
            .Where(x => x.Local >= from && x.Local < to)
            .GroupBy(x => new { x.Local, x.r.ItemId, x.r.ItemName })
            .Select(g =>
            {
                var pieces = g.Sum(x => x.r.QuantityBaseUnits);
                var level = packs.GetValueOrDefault(g.Key.ItemId);
                var per = level?.EquivalentBaseUnits ?? 1;
                return new ProductionDayRow(g.Key.Local, g.Key.ItemName, pieces, level?.LevelName ?? "قطعة", Math.Round(pieces / per, 2),
                                            Math.Round(g.Sum(x => x.r.QuantityBaseUnits * (x.r.UnitCost ?? 0)), 2));
            })
            .OrderBy(r => r.Date).ThenBy(r => r.ItemName).ToList();
    }

    public record LossRow(string Kind, string ItemName, string? Party, string? Category, decimal Pieces, decimal Value);

    /// <summary>خسائر الشهر بالكلفة: التلف، والمسحوب المجاني (لكل جهة وتصنيف)، وفرق الجرد — كل نوع منفصل.</summary>
    public async Task<List<LossRow>> MonthlyLossesAsync(int year, int month)
    {
        var from = new DateTime(year, month, 1);
        var to = from.AddMonths(1);
        var types = new[] { StockTransactionType.Damaged, StockTransactionType.RepDamaged, StockTransactionType.FreeIssue,
                            StockTransactionType.RepFreeSale, StockTransactionType.StocktakeVariance };
        var tx = await _db.StockTransactions.AsNoTracking()
            .Where(t => types.Contains(t.TransactionType) && t.TransactionDate >= from.AddDays(-1) && t.TransactionDate < to.AddDays(1)
                        && (t.QuantityBaseUnits < 0 || t.TransactionType == StockTransactionType.StocktakeVariance))
            .Select(t => new { t.TransactionDate, t.TransactionType, t.Item.ItemName, t.QuantityBaseUnits, t.UnitCost, t.FreeIssueRecipient, t.ReferenceTable, t.ReferenceId })
            .ToListAsync();
        var docIds = tx.Where(t => t.ReferenceTable == "StockDocuments" && t.ReferenceId != null).Select(t => t.ReferenceId!.Value).Distinct().ToList();
        var docs = await _db.StockDocuments.AsNoTracking().Where(d => docIds.Contains(d.Id))
            .Select(d => new { d.Id, d.PartyName, d.BeneficiaryCategory }).ToDictionaryAsync(d => d.Id);
        return tx.Where(t => DateTime.SpecifyKind(t.TransactionDate, DateTimeKind.Utc).ToLocalTime() is var l && l >= from && l < to)
            .Select(t =>
            {
                var kind = t.TransactionType switch
                {
                    StockTransactionType.Damaged or StockTransactionType.RepDamaged => "تلف",
                    StockTransactionType.StocktakeVariance => t.QuantityBaseUnits < 0 ? "نقص جرد" : "زيادة جرد",
                    _ => "مسحوب مجاني"
                };
                var doc = t.ReferenceTable == "StockDocuments" && t.ReferenceId is int id && docs.TryGetValue(id, out var d) ? d : null;
                return new { kind, t.ItemName, Party = doc?.PartyName ?? t.FreeIssueRecipient, Category = doc?.BeneficiaryCategory?.ToString(),
                             Pieces = -t.QuantityBaseUnits, Value = -t.QuantityBaseUnits * (t.UnitCost ?? 0) };
            })
            .GroupBy(x => new { x.kind, x.ItemName, x.Party, x.Category })
            .Select(g => new LossRow(g.Key.kind, g.Key.ItemName, g.Key.Party, g.Key.Category, g.Sum(x => x.Pieces), Math.Round(g.Sum(x => x.Value), 2)))
            .OrderBy(r => r.Kind).ThenByDescending(r => r.Value).ToList();
    }
}
