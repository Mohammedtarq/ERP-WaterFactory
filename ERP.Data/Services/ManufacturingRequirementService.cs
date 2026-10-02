using ERP.Data.ProjectDb;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

public class ManufacturingRequirementRow
{
    public int RawMaterialItemId { get; set; }
    public string RawMaterialName { get; set; } = string.Empty;
    public decimal QuantityRequired { get; set; }
    public decimal QuantityAvailable { get; set; }
    /// <summary>رصيد في مخازن غير مخصصة للمواد الأولية (لا يُصرف منه دون نقل).</summary>
    public decimal ElsewhereQuantity { get; set; }
    /// <summary>توزيع المتاح على مخازن المواد الأولية.</summary>
    public string WhereText { get; set; } = string.Empty;
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

    public ManufacturingRequirementService(ProjectDbContext db)
    {
        _db = db;
    }

    public async Task<List<ManufacturingRequirementRow>> CalculateAsync(int finishedItemId, decimal quantityToProduce, int? customRecipeId = null, int? preferredWarehouseId = null)
    {
        var (lines, error) = await new ProductionService(_db).MergeRecipeAsync(finishedItemId, customRecipeId);
        if (error is not null) return new List<ManufacturingRequirementRow>();

        var availability = await new MaterialAvailabilityService(_db).GetAsync(lines.Select(l => l.rawItemId).ToList(), preferredWarehouseId);
        return lines.Select(l =>
        {
            var a = availability[l.rawItemId];
            return new ManufacturingRequirementRow
            {
                RawMaterialItemId = l.rawItemId,
                RawMaterialName = a.ItemName,
                QuantityRequired = l.perUnit * quantityToProduce,
                QuantityAvailable = a.Available,
                ElsewhereQuantity = a.ElsewhereQuantity,
                WhereText = string.Join("، ", a.ByWarehouse.Select(w => $"{w.warehouseName}: {w.qty:N0}"))
            };
        }).ToList();
    }
}
