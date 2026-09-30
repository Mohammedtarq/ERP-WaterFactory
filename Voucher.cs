namespace ERP.Data.ProjectDb.Entities;

public enum VoucherType { Receipt, Payment }
public enum VoucherPartyType { Customer, Supplier, Employee, Other }
public enum PaymentMethod { Cash, Bank, Cheque }

public class Voucher
{
    public int Id { get; set; }
    public string VoucherNumber { get; set; } = string.Empty;
    public VoucherType VoucherType { get; set; }
    public VoucherPartyType PartyType { get; set; }
    public int? PartyId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "IQD";
    public PaymentMethod PaymentMethod { get; set; }
    public DateTime VoucherDate { get; set; }
    public string? Notes { get; set; }

    public int? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }

    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// "العقل المالي": يحدد الحسابين الافتراضيين (مدين/دائن) لكل نوع عملية، بحيث
/// يُنشئ السند القيد المحاسبي المقابل تلقائيًا دون تدخل يدوي من غير المحاسب.
/// </summary>
public class AccountMappingRule
{
    public int Id { get; set; }
    public string TransactionType { get; set; } = string.Empty;  // مثال: "CashReceiptVoucher"

    public int DebitAccountId { get; set; }
    public ChartOfAccount DebitAccount { get; set; } = null!;

    public int CreditAccountId { get; set; }
    public ChartOfAccount CreditAccount { get; set; } = null!;
}
