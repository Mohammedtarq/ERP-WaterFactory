namespace ERP.Data.ProjectDb.Entities;

/// <summary>يوم عمل لعامل وقتي (كشف يدوي، بلا بصمة).</summary>
public class TempWorkDay
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
    public DateTime WorkDate { get; set; }
    /// <summary>1 = يوم، 0.5 = نصف يوم، 1.5 = يوم ونصف (إضافي).</summary>
    public decimal Days { get; set; } = 1;
    public string? Notes { get; set; }
    /// <summary>NULL = لم يُصرف بعد.</summary>
    public int? PaymentId { get; set; }
    public TempWorkerPayment? Payment { get; set; }
    public int CreatedByUserId { get; set; }
}

/// <summary>صرف أجر عامل وقتي عن أيام لم تُصرف (أسبوعيًا، أو تسوية نهاية الخدمة).</summary>
public class TempWorkerPayment
{
    public int Id { get; set; }
    public string PaymentNumber { get; set; } = "";
    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
    public DateTime PaidDate { get; set; } = DateTime.Today;
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public decimal Days { get; set; }
    public decimal DailyWage { get; set; }
    public decimal Amount { get; set; }
    public bool IsFinal { get; set; }
    public int? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }
    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
