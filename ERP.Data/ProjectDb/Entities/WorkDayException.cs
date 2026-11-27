namespace ERP.Data.ProjectDb.Entities;

public class WorkDayException
{
    public int Id { get; set; }

    public int? DepartmentId { get; set; }   // NULL = كل الأقسام
    public Department? Department { get; set; }

    public string ExceptionDay { get; set; } = "Friday";
    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }   // NULL = بلا نهاية
    public bool IsActive { get; set; } = true;
}
