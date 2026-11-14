namespace ERP.Data.ProjectDb.Entities;

/// <summary>درجة العلاوة السنوية (A / B / C …) بمبلغها — تحددها الإدارة (60_raise_grades.sql).</summary>
public class RaiseGrade
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public bool IsActive { get; set; } = true;
}
