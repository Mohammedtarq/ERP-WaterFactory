using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

public record PurchaseOrderLineInput(int ItemId, decimal QuantityOrdered, decimal ExpectedUnitCost);
/// <param name="LineTotal">قيمة السطر كما في فاتورة المورد (إن وُجدت) — تمنع فروق التقريب بين كلفة القطعة والإجمالي.</param>
public record GoodsReceiptLineInput(int ItemId, int? PurchaseOrderLineId, decimal QuantityReceived,
    decimal UnitCost, string BatchNumber, DateTime? ExpiryDate,
    decimal? LineTotal = null, string? PurchaseUnit = null, decimal? PurchaseQuantity = null, decimal? PurchaseUnitPrice = null);

/// <summary>وحدة شراء متاحة لصنف: من هيكلية التعبئة (كرتون، باليت...) أو بالوزن (كغم، طن) إن عُرف وزن القطعة.</summary>
public record PurchaseUnitOption(string Label, decimal PiecesPerUnit)
{
    public override string ToString() => PiecesPerUnit == 1 ? Label : $"{Label} ({PiecesPerUnit:#,0.###} قطعة)";
}

/// <param name="UnitLabel">وحدة الشراء كما في فاتورة المورد.</param>
/// <param name="PiecesPerUnit">معامل التحويل إلى القطعة (الوحدة الأساسية في المخزن).</param>
public record PurchaseInvoiceLineInput(int ItemId, string UnitLabel, decimal PiecesPerUnit, decimal Quantity, decimal UnitPrice,
    string? BatchNumber = null, DateTime? ExpiryDate = null)
{
    public decimal Pieces => Math.Round(Quantity * PiecesPerUnit, 3);
    public decimal LineTotal => Math.Round(Quantity * UnitPrice, 2);
}

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
        var pendingStock = new List<StockTransaction>();

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
                BatchId = batch.Id,
                PurchaseUnit = l.PurchaseUnit,
                PurchaseQuantity = l.PurchaseQuantity,
                PurchaseUnitPrice = l.PurchaseUnitPrice
            });

            // الكلفة على الحركة نفسها: مشغّل الكلفة يعيد حساب متوسط الصنف المرجّح (28_costing_purchasing.sql)
            pendingStock.Add(new StockTransaction
            {
                ItemId = l.ItemId,
                WarehouseId = po.WarehouseId,
                BatchId = batch.Id,
                QuantityBaseUnits = l.QuantityReceived,
                UnitCost = l.UnitCost,
                TransactionType = StockTransactionType.Receipt,
                ReferenceTable = nameof(GoodsReceipt),
                CreatedByUserId = createdByUserId
            });

            var poLine = po.Lines.FirstOrDefault(x => x.Id == l.PurchaseOrderLineId);
            if (poLine is not null) poLine.QuantityReceived += l.QuantityReceived;

            totalAmount += l.LineTotal ?? Math.Round(l.QuantityReceived * l.UnitCost, 2);
        }

        po.Status = po.Lines.All(x => x.QuantityReceived >= x.QuantityOrdered)
            ? PurchaseOrderStatus.Completed
            : PurchaseOrderStatus.PartiallyReceived;

        _db.GoodsReceipts.Add(receipt);
        await _db.SaveChangesAsync();
        foreach (var t in pendingStock) t.ReferenceId = receipt.Id;
        _db.StockTransactions.AddRange(pendingStock);
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

    /// <summary>وحدات الشراء الممكنة للصنف: القطعة، ومستويات التعبئة، والكغم والطن إن كان وزن القطعة معروفًا.</summary>
    public async Task<List<PurchaseUnitOption>> GetPurchaseUnitsAsync(int itemId)
    {
        var item = await _db.Items.AsNoTracking().FirstAsync(i => i.Id == itemId);
        var levels = await _db.ItemPackagingLevels.AsNoTracking().Where(l => l.ItemId == itemId).OrderBy(l => l.EquivalentBaseUnits).ToListAsync();
        var list = new List<PurchaseUnitOption>();
        foreach (var l in levels)
            if (list.All(x => x.Label != l.LevelName)) list.Add(new PurchaseUnitOption(l.LevelName, l.EquivalentBaseUnits));
        if (list.Count == 0) list.Add(new PurchaseUnitOption(item.BaseUnitName, 1));
        if (item.UnitWeightGrams is decimal g && g > 0)
        {
            list.Add(new PurchaseUnitOption("كغم", Math.Round(1000m / g, 3)));
            list.Add(new PurchaseUnitOption("طن", Math.Round(1_000_000m / g, 3)));
        }
        return list;
    }

    /// <summary>
    /// فاتورة شراء مباشرة (الوضع الافتراضي، بلا أمر شراء مسبق): الصنف بوحدة الشراء ومعامل التحويل ←
    /// دخول المخزن بالقطع، وذمة المورد وقيدها، وتحديث متوسط الكلفة — كلها في خطوة واحدة.
    /// اختياريًا يُدفع جزء نقدًا الآن بسند صرف للمورد.
    /// </summary>
    public async Task<(FinanceOperationResult result, int? receiptId)> PurchaseInvoiceAsync(
        int supplierId, int warehouseId, DateTime invoiceDate, string? supplierInvoiceNumber,
        IReadOnlyList<PurchaseInvoiceLineInput> lines, decimal paidNow, int userId)
    {
        var valid = lines.Where(l => l.Quantity > 0).ToList();
        if (valid.Count == 0) return (FinanceOperationResult.Fail("أضف صنفًا واحدًا على الأقل بكمية أكبر من صفر"), null);
        if (valid.Any(l => l.PiecesPerUnit <= 0)) return (FinanceOperationResult.Fail("معامل التحويل إلى القطع يجب أن يكون أكبر من صفر"), null);
        if (valid.Any(l => l.UnitPrice < 0)) return (FinanceOperationResult.Fail("السعر لا يمكن أن يكون سالبًا"), null);
        var total = valid.Sum(l => l.LineTotal);
        if (paidNow < 0 || paidNow > total) return (FinanceOperationResult.Fail("المدفوع الآن بين صفر وإجمالي الفاتورة"), null);
        if (!await _db.Suppliers.AnyAsync(s => s.Id == supplierId)) return (FinanceOperationResult.Fail("اختر المورد"), null);

        var ownTx = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync() : null;
        try
        {
            var po = new PurchaseOrder
            {
                PONumber = await GenerateNumberAsync("PO", () => _db.PurchaseOrders.CountAsync()),
                SupplierId = supplierId, WarehouseId = warehouseId, OrderDate = invoiceDate.Date,
                Status = PurchaseOrderStatus.Sent, PaymentTerms = SupplierPaymentTerms.Credit, CreatedByUserId = userId
            };
            foreach (var l in valid)
                po.Lines.Add(new PurchaseOrderLine { ItemId = l.ItemId, QuantityOrdered = l.Pieces, ExpectedUnitCost = Math.Round(l.LineTotal / l.Pieces, 4) });
            _db.PurchaseOrders.Add(po);
            await _db.SaveChangesAsync();

            var receiptLines = valid.Select((l, i) => new GoodsReceiptLineInput(
                l.ItemId, po.Lines.ElementAt(i).Id, l.Pieces, Math.Round(l.LineTotal / l.Pieces, 4),
                string.IsNullOrWhiteSpace(l.BatchNumber) ? $"{invoiceDate:yyyyMMdd}-{po.PONumber}" : l.BatchNumber.Trim(), l.ExpiryDate,
                l.LineTotal, l.UnitLabel, l.Quantity, l.UnitPrice)).ToList();
            var received = await ReceiveGoodsAsync(po.Id, invoiceDate, supplierInvoiceNumber, receiptLines, userId);
            if (!received.Success) throw new PurchaseAbort(received.ErrorMessage!);
            var receiptId = await _db.GoodsReceipts.Where(g => g.PurchaseOrderId == po.Id).Select(g => g.Id).FirstAsync();

            if (paidNow > 0)
            {
                var paid = await _finance.CreateVoucherAsync(VoucherType.Payment, VoucherPartyType.Supplier, supplierId, paidNow, PaymentMethod.Cash,
                                                             invoiceDate, "SupplierPaymentVoucher", userId,
                                                             $"دفعة على فاتورة الشراء {supplierInvoiceNumber ?? po.PONumber}");
                if (!paid.Success) throw new PurchaseAbort(paid.ErrorMessage!);
            }
            if (ownTx is not null) await ownTx.CommitAsync();
            return (FinanceOperationResult.Ok(), receiptId);
        }
        catch (PurchaseAbort ex)
        {
            if (ownTx is not null) await ownTx.RollbackAsync();
            _db.ChangeTracker.Clear();
            return (FinanceOperationResult.Fail(ex.Message), null);
        }
        catch (DbUpdateException ex) when (FinanceService.BusinessError(ex) is string msg)
        {
            if (ownTx is not null) await ownTx.RollbackAsync();
            _db.ChangeTracker.Clear();
            return (FinanceOperationResult.Fail(msg), null);
        }
        finally
        {
            if (ownTx is not null) await ownTx.DisposeAsync();
        }
    }

    private sealed class PurchaseAbort(string message) : Exception(message);

    private async Task<string> GenerateNumberAsync(string prefix, Func<Task<int>> countAsync)
    {
        int count = await countAsync();
        return $"{prefix}-{(count + 1):D5}";
    }
}
