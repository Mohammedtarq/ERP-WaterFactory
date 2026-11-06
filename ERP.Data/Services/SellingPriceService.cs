using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>سطر في شاشة «أسعار البيع»: منتج أو طلب خاص منه، بعبوة بيعه، وسعر العبوة العام الموحّد.</summary>
public class SellingPriceRow
{
    public int ItemId { get; init; }
    public string ItemCode { get; init; } = "";
    public string ItemName { get; init; } = "";
    /// <summary>NULL = المنتج الأساسي.</summary>
    public int? RecipeId { get; init; }
    public string? RecipeName { get; init; }
    public int LevelId { get; init; }
    public string PackName { get; init; } = "";
    public decimal PackPieces { get; init; } = 1;
    /// <summary>سعر العبوة (للطلب الخاص NULL = يأخذ سعر الأساسي).</summary>
    public decimal? PackPrice { get; init; }
    /// <summary>سعر عبوة الأساسي (لما يرثه الطلب الخاص).</summary>
    public decimal BasePackPrice { get; init; }
}

/// <summary>تعديل سعر عبوة لمنتج أو طلب خاص (NULL للطلب الخاص = يرث سعر الأساسي).</summary>
public record SellingPriceChange(int ItemId, int? RecipeId, int LevelId, decimal? PackPrice);

/// <summary>
/// «أسعار البيع» بالعبوة (docs/agreed-pricing-and-names.md قسم «أ»): كل عبوة تُحفظ كما تُكتب (PackPrices)، والسعر العام موحّد لكل الزبائن.
/// الطلب الخاص سعره على المتغير لا على الزبون؛ بلا سعر يأخذ سعر الأساسي. لا أسعار وكلاء (بياناتها القديمة محفوظة بلا استعمال).
/// </summary>
public class SellingPriceService
{
    private readonly ProjectDbContext _db;
    public SellingPriceService(ProjectDbContext db) => _db = db;

    public async Task<List<SellingPriceRow>> RowsAsync()
    {
        var items = await _db.Items.AsNoTracking().Where(i => i.IsActive && i.SourcingMethod != SourcingMethod.Purchased)
            .OrderBy(i => i.ItemName).ToListAsync();
        var ids = items.Select(i => i.Id).ToList();
        var recipes = await _db.CustomRecipes.AsNoTracking().Where(r => r.IsActive && ids.Contains(r.FinishedItemId)).OrderBy(r => r.Name).ToListAsync();
        // عبوات البيع (لا قطعة لمنتج له عبوة أكبر)
        var levels = (await _db.ItemPackagingLevels.AsNoTracking().Where(l => ids.Contains(l.ItemId) && l.IsSellableUnit).ToListAsync())
            .GroupBy(l => l.ItemId)
            .ToDictionary(g => g.Key, g => g.Any(l => l.EquivalentBaseUnits > 1) ? g.Where(l => l.EquivalentBaseUnits > 1).OrderByDescending(l => l.EquivalentBaseUnits).ToList() : g.ToList());
        var prices = await _db.PackPrices.AsNoTracking().Where(p => ids.Contains(p.ItemId)).ToListAsync();

        var rows = new List<SellingPriceRow>();
        foreach (var i in items)
        {
            if (!levels.TryGetValue(i.Id, out var itemLevels)) continue;
            foreach (var l in itemLevels)
            {
                var basePrice = prices.FirstOrDefault(p => p.PackagingLevelId == l.Id && p.CustomRecipeId == null)?.Price
                                ?? Math.Round(i.SalePrice * l.EquivalentBaseUnits, 2);
                SellingPriceRow Row(CustomRecipe? r) => new()
                {
                    ItemId = i.Id, ItemCode = i.ItemCode, ItemName = i.ItemName, RecipeId = r?.Id, RecipeName = r?.Name,
                    LevelId = l.Id, PackName = l.LevelName, PackPieces = l.EquivalentBaseUnits, BasePackPrice = basePrice,
                    PackPrice = r is null ? basePrice : prices.FirstOrDefault(p => p.PackagingLevelId == l.Id && p.CustomRecipeId == r.Id)?.Price
                };
                rows.Add(Row(null));
                rows.AddRange(recipes.Where(r => r.FinishedItemId == i.Id).Select(Row));
            }
        }
        return rows;
    }

    public async Task<FinanceOperationResult> SaveAsync(IReadOnlyCollection<SellingPriceChange> changes, int userId)
    {
        if (changes.Count == 0) return FinanceOperationResult.Fail("لا توجد تغييرات");
        if (changes.Any(c => c.PackPrice < 0)) return FinanceOperationResult.Fail("السعر لا يمكن أن يكون سالبًا");
        if (changes.Any(c => c.RecipeId is null && c.PackPrice is null))
            return FinanceOperationResult.Fail("سعر المنتج الأساسي مطلوب (للطلب الخاص يمكن تركه فارغًا ليأخذ سعر الأساسي)");

        var levelIds = changes.Select(c => c.LevelId).Distinct().ToList();
        var levels = await _db.ItemPackagingLevels.Include(l => l.Item).Where(l => levelIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id);
        var recipeIds = changes.Where(c => c.RecipeId != null).Select(c => c.RecipeId!.Value).Distinct().ToList();
        var recipes = await _db.CustomRecipes.Where(r => recipeIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id);
        var existing = await _db.PackPrices.Where(p => levelIds.Contains(p.PackagingLevelId)).ToListAsync();
        var log = new List<string>();

        foreach (var c in changes)
        {
            if (!levels.TryGetValue(c.LevelId, out var level) || level.ItemId != c.ItemId) return FinanceOperationResult.Fail("العبوة لا تخص هذا المنتج");
            var recipe = c.RecipeId is int rid ? recipes.GetValueOrDefault(rid) : null;
            if (c.RecipeId is not null && (recipe is null || recipe.FinishedItemId != c.ItemId)) return FinanceOperationResult.Fail("الطلب الخاص لا يخص هذا المنتج");
            var name = $"{level.Item.ItemName} {level.LevelName}" + (recipe is null ? "" : $" — {recipe.Name}");
            var row = existing.FirstOrDefault(p => p.PackagingLevelId == c.LevelId && p.CustomRecipeId == c.RecipeId);
            log.Add($"{name}: {Txt(row?.Price)} ← {Txt(c.PackPrice)}");
            if (c.PackPrice is null) { if (row is not null) _db.PackPrices.Remove(row); }
            else if (row is null) _db.PackPrices.Add(new PackPrice { ItemId = c.ItemId, PackagingLevelId = c.LevelId, CustomRecipeId = c.RecipeId, Price = c.PackPrice.Value, UpdatedByUserId = userId });
            else { row.Price = c.PackPrice.Value; row.UpdatedAt = DateTime.UtcNow; row.UpdatedByUserId = userId; }

            // سعر القطعة المرجعي يتبع العبوة الكبرى (للتقييم والتقارير القديمة فقط — البيع بالعبوة)
            var piece = c.PackPrice is { } p ? Math.Round(p / level.EquivalentBaseUnits, 2) : (decimal?)null;
            if (recipe is null) level.Item.SalePrice = piece!.Value;
            else recipe.SalePrice = piece;
        }
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Edit", "SellingPrices", null, "أسعار البيع (بالعبوة): " + string.Join("، ", log));
        return FinanceOperationResult.Ok();

        static string Txt(decimal? v) => v is null ? "—" : v.Value.ToString("N0");
    }
}
