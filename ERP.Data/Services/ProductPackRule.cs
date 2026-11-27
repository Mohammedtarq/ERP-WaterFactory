using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>
/// المنتج المصنَّع بعبوة واحدة: الشرنك والكارتون منتجان مستقلان (كما في النظام القديم: 330*20 و330*40)،
/// لكل منهما قائمة مواده (نايلون أو كرتونة) ورصيده بعدد عبواته. عبوة ثانية على الصنف نفسه تخلط الرصيد
/// (لا يُعرف كم منه شرنك وكم كارتون) وتصرف مواد عبوة واحدة للاثنين — فتُمنع.
/// </summary>
public static class ProductPackRule
{
    /// <returns>رسالة المنع، أو null إن كانت الوحدة مقبولة.</returns>
    public static async Task<string?> CheckAsync(ProjectDbContext db, int itemId, int levelId, decimal equivalentBaseUnits, string levelName)
    {
        if (equivalentBaseUnits <= 1) return null;
        var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId);
        if (item is null || item.SourcingMethod == SourcingMethod.Purchased) return null;
        var other = await db.ItemPackagingLevels.AsNoTracking()
            .Where(l => l.ItemId == itemId && l.Id != levelId && l.EquivalentBaseUnits > 1)
            .Select(l => l.LevelName).FirstOrDefaultAsync();
        return other is null ? null
            : $"المنتج «{item.ItemName}» عبوته «{other}». كل عبوة منتج مستقل بقائمة مواده ورصيده: أنشئ صنفًا جديدًا " +
              $"(مثل «{item.ItemName} {levelName.Trim()}») وطبّق عليه قالب «{levelName.Trim()}» بدل إضافة عبوة ثانية.";
    }

    /// <summary>عبوة المنتج (الوحدة الأكبر من القطعة)، أو null إن لم تُعرَّف.</summary>
    public static Task<ItemPackagingLevel?> PackOfAsync(ProjectDbContext db, int itemId) =>
        db.ItemPackagingLevels.AsNoTracking().Where(l => l.ItemId == itemId && l.EquivalentBaseUnits > 1)
          .OrderByDescending(l => l.EquivalentBaseUnits).FirstOrDefaultAsync();
}
