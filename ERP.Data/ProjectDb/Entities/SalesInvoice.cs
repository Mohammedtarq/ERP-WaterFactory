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

    // المجاميع تُثبَّت لحظة الترحيل (sp_Sales_PostInvoice) — قبلها تُعرض من vw_SalesInvoiceTotals
    public decimal SubTotal { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal TotalAmount { get; set; }
    /// <summary>المدفوع التراكمي (المدفوع عند البيع + ما وُزّع عليها من سندات القبض) — 20_payment_allocations.sql.</summary>
    public decimal AmountSettled { get; set; }
    /// <summary>المتبقي = الإجمالي − المدفوع (عمود محسوب في قاعدة البيانات).</summary>
    public decimal AmountRemaining { get; private set; }
    public string? Notes { get; set; }
    /// <summary>رصيد افتتاحي منقول من نظام سابق: بلا سطور، يدخل كشف العميل وتوزيع الدفعات، ويُستبعد من إحصاءات المبيعات.</summary>
    public bool IsOpeningBalance { get; set; }

    /// <summary>إلغاء الفاتورة المرحّلة بدل حذفها (sp_Sales_VoidInvoice).</summary>
    public string? VoidReason { get; set; }
    public int? VoidedByUserId { get; set; }
    public DateTime? VoidedAt { get; set; }

    public int? PostedByUserId { get; set; }
    public User? PostedByUser { get; set; }
    public DateTime? PostedAt { get; set; }

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

    /// <summary>متغير مطلوب بالاسم (مطعم، مناسبة). NULL = الأساسي ومحجوز العميل نفسه.</summary>
    public int? CustomRecipeId { get; set; }
    public CustomRecipe? CustomRecipe { get; set; }

    public int PackagingLevelId { get; set; }
    public ItemPackagingLevel PackagingLevel { get; set; } = null!;

    public decimal QuantityInLevel { get; set; }       // بوحدة البيع المختارة (كارتون/شرنك/قطعة)
    public decimal QuantityBaseUnits { get; set; }      // محسوبة تلقائيًا بالقطعة لخصم المخزون
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
    /// <summary>سعر القائمة لحظة البيع (لإظهار خصم الوكلاء في الحسابات الختامية): (القائمة − السعر) × الكمية.</summary>
    public decimal? ListUnitPrice { get; set; }
}
