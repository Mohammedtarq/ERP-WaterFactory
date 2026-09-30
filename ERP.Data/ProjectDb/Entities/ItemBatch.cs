namespace ERP.Data.ProjectDb.Entities;

public class ItemBatch
{
    public int Id { get; set; }

    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;

    public string BatchNumber { get; set; } = string.Empty;
    public DateTime? ManufactureDate { get; set; }
    public DateTime? ExpiryDate { get; set; }

    public int? ProductionOrderId { get; set; }   // يُربط فعليًا في مرحلة الإنتاج لاحقًا
}
