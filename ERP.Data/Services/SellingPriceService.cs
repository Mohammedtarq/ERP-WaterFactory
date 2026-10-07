using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>سطر في شاشة «أسعار البيع»: منتج أو طلب خاص منه، بعبوته، وسعره العام وسعر الوكيل المختار (بالقطعة).</summary>
public class SellingPriceRow
{
    public int ItemId { get; init; }
    public string ItemCode { get; init; } = "";
    public string ItemName { get; init; } = "";
    /// <summary>NULL = المنتج الأساسي.</summary>
    public int? RecipeId { get; init; }
    public string? RecipeName { get; init; }
    public string PackName { get; init; } = "قطعة";
    public decimal PackPieces { get; init; } = 1;
    /// <summary>السعر العام بالقطعة (للطلب الخاص NULL = يأخذ سعر الأساسي).</summary>
    public decimal? GeneralPiece { get; init; }
    /// <summary>سعر الوكيل بالقطعة (NULL = يأخذ السعر العام).</summary>
    public decimal? AgentPiece { get; init; }
    /// <summary>سعر الأساسي العام بالقطعة (لما يرثه الطلب الخاص).</summary>
    public decimal BasePiece { get; init; }
    /// <summary>سعر الوكيل للأساسي بالقطعة (لما يرثه طلبه الخاص).</summary>
    public decimal? BaseAgentPiece { get; init; }
}

/// <summary>تعديل سعر: العام و/أو سعر الوكيل لمنتج أو طلب خاص (بالقطعة؛ NULL = إلغاء السعر الخاص).</summary>
public record SellingPriceChange(int ItemId, int? RecipeId, bool GeneralChanged, decimal? GeneralPiece, bool AgentChanged, decimal? AgentPiece);

/// <summary>
/// «أسعار البيع» (ملاحظة التجربة 3): المنتجات وطلباتها الخاصة فقط (لا المواد الأولية)، بعبوتها، بالسعر العام وسعر كل وكيل.
/// التسعير الهرمي للقطعة: سعر الوكيل للطلب الخاص ← العام للطلب الخاص ← سعر الوكيل للأساسي ← سعر بيع الصنف (fn_Sales_UnitPrice).
/// </summary>
public class SellingPriceService
{
    private readonly ProjectDbContext _db;
    public SellingPriceService(ProjectDbContext db) => _db = db;

    public async Task<List<SellingPriceRow>> RowsAsync(int? agentId)
    {
        var items = await _db.Items.AsNoTracking().Where(i => i.IsActive && i.SourcingMethod != SourcingMethod.Purchased)
            .OrderBy(i => i.ItemName).ToListAsync();
        var ids = items.Select(i => i.Id).ToList();
        var recipes = await _db.CustomRecipes.AsNoTracking().Where(r => r.IsActive && ids.Contains(r.FinishedItemId)).OrderBy(r => r.Name).ToListAsync();
        var packs = await PackFormatter.LoadAsync(_db, ids);
        var agentPrices = agentId is int a
            ? await _db.AgentItemPrices.AsNoTracking().Where(p => p.CustomerId == a && ids.Contains(p.ItemId)).ToListAsync()
            : new List<AgentItemPrice>();

        var rows = new List<SellingPriceRow>();
        foreach (var i in items)
        {
            var top = packs.LevelsOf(i.Id).FirstOrDefault();
            var baseAgent = agentPrices.FirstOrDefault(p => p.ItemId == i.Id && p.CustomRecipeId == null)?.AgentPrice;
            SellingPriceRow Row(CustomRecipe? r) => new()
            {
                ItemId = i.Id, ItemCode = i.ItemCode, ItemName = i.ItemName, RecipeId = r?.Id, RecipeName = r?.Name,
                PackName = top?.LevelName ?? "قطعة", PackPieces = top?.EquivalentBaseUnits ?? 1,
                GeneralPiece = r is null ? i.SalePrice : r.SalePrice, BasePiece = i.SalePrice, BaseAgentPiece = baseAgent,
                AgentPiece = r is null ? baseAgent : agentPrices.FirstOrDefault(p => p.ItemId == i.Id && p.CustomRecipeId == r.Id)?.AgentPrice
            };
            rows.Add(Row(null));
            rows.AddRange(recipes.Where(r => r.FinishedItemId == i.Id).Select(Row));
        }
        return rows;
    }

