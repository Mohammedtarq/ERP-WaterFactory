using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>توفر مادة أولية واحدة: المتاح في مخازن المواد الأولية مع تفصيله حسب المخزن.</summary>
public class MaterialAvailability
{
    public int ItemId { get; init; }
    public string ItemName { get; init; } = "";
    public decimal Available { get; init; }
    /// <summary>رصيد الصنف في مخازن أخرى غير مخصصة للمواد الأولية (للتوضيح فقط، لا يُصرف منها).</summary>
    public decimal ElsewhereQuantity { get; init; }
    public IReadOnlyList<(int warehouseId, string warehouseName, decimal qty)> ByWarehouse { get; init; } = Array.Empty<(int, string, decimal)>();
}

public record MaterialAllocation(int WarehouseId, int? BatchId, decimal Quantity);

/// <summary>
/// محرك التوفر الموحّد للمواد الأولية — المصدر الوحيد لحساب "المتاح" في شاشة احتياجات التصنيع
/// ومعاينة أمر الإنتاج وخطوة بدء التشغيل، حتى لا يختلف رقم شاشة عن أخرى.
/// المتاح = مجموع أرصدة مخازن المواد الأولية الفعّالة (+ المخزن المختار في الأمر إن كان من نوع آخر)،
/// والصرف بترتيب الأقرب انتهاءً (FEFO) عبر هذه المخازن مع تفضيل المخزن المختار عند التساوي.
/// </summary>
public class MaterialAvailabilityService
{
    private static readonly WarehouseType[] FallbackTypes = { WarehouseType.Main, WarehouseType.Sub };
    private readonly ProjectDbContext _db;

    public MaterialAvailabilityService(ProjectDbContext db) => _db = db;

    /// <summary>
    /// مخازن الصرف: كل مخازن المواد الأولية الفعّالة، والمخزن المختار دائمًا. إن لم يُعرَّف أي مخزن مواد أولية
    /// تُستخدم المخازن الرئيسية والفرعية. لا تدخل أبدًا مخازن المنتج التام أو التالف أو سيارات المندوبين.
    /// </summary>
    public async Task<List<Warehouse>> SourceWarehousesAsync(int? preferredWarehouseId = null)
    {
        // نوع المخزن مخزّن نصًا — التصفية في الذاكرة (جدول صغير)
        var all = await _db.Warehouses.AsNoTracking().ToListAsync();
        var raw = all.Where(w => w.IsActive && w.WarehouseType == WarehouseType.RawMaterial).ToList();
        if (raw.Count == 0) raw = all.Where(w => w.IsActive && FallbackTypes.Contains(w.WarehouseType)).ToList();
        if (preferredWarehouseId is int p && raw.All(w => w.Id != p) && all.FirstOrDefault(w => w.Id == p) is { } preferred)
            raw.Add(preferred);
        return raw.OrderBy(w => w.Id != preferredWarehouseId).ThenBy(w => w.Name).ToList();
    }

    private sealed record BatchBalance(int WarehouseId, int? BatchId, DateTime? Expiry, decimal Qty);

    private async Task<List<BatchBalance>> BalancesAsync(IReadOnlyCollection<int> warehouseIds, int itemId)
    {
        var rows = await _db.StockTransactions
            .Where(t => t.ItemId == itemId && warehouseIds.Contains(t.WarehouseId))
            .GroupBy(t => new { t.WarehouseId, t.BatchId, Expiry = t.Batch != null ? t.Batch.ExpiryDate : null })
            .Select(g => new { g.Key.WarehouseId, g.Key.BatchId, g.Key.Expiry, Qty = g.Sum(t => t.QuantityBaseUnits) })
            .ToListAsync();
        return rows.Select(r => new BatchBalance(r.WarehouseId, r.BatchId, r.Expiry, r.Qty)).ToList();
    }

    /// <summary>المتاح الفعلي في مخزن واحد = صافي الرصيد، ولا يتجاوز مجموع التشغيلات الموجبة.</summary>
    private static decimal WarehouseAvailable(IEnumerable<BatchBalance> rows)
    {
        var list = rows.ToList();
        return Math.Max(0, Math.Min(list.Where(b => b.Qty > 0).Sum(b => b.Qty), list.Sum(b => b.Qty)));
    }

