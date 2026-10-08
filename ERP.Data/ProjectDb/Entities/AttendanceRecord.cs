namespace ERP.Data.ProjectDb.Entities;

public enum AttendanceStatus { Present, Late, Absent, ApprovedLeave }

public class AttendanceRecord
{
    public int Id { get; set; }

    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public DateTime AttendanceDate { get; set; }
    public TimeSpan? CheckInTime { get; set; }
    public TimeSpan? CheckOutTime { get; set; }
    public int LateMinutes { get; set; }
    public AttendanceStatus Status { get; set; }
}
