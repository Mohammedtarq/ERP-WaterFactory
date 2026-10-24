namespace ERP.Presentation.Services;

/// <summary>مستخدم دخل من هذا الجهاز: يظهر في شاشة الدخول بصورته واسمه ووظيفته (الأحدث أولًا).</summary>
public sealed record RecentUser(string Username, string FullName, string? Role, string? Project, byte[]? Photo, string? Theme,
                                DateTime LastLoginUtc, string? CompanyName = null, byte[]? CompanyLogo = null);

/// <summary>حفظ المستخدمين الأخيرين على هذا الجهاز (ملف للمستخدم في ويندوز؛ لا كلمات مرور).</summary>
public interface IRecentUsersStore
{
    List<RecentUser> Load();
    void Remember(RecentUser user);
    void Forget(string username);
}

public static class RecentUsers
{
    public const int Max = 8;
    public static IRecentUsersStore? Store { get; set; }

    /// <summary>غيّر المستخدم مظهره: شاشة الدخول على هذا الجهاز تظهر به في المرة القادمة.</summary>
    public static void UpdateTheme(string username, string themeKey)
    {
        if (Store?.Load().FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)) is { } me)
            Store.Remember(me with { Theme = themeKey });
    }

    /// <summary>دمج دخول جديد مع القائمة: الأحدث أولًا، بلا تكرار، وحد أقصى.</summary>
    public static List<RecentUser> Merge(IEnumerable<RecentUser> existing, RecentUser user) =>
        new[] { user }.Concat(existing.Where(u => !string.Equals(u.Username, user.Username, StringComparison.OrdinalIgnoreCase)))
                      .Take(Max).ToList();
}
