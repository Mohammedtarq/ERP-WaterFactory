namespace ERP.Data.ProjectDb.Entities;

/// <summary>
/// حد التنبيه لصنف في مخزن بعينه (41_warehouse_item_alerts.sql) — بالقطعة، ويُقارن برصيد ذلك المخزن وحده.
/// إن لم يوجد: «حد التنبيه» في بطاقة الصنف هو الافتراضي في مخزنه الطبيعي.
/// </summary>
public class WarehouseItemAlert
{
    public int Id { get; set; }
    public int WarehouseId { get; set; }
    public Warehouse Warehouse { get; set; } = null!;
    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;
    public decimal MinQuantity { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int? UpdatedByUserId { get; set; }
}
