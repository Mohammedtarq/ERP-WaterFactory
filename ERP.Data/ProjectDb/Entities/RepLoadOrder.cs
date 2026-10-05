namespace ERP.Data.ProjectDb.Entities;

/// <summary>الحمولة الافتراضية لمندوب (30_rep_loads_settlement.sql): تُقترح تلقائيًا في طلب التحميل اليومي.</summary>
public class RepDefaultLoad
{
    public int Id { get; set; }
    public int RepEmployeeId { get; set; }
    public Employee RepEmployee { get; set; } = null!;
    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;
    public int PackagingLevelId { get; set; }
    public ItemPackagingLevel PackagingLevel { get; set; } = null!;
    public decimal QuantityInLevel { get; set; }
}

public enum RepLoadOrderStatus { Pending, Prepared, Cancelled }

/// <summary>طلب تحميل: يطلبه مدير المبيعات، ويجهّزه أمين المخزن فيتحرك المخزون بمستند إسناد مرقّم.</summary>
public class RepLoadOrder
{
    public int Id { get; set; }
    public string OrderNumber { get; set; } = "";
    public int RepEmployeeId { get; set; }
    public Employee RepEmployee { get; set; } = null!;
    public int VanWarehouseId { get; set; }
    public Warehouse VanWarehouse { get; set; } = null!;
    public int FromWarehouseId { get; set; }
    public Warehouse FromWarehouse { get; set; } = null!;
    public DateTime LoadDate { get; set; } = DateTime.Today;
    public RepLoadOrderStatus Status { get; set; } = RepLoadOrderStatus.Pending;
    public string? Notes { get; set; }
    public int RequestedByUserId { get; set; }
    public User RequestedByUser { get; set; } = null!;
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public int? PreparedByUserId { get; set; }
    public User? PreparedByUser { get; set; }
    public DateTime? PreparedAt { get; set; }
    public int? StockDocumentId { get; set; }
    public StockDocument? StockDocument { get; set; }
    public string? CancelReason { get; set; }
    public List<RepLoadOrderLine> Lines { get; set; } = new();
}

public class RepLoadOrderLine
{
    public int Id { get; set; }
    public int RepLoadOrderId { get; set; }
    public RepLoadOrder RepLoadOrder { get; set; } = null!;
    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;
    public int PackagingLevelId { get; set; }
    public ItemPackagingLevel PackagingLevel { get; set; } = null!;
    public decimal QuantityInLevel { get; set; }
    /// <summary>المتغير المطلوب تحميله (مطعم، مناسبة). NULL = الأساسي.</summary>
    public int? CustomRecipeId { get; set; }
    public CustomRecipe? CustomRecipe { get; set; }
    /// <summary>ما جهّزه أمين المخزن فعلًا (قد يقل عن المطلوب إن نقص الرصيد).</summary>
    public decimal? PreparedQuantity { get; set; }
}

/// <summary>تسوية المندوب اليومية مع أمين الصندوق.</summary>
public class RepSettlement
{
    public int Id { get; set; }
    public string SettlementNumber { get; set; } = "";
    public int RepEmployeeId { get; set; }
    public Employee RepEmployee { get; set; } = null!;
    public int VanWarehouseId { get; set; }
    public Warehouse VanWarehouse { get; set; } = null!;
    public DateTime SettlementDate { get; set; } = DateTime.Today;
    public int? ReturnDocumentId { get; set; }
    public StockDocument? ReturnDocument { get; set; }
    public int? AutoInvoiceId { get; set; }
    public SalesInvoice? AutoInvoice { get; set; }
    public decimal ReturnedPieces { get; set; }
    public decimal FreePieces { get; set; }
    public decimal FreeCost { get; set; }
    public decimal FieldExpenses { get; set; }
    public decimal ExpectedCash { get; set; }
    public decimal ReceivedCash { get; set; }
    /// <summary>موجب = عجز يبقى في ذمة المندوب (في محفظته).</summary>
    public decimal Difference { get; set; }
    public string? Notes { get; set; }
    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<RepFreeGood> FreeGoods { get; set; } = new();
}

/// <summary>"مجاني المندوب": ما أعطاه المندوب بلا ثمن، لمن ولماذا، بالكلفة.</summary>
public class RepFreeGood
{
    public int Id { get; set; }
    public int RepSettlementId { get; set; }
    public RepSettlement RepSettlement { get; set; } = null!;
    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;
    public decimal QuantityBaseUnits { get; set; }
    public decimal? UnitCost { get; set; }
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public string Reason { get; set; } = "";
}
