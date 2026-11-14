using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>حد تنبيه صنف في مخزن: الرصيد والحد بالقطعة وبعبوة الصنف.</summary>
public class WarehouseAlertRow
{
    public int WarehouseId { get; init; }
    public string WarehouseName { get; init; } = "";
    public int ItemId { get; init; }
    public string ItemCode { get; init; } = "";
    public string ItemName { get; init; } = "";
    public decimal Balance { get; init; }
    public string BalanceText { get; init; } = "";
    /// <summary>الحد الفعّال بالقطعة: الخاص بالمخزن، أو الافتراضي من بطاقة الصنف.</summary>
    public decimal? MinQuantity { get; init; }
    public string MinText { get; init; } = "";
    /// <summary>الحد مأخوذ من بطاقة الصنف (لم يُضبط لهذا المخزن بعد).</summary>
    public bool IsDefault { get; init; }
    public bool IsLow => MinQuantity is { } min && Balance <= min;
}

/// <summary>
/// حدود التنبيه لكل مخزن (قرار المدير في التجربة: من داخل المخزن). كل (مخزن، صنف) له حدّه ويُقارن برصيد ذلك المخزن وحده؛
/// وإن لم يُضبط فحد بطاقة الصنف افتراضي في مخزنه الرئيسي وحده (أول مخزن مواد أولية للمشترى، وأول مخزن منتج تام للمصنّع).
/// </summary>
public class StockAlertService
{
    private readonly ProjectDbContext _db;
    public StockAlertService(ProjectDbContext db) => _db = db;

    /// <summary>مخزنا الصنف الرئيسيان: أول مخزن مواد أولية وأول مخزن منتج تام فعّالان (أو المخزن العام إن لم يوجدا).</summary>
    private sealed record Homes(int? Raw, int? Finished)
    {
        /// <summary>المخزن الطبيعي للصنف: حدّ بطاقته يسري فيه وحده ما لم يُضبط فيه حد خاص.</summary>
        public int? Of(SourcingMethod sourcing) => sourcing == SourcingMethod.Manufactured ? Finished ?? Raw : Raw ?? Finished;
    }

    private async Task<Homes> HomesAsync()
    {
        var active = await _db.Warehouses.AsNoTracking().Where(w => w.IsActive)
            .Select(w => new { w.Id, w.WarehouseType }).OrderBy(w => w.Id).ToListAsync();
        int? First(params WarehouseType[] types) => active.FirstOrDefault(w => types.Contains(w.WarehouseType))?.Id;
        var general = First(WarehouseType.Main, WarehouseType.Sub);
        return new Homes(First(WarehouseType.RawMaterial) ?? general, First(WarehouseType.FinishedGoods) ?? general);
    }

    /// <summary>المخزن الذي يُضبط فيه حد الصنف من «مقترح الشراء» (مخزن المواد الأولية الرئيسي).</summary>
    public async Task<int?> HomeWarehouseIdAsync(SourcingMethod sourcing) => (await HomesAsync()).Of(sourcing);

    /// <summary>حد التنبيه الفعّال لكل صنف مجموعًا على مخازن معيّنة (للمقترح الذي يحسب الرصيد الإجمالي لتلك المخازن).</summary>
    public async Task<Dictionary<int, decimal>> TotalsAsync(IReadOnlyCollection<int> warehouseIds, IReadOnlyCollection<Item> items)
    {
        var homes = await HomesAsync();
        var ids = items.Select(i => i.Id).ToList();
        var own = await _db.WarehouseItemAlerts.AsNoTracking().Where(a => warehouseIds.Contains(a.WarehouseId) && ids.Contains(a.ItemId)).ToListAsync();
        var result = new Dictionary<int, decimal>();
        foreach (var i in items)
        {
            var mine = own.Where(a => a.ItemId == i.Id).ToList();
            var home = homes.Of(i.SourcingMethod);
            decimal? total = mine.Count == 0 ? null : mine.Sum(a => a.MinQuantity);
            if (i.MinStockAlertLevel is { } d && home is int h && warehouseIds.Contains(h) && mine.All(a => a.WarehouseId != h))
                total = (total ?? 0) + d;
            if (total is { } t) result[i.Id] = t;
        }
        return result;
    }

    /// <summary>أصناف المخزن مع رصيدها وحدّها: أصناف مخزنه الطبيعي، وما له رصيد فيه، وما ضُبط له حد فيه.</summary>
    public async Task<List<WarehouseAlertRow>> ForWarehouseAsync(int warehouseId)
    {
        var warehouse = await _db.Warehouses.AsNoTracking().FirstAsync(w => w.Id == warehouseId);
        var homes = await HomesAsync();
        var balances = await _db.StockTransactions.Where(t => t.WarehouseId == warehouseId)
            .GroupBy(t => t.ItemId).Select(g => new { g.Key, Qty = g.Sum(t => t.QuantityBaseUnits) })
            .ToDictionaryAsync(x => x.Key, x => x.Qty);
        var own = await _db.WarehouseItemAlerts.AsNoTracking().Where(a => a.WarehouseId == warehouseId)
            .ToDictionaryAsync(a => a.ItemId, a => a.MinQuantity);
        var items = (await _db.Items.AsNoTracking().Where(i => i.IsActive).ToListAsync())
            .Where(i => homes.Of(i.SourcingMethod) == warehouseId || balances.ContainsKey(i.Id) || own.ContainsKey(i.Id)
                        || InNaturalStore(warehouse.WarehouseType, i.SourcingMethod))
            .OrderBy(i => i.ItemName).ToList();
        var packs = await PackFormatter.LoadAsync(_db, items.Select(i => i.Id));
        return items.Select(i => Row(warehouse, i, balances.GetValueOrDefault(i.Id), own, packs, homes)).ToList();
    }

