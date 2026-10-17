namespace ERP.Data.ProjectDb.Entities;

public class BillOfMaterials
{
    public int Id { get; set; }

    public int FinishedItemId { get; set; }
    public Item FinishedItem { get; set; } = null!;

    public string Name { get; set; } = "الوصفة الأساسية";
    public bool IsActive { get; set; } = true;

    /// <summary>قالب التعبئة الذي مُلئت منه القائمة (إن وُجد).</summary>
    public int? PackagingTemplateId { get; set; }
    public PackagingTemplate? PackagingTemplate { get; set; }

    public ICollection<BOMLine> Lines { get; set; } = new List<BOMLine>();
}

public class BOMLine
{
    public int Id { get; set; }

    public int BOMId { get; set; }
    public BillOfMaterials BOM { get; set; } = null!;

    public int RawMaterialItemId { get; set; }
    public Item RawMaterialItem { get; set; } = null!;

    public decimal QuantityPerUnit { get; set; }

    /// <summary>دور المكوّن (كارتون، غطاء، لاصق...) — يُستخدم لاختيار بديل العميل.</summary>
    public string? ComponentRole { get; set; }
}
