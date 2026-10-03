using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

public record PurchaseOrderLineInput(int ItemId, decimal QuantityOrdered, decimal ExpectedUnitCost);
public record GoodsReceiptLineInput(int ItemId, int? PurchaseOrderLineId, decimal QuantityReceived,
    decimal UnitCost, string BatchNumber, DateTime? ExpiryDate);

public class SupplierPurchasingService
{
    private readonly ProjectDbContext _db;
    private readonly FinanceService _finance;

    public SupplierPurchasingService(ProjectDbContext db)
    {
        _db = db;
        _finance = new FinanceService(db);
    }

    /// <summary>
    /// ينشئ أمر الشراء بلا أي أثر محاسبي، إلا إذا طُلبت دفعة مقدمة — عندها
    /// يُصدر سند صرف فورًا (يحتاج قاعدة ربط "SupplierAdvancePayment" جاهزة).
    /// </summary>
    public async Task<FinanceOperationResult> CreatePurchaseOrderAsync(
        int supplierId, int warehouseId, DateTime orderDate, DateTime? expectedDeliveryDate,
        SupplierPaymentTerms paymentTerms, decimal advanceAmount,
        List<PurchaseOrderLineInput> lines, int createdByUserId)
    {
        if (lines.Count == 0)
            return FinanceOperationResult.Fail("أضف صنفًا واحدًا على الأقل لأمر الشراء");

        var po = new PurchaseOrder
        {
            PONumber = await GenerateNumberAsync("PO", () => _db.PurchaseOrders.CountAsync()),
            SupplierId = supplierId,
            WarehouseId = warehouseId,
            OrderDate = orderDate,
            ExpectedDeliveryDate = expectedDeliveryDate,
            Status = PurchaseOrderStatus.Sent,
            PaymentTerms = paymentTerms,
            AdvanceAmount = advanceAmount,
            CreatedByUserId = createdByUserId
        };
        foreach (var l in lines)
        {
            po.Lines.Add(new PurchaseOrderLine
            {
                ItemId = l.ItemId,
                QuantityOrdered = l.QuantityOrdered,
                ExpectedUnitCost = l.ExpectedUnitCost
            });
        }
        _db.PurchaseOrders.Add(po);
        await _db.SaveChangesAsync();

        if (paymentTerms == SupplierPaymentTerms.AdvancePlusCredit && advanceAmount > 0)
        {
            var voucherResult = await _finance.CreateVoucherAsync(
                VoucherType.Payment, VoucherPartyType.Supplier, supplierId, advanceAmount,
                PaymentMethod.Cash, orderDate, "SupplierAdvancePayment", createdByUserId,
                $"دفعة مقدمة لأمر الشراء {po.PONumber}");

            if (!voucherResult.Success)
                return FinanceOperationResult.Fail(
                    $"تم إنشاء أمر الشراء {po.PONumber} لكن تعذّر إصدار سند الدفعة المقدمة: {voucherResult.ErrorMessage}");

            var voucher = await _db.Vouchers.OrderByDescending(v => v.Id).FirstAsync();
            po.AdvanceVoucherId = voucher.Id;
            await _db.SaveChangesAsync();
        }

        return FinanceOperationResult.Ok();
    }

