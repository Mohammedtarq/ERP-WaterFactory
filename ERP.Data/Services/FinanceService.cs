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
        if (amount <= 0) return FinanceOperationResult.Fail("المبلغ يجب أن يكون أكبر من صفر");
        if (voucherType == VoucherType.Payment && await ApprovalLimits.CheckPaymentAsync(_db, createdByUserId, amount) is string limitError)
            return FinanceOperationResult.Fail(limitError);

        var mapping = await _db.AccountMappingRules
            .FirstOrDefaultAsync(r => r.TransactionType == mappingTransactionType);

        if (mapping is null)
        {
            return FinanceOperationResult.Fail(
                $"لا توجد قاعدة ربط محاسبي معرَّفة بعد لنوع العملية \"{mappingTransactionType}\". " +
                "أضفها من شاشة إعدادات العقل المالي أولًا، ثم أعد المحاولة.");
        }

        // القيد والسند وحركة الصندوق معًا أو لا شيء (مثلًا: تاريخ داخل شهر مقفل يرفضه القفل بعد إنشاء القيد)
        var ownTx = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync() : null;
        try
        {
            var result = await CreateVoucherCoreAsync(voucherType, partyType, partyId, amount, paymentMethod, voucherDate, mapping, createdByUserId, notes);
            if (ownTx is not null) await ownTx.CommitAsync();
            return result;
        }
        catch (DbUpdateException ex) when (BusinessError(ex) is string msg)
        {
            if (ownTx is not null) await ownTx.RollbackAsync();
            _db.ChangeTracker.Clear();
            return FinanceOperationResult.Fail(msg);
        }
        finally
        {
            if (ownTx is not null) await ownTx.DisposeAsync();
        }
    }

    /// <summary>رسالة عمل مقصودة من قاعدة البيانات (قفل الفترة...) داخل استثناء الحفظ.</summary>
    internal static string? BusinessError(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is Microsoft.Data.SqlClient.SqlException sql && sql.Number is >= 51000 and <= 51199) return sql.Message;
        return null;
    }

    private async Task<FinanceOperationResult> CreateVoucherCoreAsync(
        VoucherType voucherType, VoucherPartyType partyType, int? partyId,
        decimal amount, PaymentMethod paymentMethod, DateTime voucherDate,
        AccountMappingRule mapping, int createdByUserId, string? notes)
    {
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

        // سند على عميل ← يُعاد توزيع دفعاته على فواتيره الأقدم أولًا
        if (partyType == VoucherPartyType.Customer && partyId is int customerId)
            await new CustomerAccountService(_db).SyncAsync(customerId);

        return FinanceOperationResult.Ok();
    }

    /// <summary>
    /// إلغاء سند مرحّل بدل حذفه: قيد عكسي بنفس تاريخ السند، وإلغاء حركة الصندوق المرتبطة، وإعادة توزيع دفعات العميل.
    /// السند يبقى ظاهرًا بحالة "ملغى" مع السبب.
    /// </summary>
    public async Task<FinanceOperationResult> VoidVoucherAsync(int voucherId, string reason, int userId)
    {
        if (string.IsNullOrWhiteSpace(reason)) return FinanceOperationResult.Fail("اكتب سبب الإلغاء");
        var canVoid = await SpecialPermission.HasAsync(_db, userId, SpecialPermission.VoidPosted) ||
                      await _db.Users.AnyAsync(u => u.Id == userId && u.Role.Permissions.Any(p => p.ModuleCode == ModuleCode.Finance && p.CanDelete));
        if (!canVoid) return FinanceOperationResult.Fail("لا تملك صلاحية إلغاء السندات");

        var v = await _db.Vouchers.FirstOrDefaultAsync(x => x.Id == voucherId);
        if (v is null) return FinanceOperationResult.Fail("السند غير موجود");
        if (v.IsVoided) return FinanceOperationResult.Fail("السند ملغى مسبقًا");
        // سند المرتجع مرتبط ببضاعة عادت للمخزن: إلغاؤه وحده يترك المخزون مختلًّا
        if (v.PaymentMethod == PaymentMethod.Return) return FinanceOperationResult.Fail("سند مرتجع بضاعة — لا يُلغى من السندات لأنه مرتبط بمخزون عاد للمخزن");

        var ownTx = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync() : null;
        try
        {
            if (v.JournalEntryId is int jeId)
            {
                var lines = await _db.JournalEntryLines.AsNoTracking().Where(l => l.JournalEntryId == jeId).ToListAsync();
                var rev = new JournalEntry
                {
                    EntryNumber = await GenerateNextNumberAsync("JV"),
                    EntryDate = v.VoucherDate,
                    EntryType = JournalEntryType.AutoVoucher,
                    Description = $"إلغاء السند {v.VoucherNumber} — {reason.Trim()}",
                    CreatedByUserId = userId,
                    IsPosted = true,
                    SourceTable = "Vouchers",
                    SourceId = v.Id
                };
                foreach (var l in lines)
                    rev.Lines.Add(new JournalEntryLine { AccountId = l.AccountId, Debit = l.Credit, Credit = l.Debit, Description = "عكس: " + l.Description });
                _db.JournalEntries.Add(rev);
            }
            var cash = await _db.CashBoxTransactions.Where(t => t.ReferenceTable == "Vouchers" && t.ReferenceId == v.Id && !t.IsVoided).ToListAsync();
            foreach (var t in cash)
            {
                t.IsVoided = true;
                t.VoidReason = "إلغاء السند: " + reason.Trim();
                t.ModifiedByUserId = userId;
                t.ModifiedAt = DateTime.UtcNow;
            }
            v.IsVoided = true;
            v.VoidReason = reason.Trim();
            v.VoidedByUserId = userId;
            v.VoidedAt = DateTime.UtcNow;
            if (v.PartyType == VoucherPartyType.Customer)
                await _db.PaymentAllocations.Where(a => a.VoucherId == v.Id).ExecuteDeleteAsync();
            await _db.SaveChangesAsync();

            if (v.PartyType == VoucherPartyType.Customer && v.PartyId is int customerId)
                await new CustomerAccountService(_db).SyncAsync(customerId);
            if (ownTx is not null) await ownTx.CommitAsync();
            return FinanceOperationResult.Ok();
        }
        catch (DbUpdateException ex) when (BusinessError(ex) is string msg)
        {
            if (ownTx is not null) await ownTx.RollbackAsync();
            _db.ChangeTracker.Clear();
            return FinanceOperationResult.Fail(msg);
        }
        finally
        {
            if (ownTx is not null) await ownTx.DisposeAsync();
        }
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
