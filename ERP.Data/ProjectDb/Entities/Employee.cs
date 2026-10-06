namespace ERP.Data.ProjectDb.Entities;

public class Employee
{
    public int Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? JobTitle { get; set; }

    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }

    public int? ShiftId { get; set; }
    public Shift? Shift { get; set; }

    public int? BranchId { get; set; }
    public Branch? Branch { get; set; }

    public SalaryCurrency SalaryCurrency { get; set; } = SalaryCurrency.IQD;
    public decimal BaseSalary { get; set; }

    public bool IsSalesRep { get; set; }
    public bool IsSalesManager { get; set; }

    public DateTime? HireDate { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>عامل وقتي: أجر يومي، بلا بصمة ولا سلف، يُصرف أسبوعيًا أو عند إنهاء الخدمة (32_temp_workers.sql).</summary>
    public bool IsTemporary { get; set; }
    public decimal? DailyWage { get; set; }
    /// <summary>معفى من البصمة بقرار الإدارة: لا يُخصم غيابه من الراتب.</summary>
    public bool AttendanceExempt { get; set; }
    public DateTime? EndOfServiceDate { get; set; }

    /// <summary>رقم الموظف في جهاز البصمة (ZKTeco) لربط ملف الحضور ببطاقته (33_fingerprint.sql).</summary>
    public string? FingerprintCode { get; set; }

    /// <summary>ضمان اجتماعي: مبلغ ثابت (بعملة الراتب) يُستقطع شهريًا عند التفعيل (35_social_security.sql).</summary>
    public bool HasSocialSecurity { get; set; }
    public decimal? SocialSecurityAmount { get; set; }
    /// <summary>تكافل اجتماعي: مبلغ ثابت يُستقطع شهريًا عند التفعيل.</summary>
    public bool HasSocialSolidarity { get; set; }
    public decimal? SocialSolidarityAmount { get; set; }
}
