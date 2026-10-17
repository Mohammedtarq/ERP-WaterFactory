namespace ERP.Data.ProjectDb.Entities;

/// <summary>سجل تعديل رقم دفعة إنتاج: القديم والجديد ومن غيّره ومتى (16_batch_numbers.sql).</summary>
public class BatchNumberChange
{
    public int Id { get; set; }
    public int BatchId { get; set; }
    public ItemBatch Batch { get; set; } = null!;
    public string OldNumber { get; set; } = string.Empty;
    public string NewNumber { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public int ChangedByUserId { get; set; }
    public User ChangedByUser { get; set; } = null!;
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
}
