namespace ERP.Data.ProjectDb.Entities;

public enum StockTransactionType
{
    Receipt, SalesIssue, ProductionConsume, ProductionOutput, Packing, Transfer,
    Damaged, FreeIssue, ReturnToWarehouse,
    RepLoad, RepSale, RepFreeSale, RepDamaged, RepReturn,
    SyncConflictAdjustment,
    /// <summary>إخراج مخزني: صرف لجهة أو غرض من واجهة المخزن (12_warehouse_docs_cashboxes.sql)</summary>
    Issue
}

public enum DamageReason { Transit, Warehouse, Production }

/// <summary>
/// سجل حركة واحد. الرصيد الحالي لأي صنف/مخزن/تشغيلة = مجموع QuantityBaseUnits
/// من كل السطور المطابقة (موجب = وارد، سالب = صادر) — لا يوجد عمود رصيد مباشر.
/// </summary>
public class StockTransaction
{
    public int Id { get; set; }

    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;

    public int WarehouseId { get; set; }
    public Warehouse Warehouse { get; set; } = null!;

    public int? LocationId { get; set; }
    public WarehouseLocation? Location { get; set; }

    public int? BatchId { get; set; }
    public ItemBatch? Batch { get; set; }

    public decimal QuantityBaseUnits { get; set; }
    public StockTransactionType TransactionType { get; set; }
    public DamageReason? DamageReason { get; set; }
    public string? FreeIssueRecipient { get; set; }

    public string? ReferenceTable { get; set; }
    public int? ReferenceId { get; set; }

    public DateTime TransactionDate { get; set; } = DateTime.UtcNow;

    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
}
