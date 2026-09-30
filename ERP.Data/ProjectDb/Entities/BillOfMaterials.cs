namespace ERP.Data.ProjectDb.Entities;

public class BillOfMaterials
{
    public int Id { get; set; }

    public int FinishedItemId { get; set; }
    public Item FinishedItem { get; set; } = null!;

    public string Name { get; set; } = "الوصفة الأساسية";
    public bool IsActive { get; set; } = true;

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
}
