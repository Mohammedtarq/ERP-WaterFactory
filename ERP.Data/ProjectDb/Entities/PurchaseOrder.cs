namespace ERP.Data.ProjectDb.Entities;

public enum PurchaseOrderStatus { Draft, Sent, PartiallyReceived, Completed, Cancelled }

public class PurchaseOrder
{
    public int Id { get; set; }
    public string PONumber { get; set; } = string.Empty;

    public int SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;

    public int WarehouseId { get; set; }
    public Warehouse Warehouse { get; set; } = null!;

    public DateTime OrderDate { get; set; }
    public DateTime? ExpectedDeliveryDate { get; set; }

    public PurchaseOrderStatus Status { get; set; } = PurchaseOrderStatus.Draft;
    public SupplierPaymentTerms PaymentTerms { get; set; } = SupplierPaymentTerms.Credit;

    public decimal AdvanceAmount { get; set; }
    public int? AdvanceVoucherId { get; set; }
    public Voucher? AdvanceVoucher { get; set; }

    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;

    public ICollection<PurchaseOrderLine> Lines { get; set; } = new List<PurchaseOrderLine>();
}

public class PurchaseOrderLine
{
    public int Id { get; set; }

    public int PurchaseOrderId { get; set; }
    public PurchaseOrder PurchaseOrder { get; set; } = null!;

    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;

    public decimal QuantityOrdered { get; set; }
    public decimal ExpectedUnitCost { get; set; }
    public decimal QuantityReceived { get; set; }   // يتراكم مع كل استلام جزئي
}
