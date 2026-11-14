namespace ERP.Data.ProjectDb.Entities;

public enum WarehouseType
{
    Main,
    Sub,
    Returns,
    Damaged,
    UnderInspection,
    Transit,
    RepVan,
    RawMaterial,
    FinishedGoods,
    /// <summary>تحت التصنيع: مخزن داخلي لكل ماكينة (15_machines_wip.sql) — لا يظهر في قوائم المخازن العادية</summary>
    WorkInProcess
}
