using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

public enum InvoiceSettlementStatus { Unpaid, PartiallyPaid, Paid }

/// <summary>فاتورة في كشف العميل: القيمة، المدفوع التراكمي، المتبقي، والحالة.</summary>
public class CustomerInvoiceRow
{
    public int InvoiceId { get; init; }
    public string InvoiceNumber { get; init; } = "";
    public DateTime InvoiceDate { get; init; }
    public decimal Total { get; init; }
    public decimal Paid { get; init; }
    public decimal Remaining => Total - Paid;
    public InvoiceSettlementStatus Status => Paid >= Total ? InvoiceSettlementStatus.Paid : Paid > 0 ? InvoiceSettlementStatus.PartiallyPaid : InvoiceSettlementStatus.Unpaid;
    public string StatusText => Status switch
    {
        InvoiceSettlementStatus.Paid => "مسددة",
        InvoiceSettlementStatus.PartiallyPaid => "مسددة جزئيًا",
        _ => "غير مسددة"
    };
}

/// <summary>دفعة (سند قبض) في كشف العميل وتوزيعها على الفواتير.</summary>
public class CustomerPaymentRow
{
    public int VoucherId { get; init; }
    public string VoucherNumber { get; init; } = "";
    public DateTime Date { get; init; }
    public decimal Amount { get; init; }
    public decimal Allocated { get; init; }
    public decimal Unallocated => Amount - Allocated;
    public bool HasManual { get; init; }
    /// <summary>مثال: INV-00001: 25 · INV-00002: 5 (يدوي)</summary>
    public string AllocationText { get; init; } = "";
}

/// <summary>
/// حساب العميل: توزيع الدفعات على الفواتير الأقدم أولًا (FIFO)، مع إمكانية التوزيع اليدوي.
/// كل فاتورة تحفظ المدفوع التراكمي والمتبقي، وكل دفعة لها سجل توزيع. دين العميل الفرعي مستقل عن وكيله.
/// مثال مرجعي: فاتورتان 25 و25 ودفعة 30 ← الأولى مسددة بالكامل، والثانية 5 (المتبقي 20).
/// </summary>
public class CustomerAccountService
{
    private readonly ProjectDbContext _db;

    public CustomerAccountService(ProjectDbContext db) => _db = db;

    private IQueryable<SalesInvoice> Invoices(int customerId) =>
        _db.SalesInvoices.Where(i => i.CustomerId == customerId && i.Status == DocumentStatus.Posted && !i.IsFreeSale);

    private IQueryable<Voucher> Receipts(int customerId) =>
        _db.Vouchers.Where(v => v.PartyType == VoucherPartyType.Customer && v.PartyId == customerId && v.VoucherType == VoucherType.Receipt);

    /// <summary>
    /// يعيد حساب التوزيع التلقائي للعميل: تُحذف التوزيعات التلقائية وتبقى اليدوية، ثم يُوزَّع المتبقي من كل سند
    /// (بترتيب تاريخه) على الفواتير الأقدم أولًا، ويُحدَّث المدفوع التراكمي لكل فاتورة. آمن للتكرار.
    /// </summary>
    public async Task SyncAsync(int customerId)
    {
        var invoices = await Invoices(customerId).OrderBy(i => i.InvoiceDate).ThenBy(i => i.Id).ToListAsync();
        var receipts = await Receipts(customerId).AsNoTracking().OrderBy(v => v.VoucherDate).ThenBy(v => v.Id).ToListAsync();
        var invoiceIds = invoices.Select(i => i.Id).ToList();
        var receiptIds = receipts.Select(v => v.Id).ToList();
        var existing = await _db.PaymentAllocations.Where(a => receiptIds.Contains(a.VoucherId) || invoiceIds.Contains(a.SalesInvoiceId)).ToListAsync();
        _db.PaymentAllocations.RemoveRange(existing.Where(a => !a.IsManual));
        var manual = existing.Where(a => a.IsManual).ToList();

        // المدفوع على كل فاتورة: المدفوع عند البيع + التوزيعات اليدوية
        var paid = invoices.ToDictionary(i => i.Id, i => Math.Min(i.AmountPaidNow, i.TotalAmount) + manual.Where(a => a.SalesInvoiceId == i.Id).Sum(a => a.Amount));
        foreach (var v in receipts)
        {
            var available = v.Amount - manual.Where(a => a.VoucherId == v.Id).Sum(a => a.Amount);
            foreach (var inv in invoices)
            {
                if (available <= 0) break;
                var open = inv.TotalAmount - paid[inv.Id];
                if (open <= 0) continue;
                var take = Math.Min(open, available);
                _db.PaymentAllocations.Add(new PaymentAllocation { VoucherId = v.Id, SalesInvoiceId = inv.Id, Amount = take });
                paid[inv.Id] += take;
                available -= take;
            }
        }
        foreach (var inv in invoices) inv.AmountSettled = paid[inv.Id];
        await _db.SaveChangesAsync();
    }

