using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

public record StockLineInput(int ItemId, int? BatchId, decimal Quantity);

/// <summary>
/// من يحق له أي تشغيلة من المنتج التام (مرآة dbo.fn_Stock_BatchPriority في 09_sales_logic.sql):
/// متغير محدد ← تشغيلاته فقط (والمحجوز لعميل آخر يحتاج <see cref="AllowReserved"/>)؛
/// غير محدد ← محجوز العميل نفسه، ثم الأساسي، ثم المحجوز لغيره بالصلاحية؛ والمناسبات تُطلب بالاسم.
/// </summary>
public sealed record BatchScope(int? RecipeId, int? CustomerId, bool AllowReserved)
{
    /// <summary>نقل متغير بعينه (أو الأساسي فقط إن كان فارغًا) بين المخازن، كتحميل السيارة.</summary>
    public static BatchScope Variant(int? recipeId) => new(recipeId, null, recipeId is not null);

    public static BatchScope ForCustomer(int? customerId, int? recipeId, bool allowReserved) => new(recipeId, customerId, allowReserved);

    /// <summary>ترتيب الصرف (الأصغر أولًا)، أو NULL إن لم يحق.</summary>
    public int? Priority(int? batchRecipe, int? owner)
    {
        if (RecipeId is int wanted)
            return batchRecipe != wanted ? null : owner is null || owner == CustomerId || AllowReserved ? 0 : null;
        if (batchRecipe is null) return 1;
        if (owner is not null && owner == CustomerId) return 0;
        if (owner is null) return null;
        return AllowReserved ? 2 : null;
    }
}

/// <summary>أدوات مشتركة لخدمات المندوبين والإنتاج: الصرف من المخزون بترتيب الصلاحية، والقيود عبر العقل المالي.</summary>
internal static class LedgerHelper
{
    /// <summary>
    /// يوزّع كمية صادرة على التشغيلات المتاحة بترتيب الأقرب انتهاءً (أو من تشغيلة محددة)،
    /// ويرفض أي خصم يجعل الرصيد سالبًا. <paramref name="scope"/> يقصر الصرف على ما يحق من متغيرات المنتج التام
    /// (NULL = أي تشغيلة، للحركات الداخلية).
    /// </summary>
    public static async Task<(List<(int? batchId, decimal qty)> allocation, string? error)> AllocateAsync(
        ProjectDbContext db, int itemId, int warehouseId, int? batchId, decimal quantity, BatchScope? scope = null)
    {
        if (quantity <= 0) return (new(), "الكمية يجب أن تكون أكبر من صفر");

        var rows = await db.StockTransactions
            .Where(t => t.ItemId == itemId && t.WarehouseId == warehouseId && (batchId == null || t.BatchId == batchId))
            .GroupBy(t => new
            {
                t.BatchId,
                Expiry = t.Batch != null ? t.Batch.ExpiryDate : null,
                Recipe = t.Batch != null ? t.Batch.CustomRecipeId : null,
                Owner = t.Batch != null && t.Batch.CustomRecipe != null ? t.Batch.CustomRecipe.CustomerId : null,
                RecipeName = t.Batch != null && t.Batch.CustomRecipe != null ? t.Batch.CustomRecipe.Name : null
            })
            .Select(g => new { g.Key.BatchId, g.Key.Expiry, g.Key.Recipe, g.Key.Owner, g.Key.RecipeName, Qty = g.Sum(t => t.QuantityBaseUnits) })
            .ToListAsync();
        // تشغيلة محددة بالاسم = طلب متغيرها نفسه
        var effective = scope is not null && batchId is not null ? scope with { RecipeId = scope.RecipeId ?? rows.FirstOrDefault()?.Recipe } : scope;
        var all = rows.Select(x => new { x.BatchId, x.Expiry, x.Qty, x.RecipeName, Priority = effective is null ? 0 : effective.Priority(x.Recipe, x.Owner) })
                      .ToList();
        var allowed = all.Where(x => x.Priority is not null).ToList();
        var balances = allowed.Where(x => x.Qty > 0).ToList();

        // المتاح الفعلي = صافي الرصيد (يشمل أي رصيد سالب بلا تشغيلة من تسويات قديمة)، ولا يتجاوز مجموع التشغيلات الموجبة
        var available = Math.Min(balances.Sum(b => b.Qty), allowed.Sum(b => b.Qty));
        if (available < quantity)
        {
            var name = await db.Items.Where(i => i.Id == itemId).Select(i => i.ItemName).FirstAsync();
            var hint = all.Where(x => x.Priority is null && x.Qty > 0 && x.RecipeName != null).GroupBy(x => x.RecipeName)
                          .Select(g => $"{g.Key} {g.Sum(x => x.Qty):0.###}").ToList();
            var variant = effective?.RecipeId is int rid ? await db.CustomRecipes.Where(r => r.Id == rid).Select(r => r.Name).FirstOrDefaultAsync() : null;
            return (new(), $"الرصيد غير كافٍ للصنف \"{name}\"{(variant is null ? "" : $" ({variant})")}: المطلوب {quantity:0.###}، المتاح {available:0.###}"
                           + (hint.Count > 0 ? $". ويوجد رصيد متغيرات لا يُصرف هنا: {string.Join("، ", hint)} قطعة" : ""));
        }

        var result = new List<(int?, decimal)>();
        var remaining = quantity;
        foreach (var b in balances.OrderBy(b => b.Priority).ThenBy(b => b.Expiry is null).ThenBy(b => b.Expiry).ThenBy(b => b.BatchId))
        {
            if (remaining <= 0) break;
            var take = Math.Min(remaining, b.Qty);
            result.Add((b.BatchId, take));
            remaining -= take;
        }
        return (result, null);
    }

