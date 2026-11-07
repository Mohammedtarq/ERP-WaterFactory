namespace ERP.Data.ProjectDb.Entities;

public enum ExtraProductionStatus { Pending, Approved, Partial, Rejected }

/// <summary>
/// طلب إنتاج إضافي على سطر من أمر إنتاج (77_extra_production.sql): عدد عبوات زائد من الصنف/المتغير نفسه.
/// عند موافقة أمين المخزن تُصرف المواد الأولية الزائدة ويُدخل الناتج مخزن المنتج التام على تشغيلة الأمر نفسها.
/// </summary>
public class ExtraProductionRequest
{
    public int Id { get; set; }
    public int ProductionOrderId { get; set; }
    public ProductionOrder ProductionOrder { get; set; } = null!;
    public int ProductionOrderLineId { get; set; }
    public ProductionOrderLine ProductionOrderLine { get; set; } = null!;
    public int PackagingLevelId { get; set; }
    public ItemPackagingLevel PackagingLevel { get; set; } = null!;
    public decimal Packs { get; set; }
    public decimal Pieces { get; set; }
    public string Reason { get; set; } = "";
    public ExtraProductionStatus Status { get; set; } = ExtraProductionStatus.Pending;
    public decimal? ApprovedPacks { get; set; }
    public int RequestedByUserId { get; set; }
    public User RequestedByUser { get; set; } = null!;
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public int? DecidedByUserId { get; set; }
    public User? DecidedByUser { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }
}
