using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

public enum RepVanStatus { Settled, Pending, Overdue, Idle }

/// <summary>بطاقة سيارة مندوب في لوحة «سيارات المندوبين».</summary>
public record RepVanCard(int VanWarehouseId, string VanName, int RepEmployeeId, string RepName, string? Territories,
                         decimal BalancePieces, string BalanceText, string TodayLoadText, decimal TodayLoadPieces,
                         RepVanStatus Status, int DaysOverdue, DateTime? LastSettlement, decimal ExpectedCash)
{
    public string StatusText => Status switch
    {
        RepVanStatus.Settled => "تمت تسوية اليوم",
        RepVanStatus.Pending => TodayLoadPieces > 0 ? "محمّل — بانتظار التسوية" : "في السيارة رصيد — بانتظار التسوية",
        RepVanStatus.Overdue => $"متأخر عن التسوية {DaysOverdue} يوم",
        _ => "لا حمولة اليوم"
    };
    public bool NeedsSettlement => Status is RepVanStatus.Pending or RepVanStatus.Overdue;
}

/// <summary>
/// لوحة سيارات المندوبين: بطاقة لكل سيارة بدل تبويب لكل سيارة — الرصيد الآن بعبوات كل منتج (الشرنك والكارتون منتجان
/// مستقلان)، وحمولة اليوم كما في طلب التحميل، وحالة التسوية، والنقد المتوقع في محفظة المندوب.
/// </summary>
public class RepVanBoardService
{
    private readonly ProjectDbContext _db;
    public RepVanBoardService(ProjectDbContext db) => _db = db;

    public async Task<List<RepVanCard>> CardsAsync(DateTime today)
    {
        var day = today.Date;
        var vans = await _db.Warehouses.AsNoTracking().Where(w => w.IsActive && w.WarehouseType == WarehouseType.RepVan && w.OwnerEmployeeId != null)
            .Select(w => new { w.Id, w.Name, RepId = w.OwnerEmployeeId!.Value, RepName = w.OwnerEmployee!.FullName }).ToListAsync();
        if (vans.Count == 0) return new();
        var vanIds = vans.Select(v => v.Id).ToList();
        var repIds = vans.Select(v => v.RepId).ToList();

        var balances = await _db.StockTransactions.AsNoTracking().Where(t => vanIds.Contains(t.WarehouseId))
            .GroupBy(t => new { t.WarehouseId, t.ItemId }).Select(g => new { g.Key.WarehouseId, g.Key.ItemId, Qty = g.Sum(t => t.QuantityBaseUnits) })
            .Where(x => x.Qty != 0).ToListAsync();
        var loads = await _db.StockDocumentLines.AsNoTracking()
            .Where(l => l.StockDocument.DocumentType == StockDocumentType.RepLoad && l.StockDocument.DocumentDate == day
                        && l.StockDocument.CounterWarehouseId != null && vanIds.Contains(l.StockDocument.CounterWarehouseId.Value))
            .GroupBy(l => new { Van = l.StockDocument.CounterWarehouseId!.Value, l.ItemId, l.PackagingLevelId, l.PackagingLevel.LevelName, l.PackagingLevel.EquivalentBaseUnits })
            .Select(g => new { g.Key.Van, g.Key.ItemId, g.Key.LevelName, g.Key.EquivalentBaseUnits, Qty = g.Sum(l => l.QuantityInLevel), Pieces = g.Sum(l => l.QuantityBaseUnits) })
            .ToListAsync();
        var itemIds = balances.Select(b => b.ItemId).Concat(loads.Select(l => l.ItemId)).Distinct().ToList();
        var items = await _db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.ItemName);
        var levels = (await _db.ItemPackagingLevels.AsNoTracking().Where(l => itemIds.Contains(l.ItemId)).ToListAsync())
            .GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.EquivalentBaseUnits).ToList());
        var settledToday = (await _db.RepSettlements.AsNoTracking().Where(s => s.SettlementDate == day && vanIds.Contains(s.VanWarehouseId))
                                     .Select(s => s.VanWarehouseId).ToListAsync()).ToHashSet();
        var lastSettlement = await _db.RepSettlements.AsNoTracking().Where(s => repIds.Contains(s.RepEmployeeId))
            .GroupBy(s => s.RepEmployeeId).Select(g => new { g.Key, Last = g.Max(s => s.SettlementDate) }).ToDictionaryAsync(x => x.Key, x => (DateTime?)x.Last);
        var wallets = await _db.RepWalletTransactions.AsNoTracking().Where(w => repIds.Contains(w.EmployeeId))
            .GroupBy(w => w.EmployeeId).Select(g => new { g.Key, Balance = g.Sum(w => w.AmountIn - w.AmountOut) }).ToDictionaryAsync(x => x.Key, x => x.Balance);
        var territories = (await _db.RepTerritories.AsNoTracking().Where(t => repIds.Contains(t.EmployeeId)).ToListAsync())
            .GroupBy(t => t.EmployeeId).ToDictionary(g => g.Key, g => string.Join("، ", g.Select(t => t.TerritoryName).OrderBy(n => n)));
        var overdue = (await new RepOperationsService(_db).GetOverdueAsync(day)).ToDictionary(o => o.RepEmployeeId, o => o.DaysOverdue);

        return vans.Select(v =>
        {
            var bal = balances.Where(b => b.WarehouseId == v.Id).ToList();
            var load = loads.Where(l => l.Van == v.Id).OrderBy(l => items.GetValueOrDefault(l.ItemId)).ThenByDescending(l => l.EquivalentBaseUnits).ToList();
            var loadPieces = load.Sum(l => l.Pieces);
            var pieces = bal.Sum(b => b.Qty);
            var status = overdue.ContainsKey(v.RepId) ? RepVanStatus.Overdue
                       : settledToday.Contains(v.Id) && pieces <= 0 ? RepVanStatus.Settled
                       : loadPieces > 0 || pieces > 0 ? RepVanStatus.Pending
                       : settledToday.Contains(v.Id) ? RepVanStatus.Settled
                       : RepVanStatus.Idle;
            return new RepVanCard(v.Id, v.Name, v.RepId, v.RepName, territories.GetValueOrDefault(v.RepId), pieces,
                bal.Count == 0 ? "فارغة" : string.Join("، ", bal.OrderBy(b => items.GetValueOrDefault(b.ItemId))
                    .Select(b => $"{items.GetValueOrDefault(b.ItemId, "؟")}: {WarehouseDocumentService.Breakdown(b.Qty, levels.GetValueOrDefault(b.ItemId))}")),
                load.Count == 0 ? "—" : string.Join("، ", load.GroupBy(l => l.ItemId)
                    .Select(g => $"{items.GetValueOrDefault(g.Key, "؟")}: {string.Join(" + ", g.Select(l => $"{l.Qty:#,0.##} {l.LevelName}"))}")),
                loadPieces, status, overdue.GetValueOrDefault(v.RepId), lastSettlement.GetValueOrDefault(v.RepId), wallets.GetValueOrDefault(v.RepId));
        })
        .OrderBy(c => c.Status switch { RepVanStatus.Overdue => 0, RepVanStatus.Pending => 1, RepVanStatus.Settled => 2, _ => 3 })
        .ThenBy(c => c.RepName).ToList();
    }
}