    /// <summary>ما يحق صرفه ضمن النطاق: صافي رصيد التشغيلات المسموح بها.</summary>
    public static async Task<decimal> AvailableAsync(ProjectDbContext db, int itemId, int warehouseId, int? batchId, BatchScope scope)
    {
        var rows = await db.StockTransactions
            .Where(t => t.ItemId == itemId && t.WarehouseId == warehouseId && (batchId == null || t.BatchId == batchId))
            .GroupBy(t => new { t.BatchId, Recipe = t.Batch != null ? t.Batch.CustomRecipeId : null,
                                Owner = t.Batch != null && t.Batch.CustomRecipe != null ? t.Batch.CustomRecipe.CustomerId : null })
            .Select(g => new { g.Key.Recipe, g.Key.Owner, Qty = g.Sum(t => t.QuantityBaseUnits) })
            .ToListAsync();
        var effective = batchId is not null ? scope with { RecipeId = scope.RecipeId ?? rows.FirstOrDefault()?.Recipe } : scope;
        var allowed = rows.Where(r => effective.Priority(r.Recipe, r.Owner) is not null).ToList();
        return Math.Max(0, Math.Min(allowed.Where(r => r.Qty > 0).Sum(r => r.Qty), allowed.Sum(r => r.Qty)));
    }

    public static async Task<decimal> BalanceAsync(ProjectDbContext db, int itemId, int warehouseId, int? batchId = null) =>
        await db.StockTransactions.Where(t => t.ItemId == itemId && t.WarehouseId == warehouseId && (batchId == null || t.BatchId == batchId))
                                  .SumAsync(t => (decimal?)t.QuantityBaseUnits) ?? 0;

    /// <summary>ينشئ قيدًا متوازنًا مرحّلًا بحسابَي قاعدة الربط المحددة. يعيد null مع رسالة إن لم تُعرَّف القاعدة.</summary>
    public static async Task<(JournalEntry? entry, string? error)> PostJournalAsync(
        ProjectDbContext db, string rule, decimal amount, DateTime date, JournalEntryType type,
        string description, int userId, string sourceTable, int? sourceId, string prefix)
    {
        var mapping = await db.AccountMappingRules.FirstOrDefaultAsync(r => r.TransactionType == rule);
        if (mapping is null)
            return (null, $"قاعدة الربط المحاسبي \"{rule}\" غير معرّفة. أضفها من المالية ← العقل المالي.");

        var count = await db.JournalEntries.CountAsync();
        var entry = new JournalEntry
        {
            EntryNumber = $"{prefix}-{count + 1:D5}",
            EntryDate = date.Date,
            EntryType = type,
            Description = description,
            CreatedByUserId = userId,
            IsPosted = true,
            SourceTable = sourceTable,
            SourceId = sourceId
        };
        entry.Lines.Add(new JournalEntryLine { AccountId = mapping.DebitAccountId, Debit = amount, Description = description });
        entry.Lines.Add(new JournalEntryLine { AccountId = mapping.CreditAccountId, Credit = amount, Description = description });
        db.JournalEntries.Add(entry);
        return (entry, null);
    }
}
