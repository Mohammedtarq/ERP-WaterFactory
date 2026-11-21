namespace ERP.Data.ProjectDb.Entities;

/// <summary>سطر في سجل الحركات العام: من فعل ماذا ومتى، والقيم قبل وبعد (27_controls.sql).</summary>
public class AuditLog
{
    public long Id { get; set; }
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    public int? UserId { get; set; }
    public string Action { get; set; } = "";
    public string TableName { get; set; } = "";
    public string? RecordId { get; set; }
    public string? Summary { get; set; }
    /// <summary>JSON: {"الحقل": ["قبل", "بعد"]}.</summary>
    public string? Changes { get; set; }
}

/// <summary>إغلاق أو فتح فترة محاسبية. القفل الساري = آخر صف (27_controls.sql).</summary>
public class PeriodLock
{
    public int Id { get; set; }
    /// <summary>آخر يوم مقفل؛ null = لا قفل.</summary>
    public DateTime? LockedThrough { get; set; }
    public string Action { get; set; } = "Close";
    public string? Reason { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
}
