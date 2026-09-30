namespace ERP.Data.ControlDb.Entities;

/// <summary>
/// حساب المستخدم على مستوى النظام كله (بوابة الدخول الموحّدة قبل اختيار المشروع).
/// </summary>
public class GlobalUser
{
    public int Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<UserProjectAccess> ProjectAccesses { get; set; } = new List<UserProjectAccess>();
}
