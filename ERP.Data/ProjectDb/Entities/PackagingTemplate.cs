namespace ERP.Data.ProjectDb.Entities;

/// <summary>قالب تعبئة (مثل "330×40 كارتون"): أدوار المكونات ونسبها لكل وحدة منتج (18_packaging_templates.sql).</summary>
public class PackagingTemplate
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public ICollection<PackagingTemplateLine> Lines { get; set; } = new List<PackagingTemplateLine>();
}

/// <summary>دور مكوّن في القالب: كارتون 1 لكل 40، امبولة 1 لكل 1، لاصق 2 لكل 1...</summary>
public class PackagingTemplateLine
{
    public int Id { get; set; }
    public int TemplateId { get; set; }
    public PackagingTemplate Template { get; set; } = null!;
    public string ComponentRole { get; set; } = string.Empty;
    public int? DefaultItemId { get; set; }
    public Item? DefaultItem { get; set; }
    public decimal ComponentQuantity { get; set; } = 1;
    public decimal PerUnits { get; set; } = 1;
    /// <summary>كمية المكوّن لكل وحدة منتج.</summary>
    public decimal QuantityPerUnit => Math.Round(ComponentQuantity / PerUnits, 6);
    public string RatioText => $"{ComponentQuantity:0.##} لكل {PerUnits:0.##}";
}

/// <summary>استبدال مكوّن في أمر إنتاج واحد (مثل لون غطاء آخر) بسببه — لا يغيّر تعريف الصنف.</summary>
public class ProductionOrderComponentOverride
{
    public int Id { get; set; }
    public int ProductionOrderLineId { get; set; }
    public ProductionOrderLine Line { get; set; } = null!;
    public int OriginalItemId { get; set; }
    public Item OriginalItem { get; set; } = null!;
    public int ReplacementItemId { get; set; }
    public Item ReplacementItem { get; set; } = null!;
    public decimal Quantity { get; set; }
    public string Reason { get; set; } = string.Empty;
    public int ChangedByUserId { get; set; }
    public User ChangedByUser { get; set; } = null!;
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
}
