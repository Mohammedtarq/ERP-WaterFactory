namespace ERP.Data.ProjectDb.Entities;

public enum PayrollRunStatus { Draft, Approved }

public class PayrollRun
{
    public int Id { get; set; }
    public int PeriodMonth { get; set; }
    public int PeriodYear { get; set; }
    public PayrollRunStatus Status { get; set; } = PayrollRunStatus.Draft;

    public int? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }

    public int? ApprovedByUserId { get; set; }
    public User? ApprovedByUser { get; set; }

    public ICollection<PayrollLine> Lines { get; set; } = new List<PayrollLine>();
}

public class PayrollLine
{
    public int Id { get; set; }

    public int PayrollRunId { get; set; }
    public PayrollRun PayrollRun { get; set; } = null!;

    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public string Currency { get; set; } = "IQD";
    public decimal BaseSalary { get; set; }
    public decimal AbsenceDeduction { get; set; }
    public decimal Allowances { get; set; }
    public decimal RepIncentiveAmount { get; set; }
    public decimal SalesManagerIncentiveAmount { get; set; }
    public decimal MonthlyIncentiveAmount { get; set; }
    /// <summary>استقطاعات الشهر بعملة الراتب: قسط السلفة، المسحوبات، العقوبات.</summary>
    public decimal LoanDeduction { get; set; }
    public decimal WithdrawalDeduction { get; set; }
    public decimal PenaltyDeduction { get; set; }
    public decimal SocialSecurityDeduction { get; set; }
    public decimal SocialSolidarityDeduction { get; set; }
    /// <summary>العمل الإضافي: أيام بالأجر اليومي + ساعات بسعر الساعة (بعملة الراتب).</summary>
    public decimal OvertimeDays { get; set; }
    public decimal OvertimeHours { get; set; }
    public decimal OvertimeAmount { get; set; }
    /// <summary>أيام إجازة بدون راتب (أو تجاوز الرصيد) وخصمها بالأجر اليومي.</summary>
    public decimal UnpaidLeaveDays { get; set; }
    public decimal UnpaidLeaveDeduction { get; set; }
    public decimal NetSalary { get; set; }
}
