namespace ERP.Data.ProjectDb.Entities;

public enum LocationLevelType { Zone, Shelf, Bin }

/// <summary>
/// موقع داخلي واحد داخل مخزن (منطقة/رف/موقع دقيق)، بعلاقة ذاتية تسمح
/// بأي عدد من مستويات التداخل: الفرع أعلاها هو Warehouse.BranchId نفسه.
/// </summary>
public class WarehouseLocation
{
    public int Id { get; set; }

    public int WarehouseId { get; set; }
    public Warehouse Warehouse { get; set; } = null!;

    public int? ParentLocationId { get; set; }
    public WarehouseLocation? ParentLocation { get; set; }

    public string LocationName { get; set; } = string.Empty;
    public LocationLevelType LevelType { get; set; }
}
