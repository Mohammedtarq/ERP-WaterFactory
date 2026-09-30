namespace ERP.Data.ProjectDb.Entities;

public class Shift
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public TimeSpan CheckInTime { get; set; }
    public int CheckInGraceMinutes { get; set; }
    public TimeSpan CheckOutTime { get; set; }
    public int CheckOutGraceMinutes { get; set; }
    public TimeSpan? OvertimeStartsAfter { get; set; }
}
