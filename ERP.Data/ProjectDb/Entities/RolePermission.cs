namespace ERP.Data.ProjectDb.Entities;

/// <summary>
/// صلاحية دور واحد على وحدة واحدة. هذا الصف هو ما يقرر ظهور عنصر
/// الشريط الجانبي (CanView) وتفعيل أزرار الإضافة/التعديل/الحذف/الترحيل.
/// </summary>
public class RolePermission
{
    public int Id { get; set; }

    public int RoleId { get; set; }
    public Role Role { get; set; } = null!;

    public string ModuleCode { get; set; } = string.Empty;

    public bool CanView { get; set; }
    public bool CanAdd { get; set; }
    public bool CanEdit { get; set; }
    public bool CanDelete { get; set; }
    public bool CanPost { get; set; }
}
