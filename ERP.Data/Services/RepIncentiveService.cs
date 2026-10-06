using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>سطر جدول حوافز المندوبين: مندوب × صنف × وحدة (شرنك، كارتون) بالعدد.</summary>
public record RepIncentiveRow(int RepEmployeeId, string RepName, int ItemId, string ItemName, int PackagingLevelId, string LevelName,
                              decimal Loaded, decimal Returned, decimal Free, decimal Rate)
{
    /// <summary>المباع = المحمّل − الراجع − المجاني.</summary>
    public decimal Net => Loaded - Returned - Free;
    public decimal Amount => Math.Round(Net * Rate, 2);
}

/// <summary>
/// حافز المندوب بالعبوة: المحمّل (مستندات التحميل) − الراجع (مستندات الإرجاع والتسوية) − المجاني، بكل وحدة تعبئة،
/// × مبلغ الإدارة لتلك الوحدة. يتجمع على الشهر ويُصرف مرة واحدة مع الراتب (لا قيد يومي).
/// </summary>
public class RepIncentiveService
{
    private readonly ProjectDbContext _db;
    public RepIncentiveService(ProjectDbContext db) => _db = db;

    public async Task<List<RepIncentiveRow>> RowsAsync(DateTime from, DateTime to, int? repEmployeeId = null)
    {
        var (f, t) = (from.Date, to.Date);
        var docs = await _db.StockDocumentLines.AsNoTracking()
            .Where(l => l.StockDocument.RepEmployeeId != null
                        && (l.StockDocument.DocumentType == StockDocumentType.RepLoad || l.StockDocument.DocumentType == StockDocumentType.RepReturn)
                        && l.StockDocument.DocumentDate >= f && l.StockDocument.DocumentDate <= t
                        && (repEmployeeId == null || l.StockDocument.RepEmployeeId == repEmployeeId))
            .GroupBy(l => new { Rep = l.StockDocument.RepEmployeeId!.Value, l.ItemId, l.PackagingLevelId, l.StockDocument.DocumentType })
            .Select(g => new { g.Key.Rep, g.Key.ItemId, g.Key.PackagingLevelId, g.Key.DocumentType, Qty = g.Sum(x => x.QuantityInLevel) })
            .ToListAsync();
        var free = await _db.RepFreeGoods.AsNoTracking()
            .Where(x => x.PackagingLevelId != null && x.RepSettlement.SettlementDate >= f && x.RepSettlement.SettlementDate <= t
                        && (repEmployeeId == null || x.RepSettlement.RepEmployeeId == repEmployeeId))
            .GroupBy(x => new { Rep = x.RepSettlement.RepEmployeeId, x.ItemId, Level = x.PackagingLevelId!.Value })
            .Select(g => new { g.Key.Rep, g.Key.ItemId, g.Key.Level, Qty = g.Sum(x => x.QuantityInLevel ?? 0) })
            .ToListAsync();

        var keys = docs.Select(d => (d.Rep, d.ItemId, Level: d.PackagingLevelId)).Concat(free.Select(x => (x.Rep, x.ItemId, x.Level))).Distinct().ToList();
        if (keys.Count == 0) return new();
        var repIds = keys.Select(k => k.Rep).Distinct().ToList();
        var itemIds = keys.Select(k => k.ItemId).Distinct().ToList();
        var levelIds = keys.Select(k => k.Level).Distinct().ToList();
        var reps = await _db.Employees.AsNoTracking().Where(e => repIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.FullName);
        var items = await _db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.ItemName);
        var levels = await _db.ItemPackagingLevels.AsNoTracking().Where(l => levelIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id);
        var rates = await _db.RepItemIncentiveRates.AsNoTracking().Where(r => levelIds.Contains(r.PackagingLevelId))
                             .ToDictionaryAsync(r => r.PackagingLevelId, r => r.IncentiveRatePerUnit);

