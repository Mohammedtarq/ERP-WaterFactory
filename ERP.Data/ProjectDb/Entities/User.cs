using System.ComponentModel.DataAnnotations.Schema;

namespace ERP.Data.ProjectDb.Entities;

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;

    public int? EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    public int RoleId { get; set; }
    public Role Role { get; set; } = null!;

    public string PreferredLanguage { get; set; } = "ar";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>مظهر الواجهة لهذا المستخدم (49_user_preferences.sql)؛ فارغ = «الرحمة».</summary>
    public string? ThemeKey { get; set; }
    /// <summary>أقسامه المفضلة: مفاتيح مفصولة بفواصل.</summary>
    public string? Favorites { get; set; }
    /// <summary>«الواجهة الجديدة»: لوحة منبثقة بمربعات أقسام الوحدة (50_user_new_ui.sql).</summary>
    public bool UseNewUi { get; set; }

    /// <summary>الصورة الظاهرة في القوائم: صورته الخاصة وإلا صورة موظفه (تُملأ عند الحاجة — ليست عمودًا).</summary>
    [NotMapped] public byte[]? PhotoPreview { get; set; }
}