    public async Task<FinanceOperationResult> SaveAsync(int? agentId, IReadOnlyCollection<SellingPriceChange> changes, int userId)
    {
        if (changes.Count == 0) return FinanceOperationResult.Fail("لا توجد تغييرات");
        if (changes.Any(c => c.GeneralPiece < 0 || c.AgentPiece < 0)) return FinanceOperationResult.Fail("السعر لا يمكن أن يكون سالبًا");
        if (changes.Any(c => c.GeneralChanged && c.RecipeId is null && c.GeneralPiece is null))
            return FinanceOperationResult.Fail("سعر المنتج الأساسي مطلوب (للطلب الخاص يمكن تركه فارغًا ليأخذ سعر الأساسي)");
        if (changes.Any(c => c.AgentChanged) && agentId is null) return FinanceOperationResult.Fail("اختر الوكيل لتعديل أسعاره");
        if (agentId is int a && !await _db.Customers.AnyAsync(c => c.Id == a && c.CustomerType == CustomerType.Agent))
            return FinanceOperationResult.Fail("العميل المختار ليس وكيلًا");

        var itemIds = changes.Select(c => c.ItemId).Distinct().ToList();
        var items = await _db.Items.Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
        var recipeIds = changes.Where(c => c.RecipeId != null).Select(c => c.RecipeId!.Value).Distinct().ToList();
        var recipes = await _db.CustomRecipes.Where(r => recipeIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id);
        var agentRows = agentId is int ag
            ? await _db.AgentItemPrices.Where(p => p.CustomerId == ag && itemIds.Contains(p.ItemId)).ToListAsync()
            : new List<AgentItemPrice>();
        var log = new List<string>();

        foreach (var c in changes)
        {
            if (!items.TryGetValue(c.ItemId, out var item)) return FinanceOperationResult.Fail("صنف غير موجود");
            var recipe = c.RecipeId is int rid ? recipes.GetValueOrDefault(rid) : null;
            if (c.RecipeId is not null && (recipe is null || recipe.FinishedItemId != item.Id)) return FinanceOperationResult.Fail("الطلب الخاص لا يخص هذا المنتج");
            var name = recipe is null ? item.ItemName : $"{item.ItemName} — {recipe.Name}";
            if (c.GeneralChanged)
            {
                if (recipe is null) { log.Add($"{name}: {item.SalePrice:N2} ← {c.GeneralPiece:N2}"); item.SalePrice = c.GeneralPiece!.Value; }
                else { log.Add($"{name}: {Txt(recipe.SalePrice)} ← {Txt(c.GeneralPiece)}"); recipe.SalePrice = c.GeneralPiece; }
            }
            if (c.AgentChanged && agentId is int agent)
            {
                var row = agentRows.FirstOrDefault(p => p.ItemId == item.Id && p.CustomRecipeId == c.RecipeId);
                log.Add($"{name} (وكيل): {Txt(row?.AgentPrice)} ← {Txt(c.AgentPiece)}");
                if (c.AgentPiece is null) { if (row is not null) _db.AgentItemPrices.Remove(row); }
                else if (row is null) _db.AgentItemPrices.Add(new AgentItemPrice { CustomerId = agent, ItemId = item.Id, CustomRecipeId = c.RecipeId, AgentPrice = c.AgentPiece.Value });
                else row.AgentPrice = c.AgentPiece.Value;
            }
        }
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Edit", "SellingPrices", agentId, "أسعار البيع (بالقطعة): " + string.Join("، ", log));
        return FinanceOperationResult.Ok();

        static string Txt(decimal? v) => v is null ? "—" : v.Value.ToString("N2");
    }
}
