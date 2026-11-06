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
    /// <summary>الأصناف المستخدمة لهذه المادة؛ IsShared = مادة مشتركة مجمَّعة بين أكثر من صنف.</summary>
    public string UsedBy { get; set; } = string.Empty;
    public bool IsShared { get; set; }
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

    public Task<List<ManufacturingRequirementRow>> CalculateAsync(int finishedItemId, decimal quantityToProduce, int? customRecipeId = null, int? preferredWarehouseId = null) =>
        CalculateForLinesAsync(new[] { new ProductionLineInput(finishedItemId, quantityToProduce, customRecipeId) }, preferredWarehouseId);

    /// <summary>
    /// احتياجات عدة أصناف معًا (أمر متعدد الأصناف): المواد المشتركة تُجمَّع في سطر واحد بمجموع كمياتها
    /// مع بيان الأصناف المستخدمة لها، والمكونات الخاصة بصنف تبقى سطرًا مستقلًا.
    /// </summary>
    public async Task<List<ManufacturingRequirementRow>> CalculateForLinesAsync(IReadOnlyList<ProductionLineInput> lines, int? preferredWarehouseId = null)
    {
        var production = new ProductionService(_db);
        var needs = new List<(int rawItemId, decimal qty, int finishedItemId)>();
        foreach (var l in lines)
        {
            var (merged, error) = await production.MergeRecipeAsync(l.FinishedItemId, l.CustomRecipeId);
            if (error is not null) continue;
            needs.AddRange(merged.Select(m => (m.rawItemId, m.perUnit * l.Quantity, l.FinishedItemId)));
        }
        if (needs.Count == 0) return new List<ManufacturingRequirementRow>();

        var finishedIds = lines.Select(l => l.FinishedItemId).ToList();
        var finishedNames = await _db.Items.AsNoTracking().Where(i => finishedIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.ItemName);
        var availability = await new MaterialAvailabilityService(_db).GetAsync(needs.Select(n => n.rawItemId).Distinct().ToList(), preferredWarehouseId);
        return needs.GroupBy(n => n.rawItemId).Select(g =>
        {
            var a = availability[g.Key];
            var users = g.Select(n => n.finishedItemId).Distinct().ToList();
            return new ManufacturingRequirementRow
            {
                RawMaterialItemId = g.Key,
                RawMaterialName = a.ItemName,
                QuantityRequired = g.Sum(n => n.qty),
                QuantityAvailable = a.Available,
                ElsewhereQuantity = a.ElsewhereQuantity,
                WhereText = string.Join("، ", a.ByWarehouse.Select(w => $"{w.warehouseName}: {w.qty:N0}")),
                UsedBy = string.Join("، ", users.Select(id => finishedNames.GetValueOrDefault(id, ""))),
                IsShared = users.Count > 1
            };
        }).OrderByDescending(r => r.IsShared).ThenBy(r => r.RawMaterialName).ToList();
    }
}
