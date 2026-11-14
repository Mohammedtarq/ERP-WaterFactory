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
    /// <summary>الاسم الموحد: «كارتون 40×330 الرحمة».</summary>
    public string ProductName { get; init; } = "";
}

/// <summary>تشغيلة منتج تام برصيدها الحالي ومتغيرها.</summary>
public record VariantBatchRow(int BatchId, int ItemId, string ItemName, string BatchNumber, DateTime? ManufactureDate, int? RecipeId, string Variant, decimal Balance)
{
    /// <summary>الرصيد بالعبوات.</summary>
    public string BalanceText { get; init; } = "";
    /// <summary>الاسم الموحد: «كارتون 40×330 مطعم داون تاون».</summary>
    public string ProductName { get; init; } = "";
}

/// <summary>
/// تشغيلة افتتاحية مستوردة من نظام الرحمة (RH-اسم الملصق) بلا متغير، مع المتغير المقترح بمطابقة اسمها.
/// Exact = الاسم يطابق اسم المتغير الخاص (أو زبونه) تمامًا؛ وإلا اقتراح للمراجعة أو لا اقتراح.
/// </summary>
public record ImportedBatchRow(int BatchId, int ItemId, string BatchNumber, string Label, decimal Balance, int? SuggestedRecipeId, bool Exact)
{
    public string BalanceText { get; init; } = "";
    public string ProductName { get; init; } = "";
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

        var names = await ProductNames.LoadAsync(_db, itemIds);
        return rows.Select(r =>
        {
            var recipe = r.Recipe is int id ? recipes.GetValueOrDefault(id) : null;
            var lv = levels.GetValueOrDefault(r.ItemId);
            return new VariantSummaryRow(items[r.ItemId], r.Recipe, recipe?.Name ?? "أساسي", KindOf(recipe), r.Produced, Math.Round(r.Cost, 2), r.Sold, r.Balance,
                                         WarehouseDocumentService.Breakdown(r.Balance, lv))
            {
                ProducedText = WarehouseDocumentService.Breakdown(r.Produced, lv), SoldText = WarehouseDocumentService.Breakdown(r.Sold, lv),
                ProductName = names.Name(r.ItemId, r.Recipe)
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
        var names = await ProductNames.LoadAsync(_db, itemIds);
        return rows.Select(r => r with { BalanceText = WarehouseDocumentService.Breakdown(r.Balance, levels.GetValueOrDefault(r.ItemId)),
                                         ProductName = names.Name(r.ItemId, r.RecipeId) }).ToList();
    }

    /// <summary>بادئة تشغيلات الرصيد الافتتاحي في النقل من نظام الرحمة (RahmaImporter).</summary>
    public const string ImportedPrefix = "RH-";

    /// <summary>
    /// التشغيلات الافتتاحية المستوردة (RH-…) للمنتج التام التي ما زالت «أساسي» ولها رصيد، مع اقتراح المتغير:
    /// المتغير الخاص لنفس المنتج الذي اسمه الخاص (قبل « — ») أو اختصاره أو اسم زبونه يساوي اسم الملصق تمامًا،
    /// وإلا متغير يحتوي الاسم أو يحتويه (للمراجعة).
    /// </summary>
    public async Task<List<ImportedBatchRow>> ImportedBatchesAsync()
    {
        var batches = (await BatchesAsync()).Where(b => b.RecipeId is null && b.BatchNumber.StartsWith(ImportedPrefix, StringComparison.Ordinal)).ToList();
        var itemIds = batches.Select(b => b.ItemId).Distinct().ToList();
        var recipes = await _db.CustomRecipes.AsNoTracking().Where(r => itemIds.Contains(r.FinishedItemId))
            .Select(r => new { r.Id, r.FinishedItemId, r.Name, r.ShortName, Customer = r.Customer != null ? r.Customer.Name : null }).ToListAsync();
        static string Norm(string? v) => string.Join(' ', (v ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return batches.Select(b =>
        {
            var label = Norm(b.BatchNumber[ImportedPrefix.Length..]);
            // اسمان طويلان تطابقا بعد القص في النقل: «…-2»
            if (System.Text.RegularExpressions.Regex.Match(label, @"^(.*)-\d+$") is { Success: true } m) label = Norm(m.Groups[1].Value);
            var mine = recipes.Where(r => r.FinishedItemId == b.ItemId).ToList();
            var exact = mine.Where(r => Norm(r.Name.Split(" — ")[0]) == label || Norm(r.ShortName) == label || Norm(r.Customer) == label).ToList();
            var partial = exact.Count > 0 ? exact : mine.Where(r => label.Length >= 3 &&
                (Norm(r.Name).Contains(label, StringComparison.OrdinalIgnoreCase) || label.Contains(Norm(r.Name.Split(" — ")[0]), StringComparison.OrdinalIgnoreCase))).ToList();
            var pick = partial.Count == 1 ? partial[0].Id : (int?)null;
            return new ImportedBatchRow(b.BatchId, b.ItemId, b.BatchNumber, label, b.Balance, pick, exact.Count == 1)
                { BalanceText = b.BalanceText, ProductName = b.ProductName };
        }).OrderBy(r => r.ProductName).ThenBy(r => r.Label).ToList();
    }

    /// <summary>ربط تشغيلات مستوردة بمتغيراتها دفعة واحدة بسبب واحد — الكل أو لا شيء، وكل تغيير في سجل التدقيق.</summary>
    public async Task<FinanceOperationResult> ApplyImportedAsync(IReadOnlyCollection<(int BatchId, int RecipeId)> links, string reason, int userId)
    {
        if (links.Count == 0) return FinanceOperationResult.Fail("أشّر على تشغيلة واحدة على الأقل واختر متغيرها");
        await using var tx = await _db.Database.BeginTransactionAsync();
        foreach (var (batchId, recipeId) in links)
        {
            var r = await SetBatchVariantAsync(batchId, recipeId, reason, userId);
            if (!r.Success)
            {
                var number = await _db.ItemBatches.Where(b => b.Id == batchId).Select(b => b.BatchNumber).FirstOrDefaultAsync();
                return FinanceOperationResult.Fail($"{number}: {r.ErrorMessage}");
            }
        }
        await tx.CommitAsync();
        return FinanceOperationResult.Ok();
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
