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
    /// <summary>تطبيق المندوب: آخر اتصال للهاتف، والطلبات بانتظار الاعتماد.</summary>
    public DateTime? LastSeenAt { get; init; }
    public int PendingRequests { get; init; }
    public string AppText => LastSeenAt is null ? "التطبيق: لا جهاز متصل"
        : $"آخر اتصال: {LastSeenAt.Value.ToLocalTime():dd/MM HH:mm}{(PendingRequests > 0 ? $" — {PendingRequests} بانتظار الاعتماد" : "")}";
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

        // الرصيد وحمولة اليوم حسب (الصنف + المتغير): الطلبات الخاصة (ليبل مطعم/مناسبة) تظهر مستقلة عن الأساسي
        var balances = await _db.StockTransactions.AsNoTracking().Where(t => vanIds.Contains(t.WarehouseId))
            .GroupBy(t => new { t.WarehouseId, t.ItemId, RecipeId = t.Batch != null ? t.Batch.CustomRecipeId : null })
            .Select(g => new { g.Key.WarehouseId, g.Key.ItemId, g.Key.RecipeId, Qty = g.Sum(t => t.QuantityBaseUnits) })
            .Where(x => x.Qty != 0).ToListAsync();
        var (from, to) = (day.ToUniversalTime(), day.AddDays(1).ToUniversalTime());
        var loads = await _db.StockTransactions.AsNoTracking()
            .Where(t => vanIds.Contains(t.WarehouseId) && t.TransactionType == StockTransactionType.RepLoad && t.QuantityBaseUnits > 0
                        && t.TransactionDate >= from && t.TransactionDate < to)
            .GroupBy(t => new { Van = t.WarehouseId, t.ItemId, RecipeId = t.Batch != null ? t.Batch.CustomRecipeId : null })
            .Select(g => new { g.Key.Van, g.Key.ItemId, g.Key.RecipeId, Pieces = g.Sum(t => t.QuantityBaseUnits) })
            .ToListAsync();
        // حمولة اليوم بوحداتها كما في المستند (شرنك/كارتون) — تُستعمل متى لم يكن في الصنف متغير
        var loadLines = await _db.StockDocumentLines.AsNoTracking()
            .Where(l => l.StockDocument.DocumentType == StockDocumentType.RepLoad && l.StockDocument.DocumentDate == day
                        && l.StockDocument.CounterWarehouseId != null && vanIds.Contains(l.StockDocument.CounterWarehouseId.Value))
            .GroupBy(l => new { Van = l.StockDocument.CounterWarehouseId!.Value, l.ItemId, l.PackagingLevel.LevelName, l.PackagingLevel.EquivalentBaseUnits })
            .Select(g => new { g.Key.Van, g.Key.ItemId, g.Key.LevelName, g.Key.EquivalentBaseUnits, Qty = g.Sum(l => l.QuantityInLevel), Pieces = g.Sum(l => l.QuantityBaseUnits) })
            .ToListAsync();
        var itemIds = balances.Select(b => b.ItemId).Concat(loads.Select(l => l.ItemId)).Concat(loadLines.Select(l => l.ItemId)).Distinct().ToList();
        var items = await _db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.ItemName);
        var recipeIds = balances.Select(b => b.RecipeId).Concat(loads.Select(l => l.RecipeId)).OfType<int>().Distinct().ToList();
        var recipes = await _db.CustomRecipes.AsNoTracking().Where(r => recipeIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name);
        var packs = await PackFormatter.LoadAsync(_db, itemIds);
        string Name(int itemId, int? recipeId) =>
            items.GetValueOrDefault(itemId, "؟") + (recipeId is int r ? $" — {recipes.GetValueOrDefault(r, "طلب خاص")}" : "");
        var settledToday = (await _db.RepSettlements.AsNoTracking().Where(s => s.SettlementDate == day && vanIds.Contains(s.VanWarehouseId))
                                     .Select(s => s.VanWarehouseId).ToListAsync()).ToHashSet();
        var lastSettlement = await _db.RepSettlements.AsNoTracking().Where(s => repIds.Contains(s.RepEmployeeId))
            .GroupBy(s => s.RepEmployeeId).Select(g => new { g.Key, Last = g.Max(s => s.SettlementDate) }).ToDictionaryAsync(x => x.Key, x => (DateTime?)x.Last);
        var wallets = await _db.RepWalletTransactions.AsNoTracking().Where(w => repIds.Contains(w.EmployeeId))
            .GroupBy(w => w.EmployeeId).Select(g => new { g.Key, Balance = g.Sum(w => w.AmountIn - w.AmountOut) }).ToDictionaryAsync(x => x.Key, x => x.Balance);
        var territories = (await _db.RepTerritories.AsNoTracking().Where(t => repIds.Contains(t.EmployeeId)).ToListAsync())
            .GroupBy(t => t.EmployeeId).ToDictionary(g => g.Key, g => string.Join("، ", g.Select(t => t.TerritoryName).OrderBy(n => n)));
        var overdue = (await new RepOperationsService(_db).GetOverdueAsync(day)).ToDictionary(o => o.RepEmployeeId, o => o.DaysOverdue);
        var lastSeen = await _db.RepDevices.AsNoTracking().Where(d => repIds.Contains(d.RepEmployeeId) && d.LastSeenAt != null)
            .GroupBy(d => d.RepEmployeeId).Select(g => new { g.Key, Last = g.Max(d => d.LastSeenAt) }).ToDictionaryAsync(x => x.Key, x => x.Last);
        var pendingRequests = await _db.RepRequests.AsNoTracking().Where(r => repIds.Contains(r.RepEmployeeId) && r.Status == RepRequestStatus.Pending)
            .GroupBy(r => r.RepEmployeeId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);

        return vans.Select(v =>
        {
            var bal = balances.Where(b => b.WarehouseId == v.Id).ToList();
            var load = loads.Where(l => l.Van == v.Id).ToList();
            var lines = loadLines.Where(l => l.Van == v.Id).ToList();
            var loadPieces = lines.Sum(l => l.Pieces);
            // صنف بلا متغير: بوحداته كما طُلب؛ صنف فيه متغير: الأساسي وكل متغير مستقل بعبوته
            var withVariant = load.Where(l => l.RecipeId is not null).Select(l => l.ItemId).ToHashSet();
            var loadParts = lines.Where(l => !withVariant.Contains(l.ItemId)).GroupBy(l => l.ItemId)
                .Select(g => (Item: items.GetValueOrDefault(g.Key, "؟"), Order: 0,
                              Text: $"{items.GetValueOrDefault(g.Key, "؟")}: {string.Join(" + ", g.OrderByDescending(l => l.EquivalentBaseUnits).Select(l => $"{l.Qty:#,0.##} {l.LevelName}"))}"))
                .Concat(load.Where(l => withVariant.Contains(l.ItemId))
                    .Select(l => (Item: items.GetValueOrDefault(l.ItemId, "؟"), Order: l.RecipeId is null ? 0 : 1, Text: $"{Name(l.ItemId, l.RecipeId)}: {packs.Of(l.ItemId, l.Pieces)}")))
                .OrderBy(x => x.Item).ThenBy(x => x.Order).Select(x => x.Text).ToList();
            var pieces = bal.Sum(b => b.Qty);
            var status = overdue.ContainsKey(v.RepId) ? RepVanStatus.Overdue
                       : settledToday.Contains(v.Id) && pieces <= 0 ? RepVanStatus.Settled
                       : loadPieces > 0 || pieces > 0 ? RepVanStatus.Pending
                       : settledToday.Contains(v.Id) ? RepVanStatus.Settled
                       : RepVanStatus.Idle;
            return new RepVanCard(v.Id, v.Name, v.RepId, v.RepName, territories.GetValueOrDefault(v.RepId), pieces,
                bal.Count == 0 ? "فارغة" : string.Join("، ", bal.OrderBy(b => items.GetValueOrDefault(b.ItemId)).ThenBy(b => b.RecipeId is not null)
                    .Select(b => $"{Name(b.ItemId, b.RecipeId)}: {packs.Of(b.ItemId, b.Qty)}")),
                loadParts.Count == 0 ? "—" : string.Join("، ", loadParts),
                loadPieces, status, overdue.GetValueOrDefault(v.RepId), lastSettlement.GetValueOrDefault(v.RepId), wallets.GetValueOrDefault(v.RepId))
            {
                LastSeenAt = lastSeen.GetValueOrDefault(v.RepId), PendingRequests = pendingRequests.GetValueOrDefault(v.RepId)
            };
        })
        .OrderBy(c => c.Status switch { RepVanStatus.Overdue => 0, RepVanStatus.Pending => 1, RepVanStatus.Settled => 2, _ => 3 })
        .ThenBy(c => c.RepName).ToList();
    }
}
