namespace ERP.Data.ProjectDb.Entities;

public enum SyncConflictStatus { Pending, Resolved }

public class SyncConflict
{
    public int Id { get; set; }

    public int StockTransactionId { get; set; }
    public StockTransaction StockTransaction { get; set; } = null!;

    public int EmployeeId { get; set; }   // المندوب
    public Employee Employee { get; set; } = null!;

    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;

    public int? BatchId { get; set; }
    public ItemBatch? Batch { get; set; }

    public decimal RequestedQuantity { get; set; }
    public decimal ResultingBalance { get; set; }   // سالب وقت اكتشاف التعارض

    public SyncConflictStatus Status { get; set; } = SyncConflictStatus.Pending;
    public string? ResolutionNotes { get; set; }

    public int? ResolvedByUserId { get; set; }
    public User? ResolvedByUser { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
