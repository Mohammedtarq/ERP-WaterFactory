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
    public decimal NetSalary { get; set; }
}
