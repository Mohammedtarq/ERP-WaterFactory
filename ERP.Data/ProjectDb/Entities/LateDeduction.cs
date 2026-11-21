namespace ERP.Data.ProjectDb.Entities;

public enum LateDeductionKind { Fraction, Fixed, None }

/// <summary>شريحة خصم تأخير: من/إلى (دقائق التأخير في اليوم الواحد بعد المسامحة)، وقيمتها جزء من أجر اليوم أو مبلغ ثابت.</summary>
public class LateDeductionTier
{
    public int Id { get; set; }
    public int FromMinutes { get; set; }
    public int? ToMinutes { get; set; }
    public LateDeductionKind Kind { get; set; } = LateDeductionKind.Fraction;
    public decimal Value { get; set; }
}

/// <summary>رصيد دقائق مسامحة شهري لموظف: يُسقَط من دقائق تأخيره بترتيب الأيام قبل حساب الخصم.</summary>
public class LateMonthlyForgiveness
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
    public int PeriodMonth { get; set; }
    public int PeriodYear { get; set; }
    public int Minutes { get; set; }
    public string Reason { get; set; } = "";
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
