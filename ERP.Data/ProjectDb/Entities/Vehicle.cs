namespace ERP.Data.ProjectDb.Entities;

public class Vehicle
{
    public int Id { get; set; }
    public string VehicleName { get; set; } = string.Empty;
    public string? PlateNumber { get; set; }

    public int? AssignedEmployeeId { get; set; }
    public Employee? AssignedEmployee { get; set; }

    public DateTime? DrivingLicenseExpiry { get; set; }
    public DateTime? VehicleRegistrationExpiry { get; set; }
    public bool IsActive { get; set; } = true;
}
