namespace ERP.Data.ProjectDb.Entities;

public class Warehouse
{
    public int Id { get; set; }

    public int BranchId { get; set; }
    public Branch Branch { get; set; } = null!;

    public string Name { get; set; } = string.Empty;
    public WarehouseType WarehouseType { get; set; }
    public bool IsSellableStock { get; set; } = true;

    public int? OwnerEmployeeId { get; set; }
    public Employee? OwnerEmployee { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>الوحدة التي تظهر فيها مساحة المخزن: فارغ = المخازن، أو «Production» لتظهر في قسم الإنتاج (80_warehouse_show_in_module.sql).</summary>
    public string ShowInModule { get; set; } = "";
}
