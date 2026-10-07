using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>متغير منتج في فترة: المنتج والمبيع ورصيده الحالي (المخازن والسيارات)، وكلفة إنتاج القطعة.</summary>
public record VariantSummaryRow(string ItemName, int? RecipeId, string Variant, string Kind, decimal Produced, decimal ProducedCost,
                                decimal Sold, decimal Balance, string BalanceText)
{
    public decimal UnitCost => Produced > 0 ? Math.Round(ProducedCost / Produced, 2) : 0;
    /// <summary>المنتج والمبيع بالعبوات ("300 شرنك").</summary>
    public string ProducedText { get; init; } = "";
    public string SoldText { get; init; } = "";
}

/// <summary>تشغيلة منتج تام برصيدها الحالي ومتغيرها.</summary>
public record VariantBatchRow(int BatchId, int ItemId, string ItemName, string BatchNumber, DateTime? ManufactureDate, int? RecipeId, string Variant, decimal Balance)
{
    /// <summary>الرصيد بالعبوات.</summary>
    public string BalanceText { get; init; } = "";
}

/// <summary>
/// المنتج التام حسب المتغير (الأساسي، محجوز مطعم، مناسبة): ملخص الفترة، والتشغيلات بأرصدتها،
/// وتصحيح متغير تشغيلة سُجّلت خطأً (بالصلاحية الخاصة، ومع سجل الحركات).
/// </summary>
public class VariantStockService
{
    private readonly ProjectDbContext _db;
    public VariantStockService(ProjectDbContext db) => _db = db;

    private static readonly StockTransactionType[] SaleTypes =
        { StockTransactionType.SalesIssue, StockTransactionType.RepSale, StockTransactionType.SalesVoid };

    public static string KindOf(CustomRecipe? r) =>
        r is null ? "أساسي" : r.CustomerId is null ? "مناسبة (يُطلب بالاسم)" : $"محجوز لـ {r.Customer?.Name}";

