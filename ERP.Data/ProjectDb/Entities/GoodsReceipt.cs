namespace ERP.Data.ProjectDb.Entities;

public enum DocumentStatus { Draft, Posted, Voided }

public class GoodsReceipt
{
    public int Id { get; set; }
    public string ReceiptNumber { get; set; } = string.Empty;

    public int? PurchaseOrderId { get; set; }
    public PurchaseOrder? PurchaseOrder { get; set; }

    public int SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;

    public int WarehouseId { get; set; }
    public Warehouse Warehouse { get; set; } = null!;

    public DateTime ReceiptDate { get; set; }
    public string? SupplierInvoiceNumber { get; set; }

    public int? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }

    public DocumentStatus Status { get; set; } = DocumentStatus.Draft;

    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;

    public ICollection<GoodsReceiptLine> Lines { get; set; } = new List<GoodsReceiptLine>();
}

public class GoodsReceiptLine
{
    public int Id { get; set; }

    public int GoodsReceiptId { get; set; }
    public GoodsReceipt GoodsReceipt { get; set; } = null!;

    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;

    public int? PurchaseOrderLineId { get; set; }
    public PurchaseOrderLine? PurchaseOrderLine { get; set; }

    public decimal QuantityReceived { get; set; }
    public decimal UnitCost { get; set; }
    /// <summary>كما في فاتورة المورد: الوحدة (كرتون، باليت، طن...) والكمية وسعر الوحدة.</summary>
    public string? PurchaseUnit { get; set; }
    public decimal? PurchaseQuantity { get; set; }
    public decimal? PurchaseUnitPrice { get; set; }

    public int BatchId { get; set; }
    public ItemBatch Batch { get; set; } = null!;
}
