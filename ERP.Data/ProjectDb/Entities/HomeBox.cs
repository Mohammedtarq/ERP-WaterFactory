namespace ERP.Data.ProjectDb.Entities;

/// <summary>نوع الصرف من صندوق المنزل خارج العمل.</summary>
public enum HomeBoxEntryKind { Khums, Aid, Asset, Other }

/// <summary>
/// صرف من صندوق المنزل (74_home_box.sql): الخمس، مساعدات وتبرعات، شراء ماكنات وأصول، أو غيرها.
/// ربح متحقق يُصرف خارج العمل: لا يدخل كلفة القنينة ولا صافي ربح الشهر، ولا يمس رأس المال التشغيلي.
/// </summary>
public class HomeBoxEntry
{
    public int Id { get; set; }
    public string EntryNumber { get; set; } = "";
    public DateTime EntryDate { get; set; } = DateTime.Today;
    public HomeBoxEntryKind Kind { get; set; }
    public decimal Amount { get; set; }
    public string? Title { get; set; }
    public string? Beneficiary { get; set; }
    public string? Notes { get; set; }
    public int CashBoxId { get; set; }
    public CashBox CashBox { get; set; } = null!;
    public int? CashBoxTxId { get; set; }
    public CashBoxTransaction? CashBoxTx { get; set; }
    public int? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }
    public bool IsVoided { get; set; }
    public string? VoidReason { get; set; }
    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum HomeLoanStatus { Open, Settled, Voided }

/// <summary>قرض من صندوق المنزل إلى صندوق عمل نفدت سيولته، يُسدَّد عند توفر الوفرة. لا يؤثر على الأرباح.</summary>
public class HomeLoan
{
    public int Id { get; set; }
    public string LoanNumber { get; set; } = "";
    public int ToCashBoxId { get; set; }
    public CashBox ToCashBox { get; set; } = null!;
    public int HomeCashBoxId { get; set; }
    public CashBox HomeCashBox { get; set; } = null!;
    public decimal Amount { get; set; }
    public DateTime LoanDate { get; set; } = DateTime.Today;
    public string Reason { get; set; } = "";
    public HomeLoanStatus Status { get; set; } = HomeLoanStatus.Open;
    public Guid? TransferGroup { get; set; }
    public DateTime? SettledAt { get; set; }
    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<HomeLoanRepayment> Repayments { get; set; } = new List<HomeLoanRepayment>();
}

public class HomeLoanRepayment
{
    public int Id { get; set; }
    public int LoanId { get; set; }
    public HomeLoan Loan { get; set; } = null!;
    public DateTime RepayDate { get; set; } = DateTime.Today;
    public decimal Amount { get; set; }
    public int FromCashBoxId { get; set; }
    public CashBox FromCashBox { get; set; } = null!;
    public Guid? TransferGroup { get; set; }
    public string? Notes { get; set; }
    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>إعدادات مالية عامة (سجل واحد Id = 1) — 75_cash_adjust_limit.sql.</summary>
public class FinanceSetting
{
    public int Id { get; set; } = 1;
    /// <summary>فرق تسوية الجرد الذي يسوّيه صاحب الصلاحية منفردًا؛ ما فوقه يحتاج اعتماد المالية. 0 = بلا موافقة ثانية.</summary>
    public decimal CashAdjustLimit { get; set; } = DefaultCashAdjustLimit;
    public const decimal DefaultCashAdjustLimit = 5_000_000m;
    public int? UpdatedByUserId { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