    /// <summary>
    /// استلام بضاعة مرتبط بأمر شراء: يُحدّث الكميات المستلمة تراكميًا، يضبط حالة
    /// أمر الشراء تلقائيًا (مستلم جزئيًا/مكتمل)، وينشئ القيد المحاسبي — مع تسوية
    /// أي دفعة مقدمة تلقائيًا فور اكتمال الاستلام بالكامل لأول مرة.
    /// </summary>
    public async Task<FinanceOperationResult> ReceiveGoodsAsync(
        int purchaseOrderId, DateTime receiptDate, string? supplierInvoiceNumber,
        List<GoodsReceiptLineInput> lines, int createdByUserId)
    {
        var po = await _db.PurchaseOrders
            .Include(p => p.Lines)
            .FirstOrDefaultAsync(p => p.Id == purchaseOrderId);

        if (po is null) return FinanceOperationResult.Fail("أمر الشراء غير موجود");
        if (lines.Count == 0) return FinanceOperationResult.Fail("أدخل كمية استلام واحدة على الأقل");

        bool wasAlreadyCompleted = po.Status == PurchaseOrderStatus.Completed;

        var receipt = new GoodsReceipt
        {
            ReceiptNumber = await GenerateNumberAsync("GR", () => _db.GoodsReceipts.CountAsync()),
            PurchaseOrderId = po.Id,
            SupplierId = po.SupplierId,
            WarehouseId = po.WarehouseId,
            ReceiptDate = receiptDate,
            SupplierInvoiceNumber = supplierInvoiceNumber,
            Status = DocumentStatus.Posted,
            CreatedByUserId = createdByUserId
        };

        decimal totalAmount = 0;

        foreach (var l in lines)
        {
            var batch = await _db.ItemBatches.FirstOrDefaultAsync(b => b.ItemId == l.ItemId && b.BatchNumber == l.BatchNumber)
                        ?? new ItemBatch { ItemId = l.ItemId, BatchNumber = l.BatchNumber, ExpiryDate = l.ExpiryDate };
            if (batch.Id == 0)
            {
                _db.ItemBatches.Add(batch);
                await _db.SaveChangesAsync();
            }

            receipt.Lines.Add(new GoodsReceiptLine
            {
                ItemId = l.ItemId,
                PurchaseOrderLineId = l.PurchaseOrderLineId,
                QuantityReceived = l.QuantityReceived,
                UnitCost = l.UnitCost,
                BatchId = batch.Id
            });

            _db.StockTransactions.Add(new StockTransaction
            {
                ItemId = l.ItemId,
                WarehouseId = po.WarehouseId,
                BatchId = batch.Id,
                QuantityBaseUnits = l.QuantityReceived,
                TransactionType = StockTransactionType.Receipt,
                ReferenceTable = nameof(GoodsReceipt),
                CreatedByUserId = createdByUserId
            });

            var poLine = po.Lines.FirstOrDefault(x => x.Id == l.PurchaseOrderLineId);
            if (poLine is not null) poLine.QuantityReceived += l.QuantityReceived;

            totalAmount += l.QuantityReceived * l.UnitCost;

            // سعر الكلفة = آخر سعر شراء (تقييم المخزون في المطابقة)
            if (l.UnitCost > 0 && await _db.Items.FindAsync(l.ItemId) is { } item) item.CostPrice = l.UnitCost;
        }

        po.Status = po.Lines.All(x => x.QuantityReceived >= x.QuantityOrdered)
            ? PurchaseOrderStatus.Completed
            : PurchaseOrderStatus.PartiallyReceived;

        _db.GoodsReceipts.Add(receipt);
        await _db.SaveChangesAsync();

        var mainEntryResult = await _finance.PostGoodsReceiptEntryAsync(
            totalAmount, receiptDate, createdByUserId, receipt.ReceiptNumber);

        if (!mainEntryResult.result.Success)
            return FinanceOperationResult.Fail(
                $"تم تسجيل الاستلام فعليًا في المخزون، لكن تعذّر إنشاء القيد المحاسبي: {mainEntryResult.result.ErrorMessage}");

        receipt.JournalEntryId = mainEntryResult.journalEntryId;
        await _db.SaveChangesAsync();

        bool justCompleted = po.Status == PurchaseOrderStatus.Completed && !wasAlreadyCompleted;
        if (justCompleted && po.AdvanceAmount > 0)
        {
            var offsetResult = await _finance.PostSupplierAdvanceOffsetAsync(po.AdvanceAmount, receiptDate, createdByUserId);
            if (!offsetResult.Success)
                return FinanceOperationResult.Fail(
                    $"تم الاستلام والترحيل بنجاح، لكن تعذّرت تسوية الدفعة المقدمة تلقائيًا: {offsetResult.ErrorMessage}. سوّها يدويًا من القيود.");
        }

        return FinanceOperationResult.Ok();
    }

    private async Task<string> GenerateNumberAsync(string prefix, Func<Task<int>> countAsync)
    {
        int count = await countAsync();
        return $"{prefix}-{(count + 1):D5}";
    }
}
