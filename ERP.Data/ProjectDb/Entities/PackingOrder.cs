namespace ERP.Data.ProjectDb.Entities;

public class PackingOrder
{
    public int Id { get; set; }

    public int ProductionOrderId { get; set; }
    public ProductionOrder ProductionOrder { get; set; } = null!;

    public int? ProductionOrderLineId { get; set; }
    public ProductionOrderLine? Line { get; set; }

    public int PackagingLevelId { get; set; }
    public ItemPackagingLevel PackagingLevel { get; set; } = null!;

    public decimal UnitsPackaged { get; set; }

    public int ResultingFinishedGoodsWarehouseId { get; set; }   // WarehouseType = FinishedGoods
    public Warehouse ResultingFinishedGoodsWarehouse { get; set; } = null!;

    public DateTime PackingDate { get; set; } = DateTime.UtcNow;

    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
}
