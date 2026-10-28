namespace ERP.Data.ProjectDb.Entities;

public enum CustodyStatus { Active, Settled, Voided }

/// <summary>Issue = صرف أو تجديد (يزيد الرصيد)، Expense = مصروف من العهدة، Return = إعادة متبقٍّ للصندوق.</summary>
public enum CustodyTxKind { Issue, Expense, Return }

/// <summary>
/// عهدة نقدية لموظف (71_custody.sql): رصيدها = مجموع الصرف − المصروف − المُعاد (غير الملغى).
/// مسؤول المشتريات يسجّل مصاريفه من التطبيق، وللمندوب في الحالات الطارئة. لا تُحذف بل تُلغى.
/// </summary>
public class Custody
{
    public int Id { get; set; }
    public string CustodyNumber { get; set; } = "";
    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
    public string Purpose { get; set; } = "";
    public CustodyStatus Status { get; set; } = CustodyStatus.Active;
    public int? CashBoxId { get; set; }
    public CashBox? CashBox { get; set; }
    public DateTime IssuedAt { get; set; } = DateTime.Today;
    /// <summary>المبلغ المعتاد الذي يعيد التجديد العهدة إليه.</summary>
    public decimal? TargetAmount { get; set; }
    public DateTime? ClosedAt { get; set; }
    public int? ClosedByUserId { get; set; }
    public string? VoidReason { get; set; }
    public int? VoidedByUserId { get; set; }
    public DateTime? VoidedAt { get; set; }
    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<CustodyTransaction> Transactions { get; set; } = new();
}

public class CustodyTransaction
{
    public int Id { get; set; }
    public int CustodyId { get; set; }
    public Custody Custody { get; set; } = null!;
    public DateTime TxDate { get; set; } = DateTime.Today;
    public CustodyTxKind Kind { get; set; }
    public decimal Amount { get; set; }
    public decimal? OriginalAmount { get; set; }
    public int? CategoryId { get; set; }
    public FinanceCategory? Category { get; set; }
    public string? Description { get; set; }
    public string? ReceiptNumber { get; set; }
    public bool HasReceiptPhoto { get; set; }
    public int? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }
    public int? FinanceEntryId { get; set; }
    public FinanceEntry? FinanceEntry { get; set; }
    public int? CashBoxTxId { get; set; }
    public CashBoxTransaction? CashBoxTx { get; set; }
    /// <summary>رقم فريد من الهاتف فلا يتكرر المصروف عند إعادة الإرسال.</summary>
    public Guid? ClientId { get; set; }
    public bool IsVoided { get; set; }
    public string? VoidReason { get; set; }
    public int? VoidedByUserId { get; set; }
    public DateTime? VoidedAt { get; set; }
    public int? ModifiedByUserId { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
