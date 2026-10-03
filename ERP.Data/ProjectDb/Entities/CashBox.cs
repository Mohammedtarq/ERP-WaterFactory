namespace ERP.Data.ProjectDb.Entities;

public enum CashBoxType { Main, User }

public enum CashBoxTxType
{
    Opening, Deposit, Withdrawal, TransferIn, TransferOut,
    SalesReceipt, VoucherReceipt, VoucherPayment, RepHandover,
    CustomerDepositIn, CustomerDepositOut
}

/// <summary>صندوق مالي: رئيسي أو خاص بمستخدم. الرصيد = مجموع حركاته غير الملغاة.</summary>
public class CashBox
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public CashBoxType BoxType { get; set; } = CashBoxType.Main;
    public int? OwnerUserId { get; set; }
    public User? OwnerUser { get; set; }
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class CashBoxTransaction
{
    public int Id { get; set; }
    public string TxNumber { get; set; } = "";
    public int CashBoxId { get; set; }
    public CashBox CashBox { get; set; } = null!;
    public DateTime TxDate { get; set; } = DateTime.Today;
    public CashBoxTxType TxType { get; set; }
    /// <summary>موجب = داخل للصندوق، سالب = خارج.</summary>
    public decimal Amount { get; set; }
    public int? CounterCashBoxId { get; set; }
    public CashBox? CounterCashBox { get; set; }
    public Guid? TransferGroup { get; set; }
    public string? PartyName { get; set; }
    public string? Description { get; set; }
    public string? ReferenceTable { get; set; }
    public int? ReferenceId { get; set; }
    public int? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }
    public bool IsVoided { get; set; }
    public string? VoidReason { get; set; }
    public decimal? OriginalAmount { get; set; }
    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int? ModifiedByUserId { get; set; }
    public User? ModifiedByUser { get; set; }
    public DateTime? ModifiedAt { get; set; }
}
