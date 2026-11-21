namespace ERP.Data.ControlDb.Entities;

/// <summary>
/// مشروع/شركة مستقلة — لكل مشروع قاعدة بيانات SQL Server منفصلة تمامًا.
/// </summary>
public class Project
{
    public int Id { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public string ServerAddress { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<UserProjectAccess> UserAccesses { get; set; } = new List<UserProjectAccess>();
}
