namespace ERP.Data.ProjectDb.Entities;

/// <summary>فترة رصيد نوع الإجازة: شهري (يسقط بنهاية الشهر) أو سنوي.</summary>
public enum LeaveAllowancePeriod { Month, Year }

/// <summary>يومية (من تاريخ إلى تاريخ) أو زمنية (من ساعة إلى ساعة في يوم واحد).</summary>
public enum LeaveKind { Day, Hourly }

/// <summary>أيام إضافية (بالأجر اليومي) أو ساعات إضافية (بسعر الساعة).</summary>
public enum OvertimeKind { Day, Hours }

/// <summary>نوع إجازة بأيامه المسموحة (44_hr_leaves_overtime.sql). AllowedDays = null بلا حد.</summary>
public class LeaveType
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal? AllowedDays { get; set; }
    public LeaveAllowancePeriod AllowancePeriod { get; set; } = LeaveAllowancePeriod.Year;
    public bool IsPaid { get; set; } = true;
    /// <summary>الاعتيادية: تُخصم منها الزمنية بعد المسموح شهريًا.</summary>
    public bool IsRegular { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

/// <summary>إجازة موظف داخل شهر واحد (اليومية التي تعبر شهرين تُقسم سجلين). المدفوع من الرصيد، والباقي بدون راتب.</summary>
public class EmployeeLeave
{
    public int Id { get; set; }
    public string LeaveNumber { get; set; } = "";
    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
    public int LeaveTypeId { get; set; }
    public LeaveType LeaveType { get; set; } = null!;
    public LeaveKind Kind { get; set; }
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public TimeSpan? FromTime { get; set; }
    public TimeSpan? ToTime { get; set; }
    public decimal WorkDays { get; set; }
    public decimal PaidDays { get; set; }
    public decimal UnpaidDays { get; set; }
    public string? Note { get; set; }
    public bool IsVoided { get; set; }
    public string? VoidReason { get; set; }
    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>عطلة رسمية: لا يُسجَّل فيها غياب من البصمة.</summary>
public class PublicHoliday
{
    public int Id { get; set; }
    public DateTime HolidayDate { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>عمل إضافي يُدخَل يدويًا: أيام (الجمعة/العطل) أو ساعات.</summary>
public class OvertimeEntry
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
    public DateTime WorkDate { get; set; }
    public OvertimeKind Kind { get; set; }
    public decimal Quantity { get; set; }
    public string? Note { get; set; }
    public bool IsVoided { get; set; }
    public string? VoidReason { get; set; }
    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>سياسة الموارد البشرية (صف واحد): سعر الساعة الإضافية العام، والزمنيات المسموحة في الشهر.</summary>
public class HrPolicy
{
    public int Id { get; set; } = 1;
    public decimal OvertimeHourlyRate { get; set; }
    public int FreeHourlyLeavesPerMonth { get; set; } = 2;
}
