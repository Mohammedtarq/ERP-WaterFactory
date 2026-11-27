using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

public enum StockAdjustmentKind
{
    Damaged,    // تالف: خصم من الرصيد مع ذكر سبب التلف
    Disposal,   // إتلاف: خصم نهائي (عادة من مخزن التالف)
    Return      // إرجاع للمخزن: إضافة للرصيد
}

public class InventoryService
{
    private readonly ProjectDbContext _db;

    public InventoryService(ProjectDbContext db)
    {
        _db = db;
    }

    public Task<decimal> GetBalanceAsync(int itemId, int warehouseId, int? batchId = null)
        => _db.StockTransactions
            .Where(t => t.ItemId == itemId && t.WarehouseId == warehouseId && (batchId == null || t.BatchId == batchId))
            .SumAsync(t => t.QuantityBaseUnits);

    /// <summary>
    /// تسوية مخزون يدوية. الكمية دائمًا موجبة بالقطعة؛ الاتجاه يُحدَّد من نوع التسوية.
    /// لا يُسمح بخصم يجعل الرصيد سالبًا.
    /// </summary>
    public async Task<FinanceOperationResult> AdjustAsync(
        StockAdjustmentKind kind, int itemId, int warehouseId, int? batchId, decimal quantityBaseUnits,
        DamageReason? damageReason, string? notes, int userId)
    {
        if (quantityBaseUnits <= 0)
            return FinanceOperationResult.Fail("الكمية يجب أن تكون أكبر من صفر");
        if (kind == StockAdjustmentKind.Damaged && damageReason is null)
            return FinanceOperationResult.Fail("حدّد سبب التلف (نقل / مخزن / إنتاج)");

        bool isOut = kind != StockAdjustmentKind.Return;
        if (isOut)
        {
            var balance = await GetBalanceAsync(itemId, warehouseId, batchId);
            if (balance < quantityBaseUnits)
                return FinanceOperationResult.Fail($"الرصيد غير كافٍ: المتاح {balance:0.###} قطعة فقط");
        }

        _db.StockTransactions.Add(new StockTransaction
        {
            ItemId = itemId,
            WarehouseId = warehouseId,
            BatchId = batchId,
            QuantityBaseUnits = isOut ? -quantityBaseUnits : quantityBaseUnits,
            TransactionType = kind == StockAdjustmentKind.Return
                ? StockTransactionType.ReturnToWarehouse
                : StockTransactionType.Damaged,
            DamageReason = kind == StockAdjustmentKind.Damaged ? damageReason : null,
            FreeIssueRecipient = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            ReferenceTable = "StockAdjustment",
            CreatedByUserId = userId
        });
        await _db.SaveChangesAsync();
        return FinanceOperationResult.Ok();
    }
}