    /// <summary>توزيع يدوي لجزء من سند قبض على فاتورة محددة (يتقدم على التوزيع التلقائي).</summary>
    public async Task<FinanceOperationResult> AllocateManualAsync(int voucherId, int invoiceId, decimal amount, int userId)
    {
        if (amount <= 0) return FinanceOperationResult.Fail("المبلغ يجب أن يكون أكبر من صفر");
        var voucher = await _db.Vouchers.AsNoTracking().FirstOrDefaultAsync(v => v.Id == voucherId);
        if (voucher is null || voucher.PartyType != VoucherPartyType.Customer || voucher.VoucherType != VoucherType.Receipt || voucher.PartyId is null)
            return FinanceOperationResult.Fail("اختر سند قبض لعميل");
        var invoice = await _db.SalesInvoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId);
        if (invoice is null || invoice.Status != DocumentStatus.Posted || invoice.IsFreeSale)
            return FinanceOperationResult.Fail("اختر فاتورة مرحّلة");
        if (invoice.CustomerId != voucher.PartyId)
            return FinanceOperationResult.Fail("الفاتورة لعميل آخر — دين كل عميل (ومنهم العملاء الفرعيون) مستقل");

        var manual = await _db.PaymentAllocations.Where(a => a.IsManual && (a.VoucherId == voucherId || a.SalesInvoiceId == invoiceId)).ToListAsync();
        var voucherFree = voucher.Amount - manual.Where(a => a.VoucherId == voucherId).Sum(a => a.Amount);
        if (amount > voucherFree) return FinanceOperationResult.Fail($"المتاح يدويًا من السند {voucherFree:N0} فقط");
        var invoiceOpen = invoice.TotalAmount - Math.Min(invoice.AmountPaidNow, invoice.TotalAmount) - manual.Where(a => a.SalesInvoiceId == invoiceId).Sum(a => a.Amount);
        if (amount > invoiceOpen) return FinanceOperationResult.Fail($"المتبقي على الفاتورة (قبل التوزيع التلقائي) {invoiceOpen:N0} فقط");

        _db.PaymentAllocations.Add(new PaymentAllocation { VoucherId = voucherId, SalesInvoiceId = invoiceId, Amount = amount, IsManual = true, AllocatedByUserId = userId });
        await _db.SaveChangesAsync();
        await SyncAsync(voucher.PartyId.Value);
        return FinanceOperationResult.Ok();
    }

    /// <summary>إلغاء التوزيع اليدوي لسند ← يعود توزيعه تلقائيًا بالأقدم أولًا.</summary>
    public async Task<FinanceOperationResult> ResetToAutomaticAsync(int voucherId)
    {
        var voucher = await _db.Vouchers.AsNoTracking().FirstOrDefaultAsync(v => v.Id == voucherId);
        if (voucher?.PartyId is null || voucher.PartyType != VoucherPartyType.Customer) return FinanceOperationResult.Fail("اختر سند قبض لعميل");
        await _db.PaymentAllocations.Where(a => a.VoucherId == voucherId && a.IsManual).ExecuteDeleteAsync();
        await SyncAsync(voucher.PartyId.Value);
        return FinanceOperationResult.Ok();
    }

    /// <summary>فواتير العميل بالمدفوع والمتبقي والحالة (الأقدم أولًا).</summary>
    public async Task<List<CustomerInvoiceRow>> GetInvoicesAsync(int customerId) =>
        await Invoices(customerId).AsNoTracking().OrderBy(i => i.InvoiceDate).ThenBy(i => i.Id)
            .Select(i => new CustomerInvoiceRow { InvoiceId = i.Id, InvoiceNumber = i.InvoiceNumber, InvoiceDate = i.InvoiceDate, Total = i.TotalAmount, Paid = i.AmountSettled })
            .ToListAsync();

    /// <summary>دفعات العميل وتوزيع كل منها.</summary>
    public async Task<List<CustomerPaymentRow>> GetPaymentsAsync(int customerId)
    {
        var receipts = await Receipts(customerId).AsNoTracking().OrderBy(v => v.VoucherDate).ThenBy(v => v.Id).ToListAsync();
        var ids = receipts.Select(v => v.Id).ToList();
        var allocations = await _db.PaymentAllocations.AsNoTracking().Where(a => ids.Contains(a.VoucherId))
            .Select(a => new { a.VoucherId, a.Amount, a.IsManual, a.SalesInvoice.InvoiceNumber, a.SalesInvoice.InvoiceDate, a.SalesInvoiceId })
            .ToListAsync();
        return receipts.Select(v =>
        {
            var mine = allocations.Where(a => a.VoucherId == v.Id).OrderBy(a => a.InvoiceDate).ThenBy(a => a.SalesInvoiceId).ToList();
            return new CustomerPaymentRow
            {
                VoucherId = v.Id, VoucherNumber = v.VoucherNumber, Date = v.VoucherDate, Amount = v.Amount,
                Allocated = mine.Sum(a => a.Amount), HasManual = mine.Any(a => a.IsManual),
                AllocationText = string.Join(" · ", mine.Select(a => $"{a.InvoiceNumber}: {a.Amount:N0}" + (a.IsManual ? " (يدوي)" : "")))
            };
        }).ToList();
    }
}
