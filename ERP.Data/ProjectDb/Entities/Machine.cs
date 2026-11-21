namespace ERP.Data.ProjectDb.Entities;

/// <summary>
/// ماكينة إنتاج (نفخ، تعبئة، تغليف...). لكل ماكينة مخزن داخلي "تحت التصنيع" يحمل رصيد المواد
/// المصروفة لها ولم تُستهلك بعد — يُرحَّل من فترة لأخرى (15_machines_wip.sql).
/// </summary>
public class Machine
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string MachineType { get; set; } = string.Empty;
    public string? ProductionLine { get; set; }

    public int WipWarehouseId { get; set; }
    public Warehouse WipWarehouse { get; set; } = null!;

    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
}
