namespace ERP.Data.ProjectDb.Entities;

public class Role
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    public ICollection<RolePermission> Permissions { get; set; } = new List<RolePermission>();
}