    public async Task<List<VariantSummaryRow>> SummaryAsync(DateTime from, DateTime to)
    {
        var fromUtc = from.Date.ToUniversalTime();
        var toUtc = to.Date.AddDays(1).ToUniversalTime();
        var rows = await _db.StockTransactions.AsNoTracking()
            .Where(t => t.Item.SourcingMethod == SourcingMethod.Manufactured && t.Warehouse.WarehouseType != WarehouseType.Damaged)
            .GroupBy(t => new { t.ItemId, Recipe = t.Batch != null ? t.Batch.CustomRecipeId : null })
            .Select(g => new
            {
                g.Key.ItemId, g.Key.Recipe,
                Produced = g.Where(t => t.TransactionType == StockTransactionType.ProductionOutput && t.TransactionDate >= fromUtc && t.TransactionDate < toUtc)
                            .Sum(t => (decimal?)t.QuantityBaseUnits) ?? 0,
                Cost = g.Where(t => t.TransactionType == StockTransactionType.ProductionOutput && t.TransactionDate >= fromUtc && t.TransactionDate < toUtc)
                        .Sum(t => (decimal?)(t.QuantityBaseUnits * (t.UnitCost ?? 0))) ?? 0,
                Sold = -(g.Where(t => SaleTypes.Contains(t.TransactionType) && t.TransactionDate >= fromUtc && t.TransactionDate < toUtc)
                          .Sum(t => (decimal?)t.QuantityBaseUnits) ?? 0),
                Balance = g.Sum(t => t.QuantityBaseUnits)
            }).ToListAsync();
        rows = rows.Where(r => r.Produced != 0 || r.Sold != 0 || r.Balance != 0).ToList();

        var itemIds = rows.Select(r => r.ItemId).Distinct().ToList();
        var items = await _db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.ItemName);
        var recipeIds = rows.Where(r => r.Recipe != null).Select(r => r.Recipe!.Value).Distinct().ToList();
        var recipes = await _db.CustomRecipes.AsNoTracking().Include(r => r.Customer).Where(r => recipeIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id);
        var levels = (await _db.ItemPackagingLevels.AsNoTracking().Where(l => itemIds.Contains(l.ItemId)).ToListAsync())
            .GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.EquivalentBaseUnits).ToList());

        return rows.Select(r =>
        {
            var recipe = r.Recipe is int id ? recipes.GetValueOrDefault(id) : null;
            var lv = levels.GetValueOrDefault(r.ItemId);
            return new VariantSummaryRow(items[r.ItemId], r.Recipe, recipe?.Name ?? "أساسي", KindOf(recipe), r.Produced, Math.Round(r.Cost, 2), r.Sold, r.Balance,
                                         WarehouseDocumentService.Breakdown(r.Balance, lv))
            {
                ProducedText = WarehouseDocumentService.Breakdown(r.Produced, lv), SoldText = WarehouseDocumentService.Breakdown(r.Sold, lv)
            };
        }).OrderBy(r => r.ItemName).ThenBy(r => r.RecipeId is null ? 0 : 1).ThenBy(r => r.Variant).ToList();
    }

    /// <summary>تشغيلات المنتج التام ذات الرصيد (في كل المخازن والسيارات عدا التالف).</summary>
    public async Task<List<VariantBatchRow>> BatchesAsync()
    {
        var rows = (await _db.StockTransactions.AsNoTracking()
            .Where(t => t.BatchId != null && t.Item.SourcingMethod == SourcingMethod.Manufactured && t.Warehouse.WarehouseType != WarehouseType.Damaged)
            .GroupBy(t => new { t.BatchId, t.ItemId, t.Item.ItemName, t.Batch!.BatchNumber, t.Batch.ManufactureDate, t.Batch.CustomRecipeId,
                                Recipe = t.Batch.CustomRecipe != null ? t.Batch.CustomRecipe.Name : null })
            .Select(g => new { g.Key, Qty = g.Sum(t => t.QuantityBaseUnits) })
            .Where(x => x.Qty > 0).ToListAsync())
        .Select(x => new VariantBatchRow(x.Key.BatchId!.Value, x.Key.ItemId, x.Key.ItemName, x.Key.BatchNumber, x.Key.ManufactureDate,
                                         x.Key.CustomRecipeId, x.Key.Recipe ?? "أساسي", x.Qty))
        .OrderByDescending(r => r.ManufactureDate).ThenBy(r => r.ItemName).ToList();
        var itemIds = rows.Select(r => r.ItemId).Distinct().ToList();
        var levels = (await _db.ItemPackagingLevels.AsNoTracking().Where(l => itemIds.Contains(l.ItemId)).ToListAsync())
            .GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.EquivalentBaseUnits).ToList());
        return rows.Select(r => r with { BalanceText = WarehouseDocumentService.Breakdown(r.Balance, levels.GetValueOrDefault(r.ItemId)) }).ToList();
    }

    /// <summary>تصحيح متغير تشغيلة (مثلًا إنتاج مطعم سُجّل أساسيًا): يغيّر من يحق له الصرف منها من الآن.</summary>
    public async Task<FinanceOperationResult> SetBatchVariantAsync(int batchId, int? recipeId, string reason, int userId)
    {
        if (string.IsNullOrWhiteSpace(reason)) return FinanceOperationResult.Fail("اكتب سبب التصحيح");
        if (!await SpecialPermission.HasAsync(_db, userId, SpecialPermission.ReservedStock))
            return FinanceOperationResult.Fail($"تصحيح متغير التشغيلة يحتاج صلاحية «{SpecialPermission.NameOf(SpecialPermission.ReservedStock)}»");
        var batch = await _db.ItemBatches.Include(b => b.CustomRecipe).FirstOrDefaultAsync(b => b.Id == batchId);
        if (batch is null) return FinanceOperationResult.Fail("التشغيلة غير موجودة");
        CustomRecipe? recipe = null;
        if (recipeId is int rid && (recipe = await _db.CustomRecipes.FirstOrDefaultAsync(r => r.Id == rid && r.FinishedItemId == batch.ItemId)) is null)
            return FinanceOperationResult.Fail("المتغير المختار لا يخص منتج التشغيلة");
        if (batch.CustomRecipeId == recipeId) return FinanceOperationResult.Fail("التشغيلة على هذا المتغير أصلًا");
        var before = batch.CustomRecipe?.Name ?? "أساسي";
        batch.CustomRecipeId = recipeId;
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Edit", "ItemBatches", batch.Id,
            $"متغير التشغيلة {batch.BatchNumber}: {before} ← {recipe?.Name ?? "أساسي"} — {reason.Trim()}");
        return FinanceOperationResult.Ok();
    }
}
