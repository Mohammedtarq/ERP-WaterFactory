using System.Globalization;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>سطر في شاشة «حدود تنبيه الأسماء الخاصة»: اسم واحد (منتج + متغير) برصيده وحدّه بالعبوة.</summary>
public class VariantAlertRow
{
    public int ItemId { get; init; }
    /// <summary>الطلب الخاص؛ null = الأساسي.</summary>
    public int? RecipeId { get; init; }
    /// <summary>الاسم الموحد: «شرنك 20×330 زواج سعيد».</summary>
    public string Name { get; init; } = "";
    public string VariantName { get; init; } = "";
    public string PackName { get; init; } = "";
    public decimal PackPieces { get; init; }
    public decimal BalancePieces { get; init; }
    public decimal BalancePacks => PackPieces <= 0 ? BalancePieces : Math.Round(BalancePieces / PackPieces, 1);
    public string BalanceText { get; init; } = "";
    /// <summary>الحد اليدوي بالعبوة؛ null = غير متابَع.</summary>
    public decimal? MinPacks { get; init; }
    /// <summary>«يكفي كم يومًا» = الرصيد ÷ المعدل اليومي للخارج إلى السوق في آخر 8 أسابيع (للاسترشاد فقط).</summary>
    public decimal? DaysCover { get; init; }
    public bool IsTracked => MinPacks is not null;
    public bool IsLow => MinPacks is { } m && BalancePacks <= m;
    public string StatusText => !IsTracked ? "غير متابَع" : IsLow ? "تحت الحد" : "كافٍ";
    public string DaysText => DaysCover is { } d ? (d >= 100 ? "+100 يوم" : $"{d.ToString("0.#", CultureInfo.InvariantCulture)} يوم") : "—";
    public string MinText => MinPacks is { } m ? $"{m.ToString("#,0.###", CultureInfo.InvariantCulture)} {PackName}" : "";
}

/// <summary>
/// حدود تنبيه الأسماء الخاصة (قسم «ح» من الاتفاقات — المرحلة الأولى): لكل اسم خاص وللأساسي حدّ يدوي بالعبوة،
/// يُقارن برصيد المنتج التام لذلك المتغير؛ الاسم بلا حد لا يُتابَع ولا ينبّه. المواد الأولية خارجها.
/// </summary>
public class VariantAlertService
{
    /// <summary>أيام حساب المعدل اليومي (آخر 8 أسابيع).</summary>
    public const int RateDays = 56;

    private static readonly StockTransactionType[] MarketTypes =
        { StockTransactionType.SalesIssue, StockTransactionType.RepLoad, StockTransactionType.RepReturn };

    private readonly ProjectDbContext _db;
    public VariantAlertService(ProjectDbContext db) => _db = db;

    /// <summary>مخازن المنتج التام التي يُحسب منها رصيد الاسم (لا السيارات ولا التالف ولا تحت التصنيع).</summary>
    private Task<List<int>> StoresAsync() => _db.Warehouses.AsNoTracking()
        .Where(w => w.IsActive && (w.WarehouseType == WarehouseType.FinishedGoods || w.WarehouseType == WarehouseType.Main || w.WarehouseType == WarehouseType.Sub))
        .Select(w => w.Id).ToListAsync();

