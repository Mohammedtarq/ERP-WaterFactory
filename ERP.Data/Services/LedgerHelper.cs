using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

public record StockLineInput(int ItemId, int? BatchId, decimal Quantity);

/// <summary>أدوات مشتركة لخدمات المندوبين والإنتاج: الصرف من المخزون بترتيب الصلاحية، والقيود عبر العقل المالي.</summary>
internal static class LedgerHelper
{
    /// <summary>
    /// يوزّع كمية صادرة على التشغيلات المتاحة بترتيب الأقرب انتهاءً (أو من تشغيلة محددة)،
    /// ويرفض أي خصم يجعل الرصيد سالبًا.
    /// </summary>
    public static async Task<(List<(int? batchId, decimal qty)> allocation, string? error)> AllocateAsync(
        ProjectDbContext db, int itemId, int warehouseId, int? batchId, decimal quantity)
    {
        if (quantity <= 0) return (new(), "الكمية يجب أن تكون أكبر من صفر");

        var balances = await db.StockTransactions
            .Where(t => t.ItemId == itemId && t.WarehouseId == warehouseId && (batchId == null || t.BatchId == batchId))
            .GroupBy(t => new { t.BatchId, Expiry = t.Batch != null ? t.Batch.ExpiryDate : null })
            .Select(g => new { g.Key.BatchId, g.Key.Expiry, Qty = g.Sum(t => t.QuantityBaseUnits) })
            .Where(x => x.Qty > 0)
            .ToListAsync();

        var available = balances.Sum(b => b.Qty);
        if (available < quantity)
        {
            var name = await db.Items.Where(i => i.Id == itemId).Select(i => i.ItemName).FirstAsync();
            return (new(), $"الرصيد غير كافٍ للصنف \"{name}\": المطلوب {quantity:0.###}، المتاح {available:0.###}");
        }

        var result = new List<(int?, decimal)>();
        var remaining = quantity;
        foreach (var b in balances.OrderBy(b => b.Expiry is null).ThenBy(b => b.Expiry).ThenBy(b => b.BatchId))
        {
            if (remaining <= 0) break;
            var take = Math.Min(remaining, b.Qty);
            result.Add((b.BatchId, take));
            remaining -= take;
        }
        return (result, null);
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
