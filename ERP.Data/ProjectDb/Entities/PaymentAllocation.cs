namespace ERP.Data.ProjectDb.Entities;

/// <summary>جزء من سند قبض مخصّص لفاتورة: تلقائي (الأقدم أولًا) أو يدوي (20_payment_allocations.sql).</summary>
public class PaymentAllocation
{
    public int Id { get; set; }
    public int VoucherId { get; set; }
    public Voucher Voucher { get; set; } = null!;
    public int SalesInvoiceId { get; set; }
    public SalesInvoice SalesInvoice { get; set; } = null!;
    public decimal Amount { get; set; }
    public bool IsManual { get; set; }
    public int? AllocatedByUserId { get; set; }
    public User? AllocatedByUser { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
