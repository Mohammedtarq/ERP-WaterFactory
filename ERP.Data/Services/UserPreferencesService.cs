using ERP.Data.ProjectDb;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>تفضيلات المستخدم في الواجهة: مظهره وأقسامه المفضلة (تتبعه على أي جهاز).</summary>
public class UserPreferencesService
{
    private readonly ProjectDbContext _db;
    public UserPreferencesService(ProjectDbContext db) => _db = db;

    public const int MaxFavorites = 24;

    public async Task<(string? Theme, List<string> Favorites)> GetAsync(int userId)
    {
        var u = await _db.Users.AsNoTracking().Where(x => x.Id == userId).Select(x => new { x.ThemeKey, x.Favorites }).FirstOrDefaultAsync();
        return (u?.ThemeKey, Split(u?.Favorites));
    }

    public async Task<bool> UsesNewUiAsync(int userId) =>
        await _db.Users.AsNoTracking().Where(x => x.Id == userId).Select(x => x.UseNewUi).FirstOrDefaultAsync();

    public async Task<bool> AlertSoundAsync(int userId) =>
        await _db.Users.AsNoTracking().Where(x => x.Id == userId).Select(x => x.AlertSound).FirstOrDefaultAsync();

    public async Task SetAlertSoundAsync(int userId, bool on)
    {
        var u = await _db.Users.FirstAsync(x => x.Id == userId);
        u.AlertSound = on;
        await _db.SaveChangesAsync();
    }

    public async Task SetNewUiAsync(int userId, bool on)
    {
        var u = await _db.Users.FirstAsync(x => x.Id == userId);
        u.UseNewUi = on;
        await _db.SaveChangesAsync();
    }

    public async Task SetThemeAsync(int userId, string themeKey)
    {
        var u = await _db.Users.FirstAsync(x => x.Id == userId);
        u.ThemeKey = themeKey.Length > 30 ? themeKey[..30] : themeKey;
        await _db.SaveChangesAsync();
    }

    /// <returns>المفضلة بعد التبديل (يُضاف القسم أو يُزال).</returns>
    public async Task<List<string>> ToggleFavoriteAsync(int userId, string sectionKey)
    {
        var u = await _db.Users.FirstAsync(x => x.Id == userId);
        var list = Split(u.Favorites);
        if (!list.Remove(sectionKey))
        {
            if (list.Count >= MaxFavorites) list.RemoveAt(0);
            list.Add(sectionKey);
        }
        u.Favorites = list.Count == 0 ? null : string.Join(",", list);
        await _db.SaveChangesAsync();
        return list;
    }

    /// <summary>
    /// تثبيت قسم في «مفضلتي» في موضع محدد (السحب والإفلات): إن كان مثبّتًا فيُنقل إلى الموضع، وإلا يُدرج؛ ويُقصّ الفائض من أقدم العناصر.
    /// </summary>
    /// <returns>المفضلة بعد التعديل.</returns>
    public async Task<List<string>> PinFavoriteAsync(int userId, string sectionKey, int index)
    {
        var u = await _db.Users.FirstAsync(x => x.Id == userId);
        var list = Split(u.Favorites);
        var oldIndex = list.IndexOf(sectionKey);
        if (oldIndex >= 0) { list.RemoveAt(oldIndex); if (index > oldIndex) index--; }
        list.Insert(Math.Clamp(index, 0, list.Count), sectionKey);
        while (list.Count > MaxFavorites) list.RemoveAt(list[0] == sectionKey ? 1 : 0);
        u.Favorites = string.Join(",", list);
        await _db.SaveChangesAsync();
        return list;
    }

    /// <summary>إزالة قسم من «مفضلتي» (لا تغيّر القسم نفسه).</summary>
    public async Task<List<string>> RemoveFavoriteAsync(int userId, string sectionKey)
    {
        var u = await _db.Users.FirstAsync(x => x.Id == userId);
        var list = Split(u.Favorites);
        if (list.Remove(sectionKey))
        {
            u.Favorites = list.Count == 0 ? null : string.Join(",", list);
            await _db.SaveChangesAsync();
        }
        return list;
    }

    private static List<string> Split(string? s) =>
        (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
}
