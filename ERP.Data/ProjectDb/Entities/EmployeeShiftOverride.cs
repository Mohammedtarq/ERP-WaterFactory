namespace ERP.Data.ProjectDb.Entities;

/// <summary>
/// استثناء مؤقت من شفت الموظف لمدة (63_employee_shift_overrides.sql): نقل لشفت آخر (مثل الصباحي ← المسائي أسبوعًا)،
/// أو وقت دخول خاص مع بقاء شفته (مثل 07:00 بدل 06:00 لظرف صحي)، أو الاثنان. بعد نهاية المدة يعود شفته الأصلي تلقائيًا.
/// </summary>
public class EmployeeShiftOverride
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
    public DateTime FromDate { get; set; } = DateTime.Today;
    public DateTime ToDate { get; set; } = DateTime.Today.AddDays(6);
    /// <summary>الشفت خلال المدة (فارغ = شفته نفسه).</summary>
    public int? ShiftId { get; set; }
    public Shift? Shift { get; set; }
    /// <summary>وقت دخول خاص خلال المدة (فارغ = وقت دخول الشفت).</summary>
    public TimeSpan? CheckInTime { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
