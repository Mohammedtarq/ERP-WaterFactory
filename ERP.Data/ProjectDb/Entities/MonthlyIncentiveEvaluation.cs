namespace ERP.Data.ProjectDb.Entities;

/// <summary>صف إعداد واحد (وزن كل عامل)، قابل للتعديل من الإعدادات دون كود.</summary>
public class IncentiveScoreWeights
{
    public int Id { get; set; }
    public decimal AttendanceWeight { get; set; } = 40;
    public decimal PerformanceWeight { get; set; } = 30;
    public decimal SkillsWeight { get; set; } = 30;
}

/// <summary>مقياس تحويل مجموع النقاط لمبلغ مالي، بشرائح قابلة للتعديل.</summary>
public class IncentiveScoreToAmountScale
{
    public int Id { get; set; }
    public decimal MinScore { get; set; }
    public decimal MaxScore { get; set; }
    public decimal Amount { get; set; }
}

public class MonthlyIncentiveEvaluation
{
    public int Id { get; set; }

    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public int PeriodMonth { get; set; }
    public int PeriodYear { get; set; }

    public decimal AttendanceScoreAuto { get; set; }       // محسوبة تلقائيًا من AttendanceRecords
    public decimal PerformanceScoreManual { get; set; }    // يُدخلها المدير
    public decimal SkillsScoreManual { get; set; }         // يُدخلها المدير
    public decimal TotalScore { get; set; }
    public decimal IncentiveAmount { get; set; }
}
