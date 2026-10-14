using System.Globalization;
using ERP.Cloud.Contracts;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>
/// الاسم الموحد للمنتجات في شاشات المعمل وتقاريره (قرار المالك): «كارتون 40×330 الرحمة»، «كارتون 40×330 مطعم داون تاون».
/// المنتج + عبوته الكبرى (أو المحددة) + المتغير (الأساسي أو الطلب الخاص). يُحمَّل مرة لكل شاشة أو تقرير.
/// </summary>
public sealed class ProductNames
{
    private readonly Dictionary<int, (string Name, decimal? SizeMl)> _items;
    private readonly Dictionary<int, List<ItemPackagingLevel>> _levels;
    private readonly Dictionary<int, string> _variants;
    private readonly string _base;

    private ProductNames(Dictionary<int, (string, decimal?)> items, Dictionary<int, List<ItemPackagingLevel>> levels,
                         Dictionary<int, string> variants, string baseVariant) =>
        (_items, _levels, _variants, _base) = (items, levels, variants, baseVariant);

    public static async Task<ProductNames> LoadAsync(ProjectDbContext db, IEnumerable<int>? itemIds = null)
    {
        var ids = itemIds?.Distinct().ToList();
        var itemsQ = db.Items.AsNoTracking();
        var levelsQ = db.ItemPackagingLevels.AsNoTracking().Where(l => l.EquivalentBaseUnits > 0);
        var recipesQ = db.CustomRecipes.AsNoTracking();
        if (ids is not null)
        {
            itemsQ = itemsQ.Where(i => ids.Contains(i.Id));
            levelsQ = levelsQ.Where(l => ids.Contains(l.ItemId));
            recipesQ = recipesQ.Where(r => ids.Contains(r.FinishedItemId));
        }
        var items = await itemsQ.Select(i => new { i.Id, i.ItemName, i.SizeMl }).ToDictionaryAsync(i => i.Id, i => (i.ItemName, i.SizeMl));
        var levels = (await levelsQ.ToListAsync()).GroupBy(l => l.ItemId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.EquivalentBaseUnits).ToList());
        var variants = (await recipesQ.Select(r => new { r.Id, r.ShortName, Customer = r.Customer != null ? r.Customer.Name : null, r.Name }).ToListAsync())
            .ToDictionary(r => r.Id, r => ProductName.Variant(r.ShortName, r.Customer, r.Name, false, null));
        var baseName = await db.CompanyProfiles.AsNoTracking().Select(c => c.BaseVariantName).FirstOrDefaultAsync();
        return new ProductNames(items, levels, variants, ProductName.Variant(null, null, null, true, baseName));
    }

    /// <summary>اسم المتغير: الأساسي (مثل «الرحمة») أو الطلب الخاص.</summary>
    public string Variant(int? recipeId) => recipeId is int r ? _variants.GetValueOrDefault(r, "طلب خاص") : _base;

    public decimal? SizeOf(int itemId) =>
        _items.TryGetValue(itemId, out var i) ? i.SizeMl ?? ProductName.ParseSizeMl(i.Name) : null;

    /// <summary>العبوة الكبرى للمنتج (أو null إن لم تُعرَّف له عبوات).</summary>
    public ItemPackagingLevel? TopLevel(int itemId) => _levels.GetValueOrDefault(itemId)?.FirstOrDefault();

    /// <summary>الاسم الموحد بعبوة محددة أو بالعبوة الكبرى: «كارتون 40×330 الرحمة»؛ صنف بلا عبوات: اسمه ومتغيره.</summary>
    public string Name(int itemId, int? recipeId = null, int? levelId = null)
    {
        var item = _items.GetValueOrDefault(itemId, ("؟", null));
        var level = levelId is int l ? _levels.GetValueOrDefault(itemId)?.FirstOrDefault(x => x.Id == l) : TopLevel(itemId);
        if (level is null) return $"{item.Item1} {Variant(recipeId)}".Trim();
        return ProductName.Of(level.LevelName, level.EquivalentBaseUnits, SizeOf(itemId), item.Item1, Variant(recipeId));
    }

    /// <summary>أجزاء الاسم (العبوة، المحتوى، المتغير) للأعمدة المنفصلة؛ صنف بلا عبوات: العبوة «قطعة».</summary>
    public (string Pack, string Content, string Variant) Parts(int itemId, int? recipeId = null, int? levelId = null)
    {
        var level = levelId is int l ? _levels.GetValueOrDefault(itemId)?.FirstOrDefault(x => x.Id == l) : TopLevel(itemId);
        return level is null
            ? ProductName.Parts("قطعة", 1, SizeOf(itemId), Variant(recipeId))
            : ProductName.Parts(level.LevelName, level.EquivalentBaseUnits, SizeOf(itemId), Variant(recipeId));
    }

    /// <summary>الاسم الموحد بعبوة معروفة باسمها وعدد قطعها (كما في سطر مستند).</summary>
    public string Name(int itemId, int? recipeId, string levelName, decimal piecesPerPack) =>
        ProductName.Of(levelName, piecesPerPack, SizeOf(itemId), _items.GetValueOrDefault(itemId, ("؟", null)).Item1, Variant(recipeId));

    /// <summary>الكمية بالاسم الموحد: «11 كارتون 40×330 الرحمة» و«+ 4 قطعة» لما دون العبوة.</summary>
    public string Quantity(int itemId, int? recipeId, decimal pieces)
    {
        if (pieces == 0) return $"0 {Name(itemId, recipeId)}";
        var sign = pieces < 0 ? "-" : "";
        var abs = Math.Abs(pieces);
        var top = TopLevel(itemId);
        if (top is null || top.EquivalentBaseUnits <= 1) return $"{sign}{Number(abs)} {Name(itemId, recipeId)}";
        var n = Math.Floor(abs / top.EquivalentBaseUnits);
        var rest = abs - n * top.EquivalentBaseUnits;
        if (n == 0) return $"{sign}{Number(rest)} قطعة — {Name(itemId, recipeId)}";
        return $"{sign}{Number(n)} {Name(itemId, recipeId)}" + (rest > 0 ? $" + {Number(rest)} قطعة" : "");
    }

    /// <summary>سطر في قائمة الأسماء الموحدة للمراجعة.</summary>
    public record CatalogRow(string Code, string ItemName, string Level, decimal Pieces, string Size, string Variant, string Kind, string UnifiedName);

    /// <summary>
    /// كل الأسماء الموحدة للمنتجات المصنّعة الفعّالة: كل عبوة × (الأساسي + كل طلب خاص فعّال) — للمراجعة قبل الاعتماد.
    /// </summary>
    public static async Task<List<CatalogRow>> CatalogAsync(ProjectDbContext db)
    {
        var items = await db.Items.AsNoTracking().Where(i => i.IsActive && i.SourcingMethod != SourcingMethod.Purchased)
            .OrderBy(i => i.ItemName).ToListAsync();
        var ids = items.Select(i => i.Id).ToList();
        var names = await LoadAsync(db, ids);
        var recipes = await db.CustomRecipes.AsNoTracking().Include(r => r.Customer).Where(r => r.IsActive && ids.Contains(r.FinishedItemId))
            .OrderBy(r => r.Name).ToListAsync();
        var rows = new List<CatalogRow>();
        foreach (var i in items)
        {
            var levels = names._levels.GetValueOrDefault(i.Id) ?? new();
            foreach (var l in levels.Where(l => l.IsSellableUnit && l.EquivalentBaseUnits > 1).DefaultIfEmpty(levels.FirstOrDefault()).OfType<ItemPackagingLevel>())
                foreach (var recipeId in new int?[] { null }.Concat(recipes.Where(r => r.FinishedItemId == i.Id).Select(r => (int?)r.Id)))
                {
                    var recipe = recipes.FirstOrDefault(r => r.Id == recipeId);
                    var size = names.SizeOf(i.Id);
                    rows.Add(new CatalogRow(i.ItemCode, i.ItemName, l.LevelName, l.EquivalentBaseUnits,
                        size is decimal s ? ProductName.Size(s) + (i.SizeMl is null ? " (من الاسم)" : "") : "غير معروف",
                        names.Variant(recipeId), VariantStockService.KindOf(recipe), names.Name(i.Id, recipeId, l.Id)));
                }
        }
        return rows;
    }

    private static string Number(decimal n) =>
        n == Math.Floor(n) ? n.ToString("N0", CultureInfo.InvariantCulture) : n.ToString("#,0.###", CultureInfo.InvariantCulture);
}