    /// <summary>كل الأصناف عند حد التنبيه أو تحته، في كل مخزن على حدة (الأشد نقصًا أولًا).</summary>
    public async Task<List<WarehouseAlertRow>> LowAsync()
    {
        var warehouses = await _db.Warehouses.AsNoTracking()
            .Where(w => w.IsActive && w.WarehouseType != WarehouseType.RepVan && w.WarehouseType != WarehouseType.WorkInProcess
                        && w.WarehouseType != WarehouseType.Damaged).ToListAsync();
        var whIds = warehouses.Select(w => w.Id).ToList();
        var own = await _db.WarehouseItemAlerts.AsNoTracking().Where(a => whIds.Contains(a.WarehouseId)).ToListAsync();
        var ownItemIds = own.Select(a => a.ItemId).Distinct().ToList();
        var items = await _db.Items.AsNoTracking().Where(i => i.IsActive && (i.MinStockAlertLevel != null || ownItemIds.Contains(i.Id))).ToListAsync();
        if (items.Count == 0) return new();
        var itemIds = items.Select(i => i.Id).ToList();
        var balances = await _db.StockTransactions.Where(t => whIds.Contains(t.WarehouseId) && itemIds.Contains(t.ItemId))
            .GroupBy(t => new { t.WarehouseId, t.ItemId }).Select(g => new { g.Key.WarehouseId, g.Key.ItemId, Qty = g.Sum(t => t.QuantityBaseUnits) })
            .ToDictionaryAsync(x => (x.WarehouseId, x.ItemId), x => x.Qty);
        var packs = await PackFormatter.LoadAsync(_db, itemIds);
        var homes = await HomesAsync();
        var rows = new List<WarehouseAlertRow>();
        foreach (var w in warehouses)
        {
            var ownHere = own.Where(a => a.WarehouseId == w.Id).ToDictionary(a => a.ItemId, a => a.MinQuantity);
            foreach (var i in items)
            {
                var row = Row(w, i, balances.GetValueOrDefault((w.Id, i.Id)), ownHere, packs, homes);
                if (row.IsLow) rows.Add(row);
            }
        }
        return rows.OrderBy(r => r.Balance - r.MinQuantity!.Value).ThenBy(r => r.ItemName).ToList();
    }

    /// <summary>
    /// حفظ حدود مخزن: قيمة = حد خاص بالمخزن (بالقطعة)، null = إزالة الخاص والعودة لحد بطاقة الصنف.
    /// </summary>
    public async Task<FinanceOperationResult> SaveAsync(int warehouseId, IReadOnlyCollection<(int ItemId, decimal? MinQuantity)> changes, int userId)
    {
        if (changes.Any(c => c.MinQuantity < 0)) return FinanceOperationResult.Fail("حد التنبيه لا يمكن أن يكون سالبًا");
        if (!await _db.Warehouses.AnyAsync(w => w.Id == warehouseId)) return FinanceOperationResult.Fail("المخزن غير موجود");
        var ids = changes.Select(c => c.ItemId).ToList();
        var existing = await _db.WarehouseItemAlerts.Where(a => a.WarehouseId == warehouseId && ids.Contains(a.ItemId)).ToDictionaryAsync(a => a.ItemId);
        foreach (var (itemId, min) in changes)
        {
            if (existing.TryGetValue(itemId, out var row))
            {
                if (min is null) _db.WarehouseItemAlerts.Remove(row);
                else { row.MinQuantity = min.Value; row.UpdatedAt = DateTime.UtcNow; row.UpdatedByUserId = userId; }
            }
            else if (min is not null)
                _db.WarehouseItemAlerts.Add(new WarehouseItemAlert { WarehouseId = warehouseId, ItemId = itemId, MinQuantity = min.Value, UpdatedByUserId = userId });
        }
        await _db.SaveChangesAsync();
        return FinanceOperationResult.Ok();
    }

    /// <summary>الأصناف التي تُعرض في مخزن من نوعه ليُضبط حدها فيه (المواد في مخازن المواد، والمنتجات في مخازن المنتج).</summary>
    private static bool InNaturalStore(WarehouseType warehouse, SourcingMethod sourcing) => warehouse switch
    {
        WarehouseType.RawMaterial => sourcing != SourcingMethod.Manufactured,
        WarehouseType.FinishedGoods => sourcing != SourcingMethod.Purchased,
        WarehouseType.Main or WarehouseType.Sub => true,
        _ => false
    };

    private static WarehouseAlertRow Row(Warehouse w, Item i, decimal balance, IReadOnlyDictionary<int, decimal> own, PackFormatter packs, Homes homes)
    {
        var hasOwn = own.TryGetValue(i.Id, out var ownMin);
        decimal? min = hasOwn ? ownMin : homes.Of(i.SourcingMethod) == w.Id ? i.MinStockAlertLevel : null;
        return new WarehouseAlertRow
        {
            WarehouseId = w.Id, WarehouseName = w.Name, ItemId = i.Id, ItemCode = i.ItemCode, ItemName = i.ItemName,
            Balance = balance, BalanceText = packs.Of(i.Id, balance),
            MinQuantity = min, MinText = min is { } m ? packs.Of(i.Id, m) : "", IsDefault = !hasOwn && min is not null
        };
    }
}
