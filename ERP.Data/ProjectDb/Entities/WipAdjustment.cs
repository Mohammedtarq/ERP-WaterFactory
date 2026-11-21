namespace ERP.Data.ProjectDb.Entities;

public enum WipAdjustmentKind { Remaining, Damaged }

/// <summary>سجل تدقيق تعديل المشرف على "تحت التصنيع": القيمة قبل وبعد، السبب، من ومتى (19_wip_adjustments.sql).</summary>
public class WipAdjustment
{
    public int Id { get; set; }
    public int MachineId { get; set; }
    public Machine Machine { get; set; } = null!;
    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;
    public int? ProductionOrderId { get; set; }
    public ProductionOrder? ProductionOrder { get; set; }
    public WipAdjustmentKind Kind { get; set; }
    public decimal BeforeQuantity { get; set; }
    public decimal AfterQuantity { get; set; }
    public string Reason { get; set; } = string.Empty;
    public int ChangedByUserId { get; set; }
    public User ChangedByUser { get; set; } = null!;
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
}
