using ERP.Data.ProjectDb;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

public class CurrentStockRow
{
    public int ItemId { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string WarehouseName { get; set; } = string.Empty;
    public decimal QuantityBaseUnits { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Value => QuantityBaseUnits * UnitPrice;
    public string? BatchNumber { get; set; }
    public DateTime? ExpiryDate { get; set; }
    /// <summary>الكمية بالعبوات: "50 شرنك" (المنتج المصنَّع بعبوة واحدة فالعدد دقيق).</summary>
    public string Breakdown { get; set; } = "";
}

/// <summary>
/// يحسب الرصيد الحالي من سجل الحركة (StockTransactions) مباشرة — لا يوجد
/// عمود رصيد مخزَّن في أي مكان، تمامًا كما في تصميم قاعدة البيانات الأصلي.
/// </summary>
public class StockQueryService
{
    private readonly ProjectDbContext _db;

    public StockQueryService(ProjectDbContext db)
    {
        _db = db;
    }

    public async Task<List<CurrentStockRow>> GetCurrentStockAsync(int? warehouseId = null)
    {
        var query = _db.StockTransactions
            .Include(t => t.Item)
            .Include(t => t.Warehouse)
            .Include(t => t.Batch)
            .AsQueryable();

        if (warehouseId.HasValue)
            query = query.Where(t => t.WarehouseId == warehouseId.Value);

        var grouped = await query
            .GroupBy(t => new
            {
                t.ItemId,
                t.Item.ItemCode,
                t.Item.ItemName,
                t.Item.SalePrice,
                t.WarehouseId,
                t.Warehouse.Name,
                t.BatchId,
                BatchNumber = t.Batch != null ? t.Batch.BatchNumber : null,
                ExpiryDate = t.Batch != null ? t.Batch.ExpiryDate : null
            })
            .Select(g => new CurrentStockRow
            {
                ItemId = g.Key.ItemId,
                ItemCode = g.Key.ItemCode,
                ItemName = g.Key.ItemName,
                WarehouseName = g.Key.Name,
                UnitPrice = g.Key.SalePrice,
                BatchNumber = g.Key.BatchNumber,
                ExpiryDate = g.Key.ExpiryDate,
                QuantityBaseUnits = g.Sum(t => t.QuantityBaseUnits)
            })
            .ToListAsync();

        // إخفاء الأسطر التي تصفّرت كميتها تمامًا (وارد بالكامل ثم صادر بالكامل)
        var rows = grouped.Where(r => r.QuantityBaseUnits != 0).OrderBy(r => r.ItemName).ToList();
        var itemIds = rows.Select(r => r.ItemId).Distinct().ToList();
        var levels = (await _db.ItemPackagingLevels.AsNoTracking().Where(l => itemIds.Contains(l.ItemId)).ToListAsync())
            .GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.EquivalentBaseUnits).ToList());
        foreach (var r in rows) r.Breakdown = WarehouseDocumentService.Breakdown(r.QuantityBaseUnits, levels.GetValueOrDefault(r.ItemId));
        return rows;
    }
}
