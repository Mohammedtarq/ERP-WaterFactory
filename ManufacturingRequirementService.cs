using ERP.Data.ProjectDb;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

public class ManufacturingRequirementRow
{
    public string RawMaterialName { get; set; } = string.Empty;
    public decimal QuantityRequired { get; set; }
    public decimal QuantityAvailable { get; set; }
    public decimal Shortfall => Math.Max(0, QuantityRequired - QuantityAvailable);
    public bool IsSufficient => QuantityAvailable >= QuantityRequired;
}

/// <summary>
/// "احتياجات التصنيع": يحسب المواد الأولية المطلوبة لإنتاج كمية معيّنة من
/// منتج نهائي (حسب قائمة المواد BOM)، ويقارنها بالرصيد الفعلي الحالي — دون
/// إنشاء أي أمر إنتاج فعلي، مجرد أداة تخطيط قبل القرار.
/// </summary>
public class ManufacturingRequirementService
{
    private readonly ProjectDbContext _db;
    private readonly StockQueryService _stock;

    public ManufacturingRequirementService(ProjectDbContext db)
    {
        _db = db;
        _stock = new StockQueryService(db);
    }

    public async Task<List<ManufacturingRequirementRow>> CalculateAsync(int finishedItemId, decimal quantityToProduce)
    {
        var bom = await _db.BillOfMaterials
            .Include(b => b.Lines).ThenInclude(l => l.RawMaterialItem)
            .Where(b => b.FinishedItemId == finishedItemId && b.IsActive)
            .FirstOrDefaultAsync();

        if (bom is null) return new List<ManufacturingRequirementRow>();

        var currentStock = await _stock.GetCurrentStockAsync();

        var rows = new List<ManufacturingRequirementRow>();
        foreach (var line in bom.Lines)
        {
            decimal required = line.QuantityPerUnit * quantityToProduce;
            decimal available = currentStock
                .Where(s => s.ItemCode == line.RawMaterialItem.ItemCode)
                .Sum(s => s.QuantityBaseUnits);

            rows.Add(new ManufacturingRequirementRow
            {
                RawMaterialName = line.RawMaterialItem.ItemName,
                QuantityRequired = required,
                QuantityAvailable = available
            });
        }

        return rows;
    }
}
