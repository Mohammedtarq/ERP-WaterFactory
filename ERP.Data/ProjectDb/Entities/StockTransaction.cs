namespace ERP.Data.ProjectDb.Entities;

public enum StockTransactionType
{
    Receipt, SalesIssue, ProductionConsume, ProductionOutput, Packing, Transfer,
    Damaged, FreeIssue, ReturnToWarehouse,
    RepLoad, RepSale, RepFreeSale, RepDamaged, RepReturn,
    SyncConflictAdjustment,
    /// <summary>إخراج مخزني: صرف لجهة أو غرض من واجهة المخزن (12_warehouse_docs_cashboxes.sql)</summary>
    Issue,
    /// <summary>صرف مواد لأمر إنتاج إلى تحت تصنيع الماكينة، وإرجاع المتبقي منه (15_machines_wip.sql)</summary>
    WipIssue, WipReturn,
    /// <summary>تعديل مشرف لرصيد تحت التصنيع بعد الجرد (19_wip_adjustments.sql)</summary>
    WipAdjust,
    /// <summary>عكس حركة فاتورة مبيعات ملغاة، وفرق الجرد الفعلي (27_controls.sql)</summary>
    SalesVoid, StocktakeVariance,
    /// <summary>زبون أعاد بضاعة اشتراها: السليم للمخزن/السيارة، والتالف لمخزن التالف (39_customer_returns.sql)</summary>
    CustomerReturn
}

/// <summary>Field = تلف ميداني (عند المندوب) — 21_rep_documents.sql</summary>
public enum DamageReason { Transit, Warehouse, Production, Field }

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
    /// <summary>كلفة القطعة لحظة الحركة: الوارد المسعَّر يحملها، وغيره يأخذ المتوسط المرجّح الساري (trg_StockTransactions_Cost).</summary>
    public decimal? UnitCost { get; set; }
    public StockTransactionType TransactionType { get; set; }
    public DamageReason? DamageReason { get; set; }
    public string? FreeIssueRecipient { get; set; }

    public string? ReferenceTable { get; set; }
    public int? ReferenceId { get; set; }

    public DateTime TransactionDate { get; set; } = DateTime.UtcNow;

    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
}