    /// <summary>كل الأسماء: الأساسي وكل طلب خاص فعّال لكل منتج مصنَّع فعّال، برصيده وحدّه.</summary>
    public async Task<List<VariantAlertRow>> ListAsync()
    {
        var items = (await _db.Items.AsNoTracking().Where(i => i.IsActive).ToListAsync())
            .Where(i => i.SourcingMethod != SourcingMethod.Purchased).ToList();
        if (items.Count == 0) return new();
        var ids = items.Select(i => i.Id).ToList();
        var recipes = await _db.CustomRecipes.AsNoTracking().Where(r => r.IsActive && ids.Contains(r.FinishedItemId)).ToListAsync();
        var stores = await StoresAsync();
        var balances = (await _db.StockTransactions.Where(t => stores.Contains(t.WarehouseId) && ids.Contains(t.ItemId))
                .GroupBy(t => new { t.ItemId, RecipeId = t.Batch != null ? t.Batch.CustomRecipeId : null })
                .Select(g => new { g.Key.ItemId, g.Key.RecipeId, Qty = g.Sum(t => t.QuantityBaseUnits) }).ToListAsync())
            .ToDictionary(b => (b.ItemId, b.RecipeId), b => b.Qty);
        var since = DateTime.UtcNow.AddDays(-RateDays);
        var outflow = (await _db.StockTransactions
                .Where(t => stores.Contains(t.WarehouseId) && ids.Contains(t.ItemId) && t.TransactionDate >= since && MarketTypes.Contains(t.TransactionType))
                .GroupBy(t => new { t.ItemId, RecipeId = t.Batch != null ? t.Batch.CustomRecipeId : null })
                .Select(g => new { g.Key.ItemId, g.Key.RecipeId, Qty = g.Sum(t => t.QuantityBaseUnits) }).ToListAsync())
            .ToDictionary(b => (b.ItemId, b.RecipeId), b => -b.Qty);
        var limits = (await _db.VariantAlerts.AsNoTracking().Where(a => ids.Contains(a.ItemId)).ToListAsync())
            .ToDictionary(a => (a.ItemId, a.CustomRecipeId), a => a.MinPacks);
        var names = await ProductNames.LoadAsync(_db, ids);
        var packs = await PackFormatter.LoadAsync(_db, ids);

        var rows = new List<VariantAlertRow>();
        foreach (var i in items.OrderBy(i => i.ItemName))
        {
            var top = names.TopLevel(i.Id);
            foreach (var recipe in new int?[] { null }.Concat(recipes.Where(r => r.FinishedItemId == i.Id).OrderBy(r => r.Name).Select(r => (int?)r.Id)))
            {
                var balance = balances.GetValueOrDefault((i.Id, recipe));
                var daily = outflow.GetValueOrDefault((i.Id, recipe)) / RateDays;
                rows.Add(new VariantAlertRow
                {
                    ItemId = i.Id, RecipeId = recipe, Name = names.Name(i.Id, recipe), VariantName = names.Variant(recipe) is { Length: > 0 } v ? v : "الأساسي",
                    PackName = top?.LevelName ?? "قطعة", PackPieces = top?.EquivalentBaseUnits ?? 1,
                    BalancePieces = balance, BalanceText = packs.Of(i.Id, balance),
                    MinPacks = limits.TryGetValue((i.Id, recipe), out var min) ? min : null,
                    DaysCover = daily > 0 ? Math.Round(Math.Max(balance, 0) / daily, 1) : null
                });
            }
        }
        // ما تحت الحد أولًا، ثم المتابَع، ثم غير المتابَع
        return rows.OrderBy(r => r.IsLow ? 0 : r.IsTracked ? 1 : 2).ThenBy(r => r.Name).ToList();
    }

    /// <summary>الأسماء تحت حدّها (للجرس).</summary>
    public async Task<List<VariantAlertRow>> LowAsync()
    {
        if (!await _db.VariantAlerts.AnyAsync()) return new();
        return (await ListAsync()).Where(r => r.IsLow).ToList();
    }

    /// <summary>حفظ الحدود: قيمة = حد بالعبوة، null = إلغاء المتابعة.</summary>
    public async Task<FinanceOperationResult> SaveAsync(IReadOnlyCollection<(int ItemId, int? RecipeId, decimal? MinPacks)> changes, int userId)
    {
        if (changes.Any(c => c.MinPacks < 0)) return FinanceOperationResult.Fail("حد التنبيه لا يمكن أن يكون سالبًا");
        var ids = changes.Select(c => c.ItemId).Distinct().ToList();
        var existing = await _db.VariantAlerts.Where(a => ids.Contains(a.ItemId)).ToListAsync();
        foreach (var (itemId, recipeId, min) in changes)
        {
            var row = existing.FirstOrDefault(a => a.ItemId == itemId && a.CustomRecipeId == recipeId);
            if (row is not null)
            {
                if (min is null) _db.VariantAlerts.Remove(row);
                else { row.MinPacks = min.Value; row.UpdatedAt = DateTime.UtcNow; row.UpdatedByUserId = userId; }
            }
            else if (min is not null)
                _db.VariantAlerts.Add(new VariantAlert { ItemId = itemId, CustomRecipeId = recipeId, MinPacks = min.Value, UpdatedByUserId = userId });
        }
        await _db.SaveChangesAsync();
        return FinanceOperationResult.Ok();
    }
}
