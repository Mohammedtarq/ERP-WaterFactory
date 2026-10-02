namespace ERP.Data.ProjectDb.Entities;

public class ItemBatch
{
    public int Id { get; set; }

    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;

    public string BatchNumber { get; set; } = string.Empty;
    /// <summary>الرقم المولَّد تلقائيًا أول مرة — يُحفظ عند أول تعديل يدوي (16_batch_numbers.sql).</summary>
    public string? OriginalBatchNumber { get; set; }
    public DateTime? ManufactureDate { get; set; }
    public DateTime? ExpiryDate { get; set; }

    public int? ProductionOrderId { get; set; }   // يُربط فعليًا في مرحلة الإنتاج لاحقًا
}