    public async Task<Dictionary<int, MaterialAvailability>> GetAsync(IReadOnlyCollection<int> itemIds, int? preferredWarehouseId = null)
    {
        var sources = await SourceWarehousesAsync(preferredWarehouseId);
        var sourceIds = sources.Select(w => w.Id).ToList();
        var names = await _db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.ItemName);
        // "في مخازن أخرى" لا يشمل رصيد تحت التصنيع (مصروف للماكينات أصلًا)
        var wipIds = await _db.Machines.Select(m => m.WipWarehouseId).ToListAsync();
        var totals = await _db.StockTransactions.Where(t => itemIds.Contains(t.ItemId) && !wipIds.Contains(t.WarehouseId))
            .GroupBy(t => t.ItemId).Select(g => new { g.Key, Qty = g.Sum(t => t.QuantityBaseUnits) })
            .ToDictionaryAsync(x => x.Key, x => x.Qty);

        var result = new Dictionary<int, MaterialAvailability>();
        foreach (var id in itemIds.Distinct())
        {
            var balances = await BalancesAsync(sourceIds, id);
            var byWarehouse = sources
                .Select(w => (w.Id, w.Name, WarehouseAvailable(balances.Where(b => b.WarehouseId == w.Id))))
                .Where(x => x.Item3 > 0).ToList();
            var available = byWarehouse.Sum(x => x.Item3);
            var inSources = balances.Sum(b => b.Qty);
            result[id] = new MaterialAvailability
            {
                ItemId = id,
                ItemName = names.GetValueOrDefault(id, ""),
                Available = available,
                ElsewhereQuantity = Math.Max(0, totals.GetValueOrDefault(id) - inSources),
                ByWarehouse = byWarehouse
            };
        }
        return result;
    }

    /// <summary>
    /// يوزّع الكمية على تشغيلات مخازن المواد الأولية بترتيب الأقرب انتهاءً (FEFO)، ثم المخزن المختار عند التساوي.
    /// يرفض كل الصرف إن لم يكفِ المتاح، برسالة توضّح أين يوجد الرصيد.
    /// </summary>
    public async Task<(List<MaterialAllocation> allocation, string? error)> AllocateAsync(int itemId, decimal quantity, int? preferredWarehouseId = null)
    {
        if (quantity <= 0) return (new(), "الكمية يجب أن تكون أكبر من صفر");
        var sources = await SourceWarehousesAsync(preferredWarehouseId);
        var sourceIds = sources.Select(w => w.Id).ToList();
        var balances = await BalancesAsync(sourceIds, itemId);

        var caps = sourceIds.ToDictionary(id => id, id => WarehouseAvailable(balances.Where(b => b.WarehouseId == id)));
        var available = caps.Values.Sum();
        if (available < quantity)
        {
            var info = (await GetAsync(new[] { itemId }, preferredWarehouseId))[itemId];
            var msg = $"الرصيد غير كافٍ للصنف \"{info.ItemName}\": المطلوب {quantity:0.###}، المتاح في مخازن المواد الأولية {available:0.###}";
            if (info.ElsewhereQuantity > 0)
                msg += $" (يوجد {info.ElsewhereQuantity:0.###} في مخازن أخرى — انقله إلى مخزن المواد الأولية أولًا)";
            return (new(), msg);
        }

        var result = new List<MaterialAllocation>();
        var remaining = quantity;
        foreach (var b in balances.Where(b => b.Qty > 0)
                     .OrderBy(b => b.Expiry is null).ThenBy(b => b.Expiry)
                     .ThenBy(b => b.WarehouseId != preferredWarehouseId).ThenBy(b => b.BatchId))
        {
            if (remaining <= 0) break;
            var take = Math.Min(Math.Min(remaining, b.Qty), caps[b.WarehouseId]);
            if (take <= 0) continue;
            result.Add(new MaterialAllocation(b.WarehouseId, b.BatchId, take));
            caps[b.WarehouseId] -= take;
            remaining -= take;
        }
        return (result, null);
    }
}