        decimal Doc(int rep, int item, int level, StockDocumentType type) =>
            docs.Where(d => d.Rep == rep && d.ItemId == item && d.PackagingLevelId == level && d.DocumentType == type).Sum(d => d.Qty);
        return keys.Select(k => new RepIncentiveRow(k.Rep, reps.GetValueOrDefault(k.Rep, "؟"), k.ItemId, items.GetValueOrDefault(k.ItemId, "؟"),
                                                    k.Level, levels[k.Level].LevelName,
                                                    Doc(k.Rep, k.ItemId, k.Level, StockDocumentType.RepLoad),
                                                    Doc(k.Rep, k.ItemId, k.Level, StockDocumentType.RepReturn),
                                                    free.Where(x => x.Rep == k.Rep && x.ItemId == k.ItemId && x.Level == k.Level).Sum(x => x.Qty),
                                                    rates.GetValueOrDefault(k.Level)))
                   .OrderBy(r => r.RepName).ThenBy(r => r.ItemName).ThenByDescending(r => levels[r.PackagingLevelId].EquivalentBaseUnits).ToList();
    }

    /// <summary>حافز المندوب لفترة (للراتب: الشهر كاملًا).</summary>
    public async Task<decimal> AmountAsync(int repEmployeeId, DateTime from, DateTime to) =>
        (await RowsAsync(from, to, repEmployeeId)).Sum(r => r.Amount);

    /// <summary>جدول أسعار الحافز: كل وحدة عبوة (أكبر من القطعة) لكل منتج مُصنَّع، وأي وحدة لها سعر، بسعرها الحالي (0 إن لم يُحدَّد).</summary>
    public async Task<List<RepIncentiveRateRow>> RatesAsync()
    {
        var levels = await _db.ItemPackagingLevels.AsNoTracking()
            .Where(l => (l.EquivalentBaseUnits > 1 && l.Item.IsActive && l.Item.SourcingMethod == SourcingMethod.Manufactured)
                        || _db.RepItemIncentiveRates.Any(r => r.PackagingLevelId == l.Id))
            .Select(l => new { l.Id, l.ItemId, l.Item.ItemName, l.LevelName, l.EquivalentBaseUnits }).ToListAsync();
        var rates = await _db.RepItemIncentiveRates.AsNoTracking().ToDictionaryAsync(r => r.PackagingLevelId, r => r.IncentiveRatePerUnit);
        return levels.OrderBy(l => l.ItemName).ThenByDescending(l => l.EquivalentBaseUnits)
                     .Select(l => new RepIncentiveRateRow(l.ItemId, l.ItemName, l.Id, l.LevelName, l.EquivalentBaseUnits) { Rate = rates.GetValueOrDefault(l.Id) })
                     .ToList();
    }

    public async Task<FinanceOperationResult> SaveRatesAsync(IReadOnlyCollection<RepIncentiveRateRow> rows, int userId)
    {
        if (rows.Any(r => r.Rate < 0)) return FinanceOperationResult.Fail("الحافز لا يكون سالبًا");
        var existing = await _db.RepItemIncentiveRates.ToDictionaryAsync(r => r.PackagingLevelId);
        var changes = new List<string>();
        foreach (var r in rows)
        {
            var old = existing.GetValueOrDefault(r.PackagingLevelId);
            if ((old?.IncentiveRatePerUnit ?? 0) == r.Rate) continue;
            if (old is null) _db.RepItemIncentiveRates.Add(new RepItemIncentiveRate { ItemId = r.ItemId, PackagingLevelId = r.PackagingLevelId, IncentiveRatePerUnit = r.Rate });
            else if (r.Rate == 0) _db.RepItemIncentiveRates.Remove(old);
            else old.IncentiveRatePerUnit = r.Rate;
            changes.Add($"{r.ItemName} {r.LevelName}: {old?.IncentiveRatePerUnit ?? 0:N0} ← {r.Rate:N0}");
        }
        if (changes.Count == 0) return FinanceOperationResult.Fail("لا تغيير في أسعار الحافز");
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Edit", "RepItemIncentiveRates", null, "حافز المندوب: " + string.Join("، ", changes));
        return FinanceOperationResult.Ok();
    }
}

/// <summary>سعر حافز وحدة عبوة لمنتج (قابل للتعديل في الشاشة).</summary>
public record RepIncentiveRateRow(int ItemId, string ItemName, int PackagingLevelId, string LevelName, decimal Pieces)
{
    public decimal Rate { get; set; }
}
