namespace ERP.Data.ProjectDb.Entities;

/// <summary>سلفة (أقساط شهرية)، مسحوب (يُستقطع كاملًا آخر الشهر)، عقوبة (خصم بلا صرف نقدي).</summary>
public enum EmployeeDeductionKind { Loan, Withdrawal, Penalty }

/// <summary>
/// استقطاع من راتب موظف. السلفة والمسحوب يُصرفان نقدًا من الصندوق (قيد على "سلف ومسحوبات الموظفين")،
/// والعقوبة تُخصم فقط. يبدأ الاستقطاع من شهر البداية، ويُسجَّل كل خصم فعلي قسطًا في دورة الرواتب.
/// </summary>
public class EmployeeDeduction
{
    public int Id { get; set; }
    public string DeductionNumber { get; set; } = "";
    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
    public EmployeeDeductionKind Kind { get; set; }
    public DateTime EntryDate { get; set; } = DateTime.Today;
    /// <summary>بالدينار دائمًا.</summary>
    public decimal Amount { get; set; }
    /// <summary>للسلفة فقط: مقدار القسط الشهري بالدينار.</summary>
    public decimal? MonthlyInstallment { get; set; }
    public int StartMonth { get; set; }
    public int StartYear { get; set; }
    /// <summary>رصيد منقول من نظام سابق: قيد فقط دون حركة صندوق.</summary>
    public bool IsOpening { get; set; }
    public string? Reason { get; set; }
    public int? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }
    public bool IsVoided { get; set; }
    public string? VoidReason { get; set; }
    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<EmployeeDeductionInstallment> Installments { get; set; } = new List<EmployeeDeductionInstallment>();

    /// <summary>هل تخرج نقدًا من الصندوق (السلفة والمسحوب)؟</summary>
    public bool PaysCash => Kind != EmployeeDeductionKind.Penalty;
}

/// <summary>خصم فعلي من راتب شهر معيّن (بالدينار).</summary>
public class EmployeeDeductionInstallment
{
    public int Id { get; set; }
    public int EmployeeDeductionId { get; set; }
    public EmployeeDeduction EmployeeDeduction { get; set; } = null!;
    public int PayrollRunId { get; set; }
    public PayrollRun PayrollRun { get; set; } = null!;
    public decimal Amount { get; set; }
}
