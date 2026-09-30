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
}
