namespace ERP.Data.ProjectDb.Entities;

public enum InvoicePaymentMethod { Cash, Credit, Partial, Electronic }

public class SalesInvoice
{
    public int Id { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;

    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    public int WarehouseId { get; set; }
    public Warehouse Warehouse { get; set; } = null!;

    public DateTime InvoiceDate { get; set; }
    public InvoicePaymentMethod PaymentMethod { get; set; }
    public decimal AmountPaidNow { get; set; }

    public bool TaxEnabled { get; set; }
    public decimal TaxRate { get; set; } = 14;

    public bool LoadingSuppliesEnabled { get; set; }
    public decimal LoadingSuppliesAmount { get; set; }

    public bool IsAgentPricing { get; set; }

    public bool IsFreeSale { get; set; }
    public string? FreeSaleRecipient { get; set; }   // إلزامي عند IsFreeSale = true (يُتحقّق منه في الخدمة، لا في الكيان)

    public int? SalesRepEmployeeId { get; set; }      // عند الصدور من كاش فان مندوب
    public Employee? SalesRepEmployee { get; set; }

    public DocumentStatus Status { get; set; } = DocumentStatus.Draft;

    public int? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }

    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;

    public ICollection<SalesInvoiceLine> Lines { get; set; } = new List<SalesInvoiceLine>();
}

public class SalesInvoiceLine
{
    public int Id { get; set; }

    public int SalesInvoiceId { get; set; }
    public SalesInvoice SalesInvoice { get; set; } = null!;

    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;

    public int? BatchId { get; set; }                 // اختيار حر، توصية FIFO تُطبَّق في الواجهة فقط
    public ItemBatch? Batch { get; set; }

    public int PackagingLevelId { get; set; }
    public ItemPackagingLevel PackagingLevel { get; set; } = null!;

    public decimal QuantityInLevel { get; set; }       // بوحدة البيع المختارة (كارتون/شرنك/قطعة)
    public decimal QuantityBaseUnits { get; set; }      // محسوبة تلقائيًا بالقطعة لخصم المخزون
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
}
