using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

public class FinanceOperationResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }

    public static FinanceOperationResult Ok() => new() { Success = true };
    public static FinanceOperationResult Fail(string message) => new() { Success = false, ErrorMessage = message };
}

public class FinanceService
{
    private readonly ProjectDbContext _db;

    public FinanceService(ProjectDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// ترحيل قيد يدوي. يُرفض إن لم يتساوَ إجمالي المدين مع إجمالي الدائن —
    /// هذا هو القيد التقني الذي اتفقنا عليه عند تصميم شاشة القيد المحاسبي.
    /// </summary>
    public async Task<FinanceOperationResult> PostManualJournalEntryAsync(
        DateTime entryDate, string? description, int createdByUserId,
        List<(int accountId, decimal debit, decimal credit)> lines)
    {
        if (lines.Count < 2)
            return FinanceOperationResult.Fail("القيد يحتاج سطرين على الأقل");

        decimal totalDebit = lines.Sum(l => l.debit);
        decimal totalCredit = lines.Sum(l => l.credit);
        if (totalDebit != totalCredit)
            return FinanceOperationResult.Fail($"القيد غير متوازن: مدين {totalDebit} ≠ دائن {totalCredit}");

        var entry = new JournalEntry
        {
            EntryNumber = await GenerateNextNumberAsync("JV"),
            EntryDate = entryDate,
            EntryType = JournalEntryType.Manual,
            Description = description,
            CreatedByUserId = createdByUserId,
            IsPosted = true
        };

        foreach (var (accountId, debit, credit) in lines)
        {
            entry.Lines.Add(new JournalEntryLine
            {
                AccountId = accountId,
                Debit = debit,
                Credit = credit
            });
        }

        _db.JournalEntries.Add(entry);
        await _db.SaveChangesAsync();
        return FinanceOperationResult.Ok();
    }

    /// <summary>
    /// إنشاء سند قبض/صرف مبسّط. يبحث عن قاعدة ربط جاهزة (AccountMappingRules)
    /// بحسب نوع العملية، وينشئ القيد المحاسبي المقابل تلقائيًا — تطبيق مباشر
    /// لمبدأ "العقل المالي" الذي اتفقنا عليه في مرحلة التصميم.
    /// </summary>
    public async Task<FinanceOperationResult> CreateVoucherAsync(
        VoucherType voucherType, VoucherPartyType partyType, int? partyId,
        decimal amount, PaymentMethod paymentMethod, DateTime voucherDate,
        string mappingTransactionType, int createdByUserId, string? notes = null)
    {
        var mapping = await _db.AccountMappingRules
            .FirstOrDefaultAsync(r => r.TransactionType == mappingTransactionType);

        if (mapping is null)
        {
            return FinanceOperationResult.Fail(
                $"لا توجد قاعدة ربط محاسبي معرَّفة بعد لنوع العملية \"{mappingTransactionType}\". " +
                "أضفها من شاشة إعدادات العقل المالي أولًا، ثم أعد المحاولة.");
        }

        var entry = new JournalEntry
        {
            EntryNumber = await GenerateNextNumberAsync("JV"),
            EntryDate = voucherDate,
            EntryType = JournalEntryType.AutoVoucher,
            Description = $"سند {(voucherType == VoucherType.Receipt ? "قبض" : "صرف")} تلقائي",
            CreatedByUserId = createdByUserId,
            IsPosted = true
        };
        entry.Lines.Add(new JournalEntryLine { AccountId = mapping.DebitAccountId, Debit = amount, Credit = 0 });
        entry.Lines.Add(new JournalEntryLine { AccountId = mapping.CreditAccountId, Debit = 0, Credit = amount });
        _db.JournalEntries.Add(entry);
        await _db.SaveChangesAsync();

        var voucher = new Voucher
        {
            VoucherNumber = await GenerateNextNumberAsync(voucherType == VoucherType.Receipt ? "RV" : "PV"),
            VoucherType = voucherType,
            PartyType = partyType,
            PartyId = partyId,
            Amount = amount,
            PaymentMethod = paymentMethod,
            VoucherDate = voucherDate,
            Notes = notes,
            JournalEntryId = entry.Id,
            CreatedByUserId = createdByUserId
        };
        _db.Vouchers.Add(voucher);
        await _db.SaveChangesAsync();

        // السند النقدي يدخل/يخرج من صندوق المستخدم (أو الافتراضي)
        if (paymentMethod == PaymentMethod.Cash)
            await new CashBoxService(_db).RecordAutoAsync(createdByUserId,
                voucherType == VoucherType.Receipt ? CashBoxTxType.VoucherReceipt : CashBoxTxType.VoucherPayment,
                voucherType == VoucherType.Receipt ? amount : -amount, voucherDate, "Vouchers", voucher.Id,
                null, $"{(voucherType == VoucherType.Receipt ? "سند قبض" : "سند صرف")} {voucher.VoucherNumber}" + (string.IsNullOrWhiteSpace(notes) ? "" : $" — {notes}"),
                entry.Id);

        return FinanceOperationResult.Ok();
    }

    /// <summary>
    /// قيد استلام البضاعة التلقائي: مدين المخزون / دائن الموردون، بمبلغ إجمالي
    /// الاستلام. يحتاج قاعدة ربط "GoodsReceiptOnAccount" جاهزة مسبقًا.
    /// </summary>
    public async Task<(FinanceOperationResult result, int? journalEntryId)> PostGoodsReceiptEntryAsync(
        decimal amount, DateTime entryDate, int createdByUserId, string receiptNumber)
    {
        var mapping = await _db.AccountMappingRules.FirstOrDefaultAsync(r => r.TransactionType == "GoodsReceiptOnAccount");
        if (mapping is null)
            return (FinanceOperationResult.Fail(
                "لا توجد قاعدة ربط محاسبي \"GoodsReceiptOnAccount\" بعد. أضفها من إعدادات العقل المالي أولًا."), null);

        var entry = new JournalEntry
        {
            EntryNumber = await GenerateNextNumberAsync("JV"),
            EntryDate = entryDate,
            EntryType = JournalEntryType.AutoPurchase,
            Description = $"قيد استلام بضاعة تلقائي - {receiptNumber}",
            CreatedByUserId = createdByUserId,
            IsPosted = true
        };
        entry.Lines.Add(new JournalEntryLine { AccountId = mapping.DebitAccountId, Debit = amount, Credit = 0 });
        entry.Lines.Add(new JournalEntryLine { AccountId = mapping.CreditAccountId, Debit = 0, Credit = amount });
        _db.JournalEntries.Add(entry);
        await _db.SaveChangesAsync();

        return (FinanceOperationResult.Ok(), entry.Id);
    }

    /// <summary>
    /// تسوية الدفعة المقدمة عند اكتمال استلام أمر الشراء: مدين الموردون / دائن
    /// دفعات مقدمة للموردين. يحتاج قاعدة ربط "SupplierAdvanceOffset" جاهزة.
    /// </summary>
    public async Task<FinanceOperationResult> PostSupplierAdvanceOffsetAsync(
        decimal amount, DateTime entryDate, int createdByUserId)
    {
        var mapping = await _db.AccountMappingRules.FirstOrDefaultAsync(r => r.TransactionType == "SupplierAdvanceOffset");
        if (mapping is null)
            return FinanceOperationResult.Fail(
                "لا توجد قاعدة ربط محاسبي \"SupplierAdvanceOffset\" بعد. أضفها من إعدادات العقل المالي أولًا.");

        var entry = new JournalEntry
        {
            EntryNumber = await GenerateNextNumberAsync("JV"),
            EntryDate = entryDate,
            EntryType = JournalEntryType.AutoPurchase,
            Description = "تسوية دفعة مقدمة لمورد عند اكتمال الاستلام",
            CreatedByUserId = createdByUserId,
            IsPosted = true
        };
        entry.Lines.Add(new JournalEntryLine { AccountId = mapping.DebitAccountId, Debit = amount, Credit = 0 });
        entry.Lines.Add(new JournalEntryLine { AccountId = mapping.CreditAccountId, Debit = 0, Credit = amount });
        _db.JournalEntries.Add(entry);
        await _db.SaveChangesAsync();

        return FinanceOperationResult.Ok();
    }

    private async Task<string> GenerateNextNumberAsync(string prefix)
    {
        // ترقيم مبسّط لهذه المرحلة: بادئة + عدّاد تسلسلي. سيُستبدل لاحقًا
        // بترقيم يخص كل سنة مالية عند بناء إقفال السنة.
        int count = prefix switch
        {
            "JV" => await _db.JournalEntries.CountAsync(),
            "RV" or "PV" => await _db.Vouchers.CountAsync(),
            _ => 0
        };
        return $"{prefix}-{(count + 1):D5}";
    }
}
