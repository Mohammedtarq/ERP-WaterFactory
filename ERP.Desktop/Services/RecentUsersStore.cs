using System.IO;
using System.Text.Json;
using ERP.Presentation.Services;

namespace ERP.Desktop.Services;

/// <summary>المستخدمون الأخيرون في %AppData%\ERP-WaterFactory\recent-users.json (للمستخدم في ويندوز فقط، بلا كلمات مرور).</summary>
public class RecentUsersStore : IRecentUsersStore
{
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ERP-WaterFactory", "recent-users.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public List<RecentUser> Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<RecentUser>>(File.ReadAllText(FilePath), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Remember(RecentUser user) => Save(RecentUsers.Merge(Load(), user));

    public void Forget(string username) => Save(Load().Where(u => !string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)).ToList());

    private static void Save(List<RecentUser> list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* ليس ضروريًا للعمل */ }
    }
}
