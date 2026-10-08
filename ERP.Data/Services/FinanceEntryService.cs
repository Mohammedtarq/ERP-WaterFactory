using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

public record FinanceEntryInput(int CategoryId, decimal Amount, DateTime Date, int? VehicleId = null, int? DepartmentId = null,
                                string? PartyName = null, string? ReceiptNumber = null, string? Notes = null);

public class FinanceEntryRow
{
    public int Id { get; init; }
    public string EntryNumber { get; init; } = "";
    public DateTime EntryDate { get; init; }
    public string Category { get; init; } = "";
    public FinanceCategoryKind Kind { get; init; }
    public decimal Amount { get; init; }
    public string? Vehicle { get; init; }
    public string? Department { get; init; }
    public string? PartyName { get; init; }
    public string? ReceiptNumber { get; init; }
    public string? Notes { get; init; }
    public bool IsVoided { get; init; }
    public string? VoidReason { get; init; }
    public string CreatedBy { get; init; } = "";
    public bool IsIncome => Kind == FinanceCategoryKind.OtherIncome;
    public string KindText => Kind switch
    {
        FinanceCategoryKind.Operating => "تشغيلي",
        FinanceCategoryKind.NonOperating => "غير تشغيلي",
        _ => "إيراد آخر"
    };
}

/// <summary>
/// شاشة «مصروف» الموحّدة (31_expenses_final_accounts.sql): المصروف أو الإيراد الآخر يخرج/يدخل صندوق المستخدم تلقائيًا،
/// ويُنشأ قيده في الخلفية على حساب نوعه، مع السيارة أو القسم ورقم الوصل. لا حذف: إلغاء بقيد عكسي.
/// </summary>
public class FinanceEntryService
{
    public const string ExpenseRule = "FinanceExpense";            // مدين مصروفات عمومية / دائن الصندوق
    public const string NonOperatingRule = "FinanceNonOperating";  // مدين مصروفات غير تشغيلية / دائن الصندوق
    public const string OtherIncomeRule = "FinanceOtherIncome";    // مدين الصندوق / دائن إيرادات أخرى

    private readonly ProjectDbContext _db;
    public FinanceEntryService(ProjectDbContext db) => _db = db;

    public static string RuleOf(FinanceCategoryKind kind) => kind switch
    {
        FinanceCategoryKind.Operating => ExpenseRule,
        FinanceCategoryKind.NonOperating => NonOperatingRule,
        _ => OtherIncomeRule
    };

    public Task<List<FinanceCategory>> GetCategoriesAsync(bool activeOnly = true) =>
        _db.FinanceCategories.AsNoTracking().Where(c => !activeOnly || c.IsActive).OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync();

    public async Task<(FinanceOperationResult result, FinanceEntry? entry)> CreateAsync(FinanceEntryInput input, int userId)
    {
        if (input.Amount <= 0) return (FinanceOperationResult.Fail("المبلغ يجب أن يكون أكبر من صفر"), null);
        var category = await _db.FinanceCategories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == input.CategoryId);
        if (category is null || !category.IsActive) return (FinanceOperationResult.Fail("اختر نوع المصروف"), null);
        var income = category.Kind == FinanceCategoryKind.OtherIncome;
        if (!income && await ApprovalLimits.CheckPaymentAsync(_db, userId, input.Amount) is string limitError)
            return (FinanceOperationResult.Fail(limitError), null);

        var ownTx = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync() : null;
        try
        {
            var count = await _db.FinanceEntries.CountAsync(e => e.EntryDate.Year == input.Date.Year);
            var entry = new FinanceEntry
            {
                EntryNumber = $"{(income ? "IN" : "EX")}-{input.Date.Year}-{count + 1:D5}", EntryDate = input.Date.Date, CategoryId = category.Id,
                Amount = input.Amount, VehicleId = input.VehicleId, DepartmentId = input.DepartmentId,
                PartyName = Clean(input.PartyName), ReceiptNumber = Clean(input.ReceiptNumber), Notes = Clean(input.Notes), CreatedByUserId = userId
            };
            _db.FinanceEntries.Add(entry);
            await _db.SaveChangesAsync();

            var text = $"{(income ? "إيراد آخر" : "مصروف")} {entry.EntryNumber}: {category.Name}" + (entry.PartyName is null ? "" : $" — {entry.PartyName}");
            var (je, error) = await LedgerHelper.PostJournalAsync(_db, RuleOf(category.Kind), input.Amount, input.Date, JournalEntryType.AutoVoucher,
                                                                  text, userId, "FinanceEntries", entry.Id, income ? "IN" : "EX");
            if (error is not null)
            {
                if (ownTx is not null) await ownTx.RollbackAsync();
                _db.ChangeTracker.Clear();
                return (FinanceOperationResult.Fail(error), null);
            }
            // نوع له حساب خاص: يحل محل الحساب الافتراضي في طرف المصروف/الإيراد
            if (category.AccountId is int accountId)
            {
                var line = income ? je!.Lines.First(l => l.Credit > 0) : je!.Lines.First(l => l.Debit > 0);
                line.AccountId = accountId;
            }
            await _db.SaveChangesAsync();
            entry.JournalEntryId = je!.Id;
            await _db.SaveChangesAsync();

            await new CashBoxService(_db).RecordAutoAsync(userId, income ? CashBoxTxType.OtherIncome : CashBoxTxType.Expense,
                income ? input.Amount : -input.Amount, input.Date, "FinanceEntries", entry.Id, entry.PartyName, text, je.Id);
            if (ownTx is not null) await ownTx.CommitAsync();
            return (FinanceOperationResult.Ok(), entry);
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

    /// <summary>إلغاء بقيد عكسي بنفس التاريخ، وإلغاء حركة الصندوق — يبقى ظاهرًا بحالة «ملغى».</summary>
    public async Task<FinanceOperationResult> VoidAsync(int entryId, string reason, int userId)
    {
        if (string.IsNullOrWhiteSpace(reason)) return FinanceOperationResult.Fail("اكتب سبب الإلغاء");
        var canVoid = await SpecialPermission.HasAsync(_db, userId, SpecialPermission.VoidPosted) ||
                      await _db.Users.AnyAsync(u => u.Id == userId && u.Role.Permissions.Any(p => p.ModuleCode == ModuleCode.Finance && p.CanDelete));
        if (!canVoid) return FinanceOperationResult.Fail("لا تملك صلاحية إلغاء المصروفات");
        var e = await _db.FinanceEntries.FirstOrDefaultAsync(x => x.Id == entryId);
        if (e is null) return FinanceOperationResult.Fail("القيد غير موجود");
        if (e.IsVoided) return FinanceOperationResult.Fail("ملغى مسبقًا");

        var ownTx = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync() : null;
        try
        {
            if (e.JournalEntryId is int jeId)
            {
                var lines = await _db.JournalEntryLines.AsNoTracking().Where(l => l.JournalEntryId == jeId).ToListAsync();
                var count = await _db.JournalEntries.CountAsync();
                var rev = new JournalEntry
                {
                    EntryNumber = $"EV-{count + 1:D5}", EntryDate = e.EntryDate, EntryType = JournalEntryType.AutoVoucher,
                    Description = $"إلغاء {e.EntryNumber} — {reason.Trim()}", CreatedByUserId = userId, IsPosted = true,
                    SourceTable = "FinanceEntries", SourceId = e.Id
                };
                foreach (var l in lines)
                    rev.Lines.Add(new JournalEntryLine { AccountId = l.AccountId, Debit = l.Credit, Credit = l.Debit, Description = "عكس: " + l.Description });
                _db.JournalEntries.Add(rev);
            }
            foreach (var t in await _db.CashBoxTransactions.Where(t => t.ReferenceTable == "FinanceEntries" && t.ReferenceId == e.Id && !t.IsVoided).ToListAsync())
            {
                t.IsVoided = true;
                t.VoidReason = "إلغاء: " + reason.Trim();
                t.ModifiedByUserId = userId;
                t.ModifiedAt = DateTime.UtcNow;
            }
            e.IsVoided = true;
            e.VoidReason = reason.Trim();
            e.VoidedByUserId = userId;
            e.VoidedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await new AuditService(_db).LogAsync(userId, "Void", "FinanceEntries", e.Id, $"{e.EntryNumber} — {e.VoidReason}");
            if (ownTx is not null) await ownTx.CommitAsync();
            return FinanceOperationResult.Ok();
        }
        catch (DbUpdateException ex) when (FinanceService.BusinessError(ex) is string msg)
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

    public async Task<List<FinanceEntryRow>> ListAsync(DateTime from, DateTime to, int? categoryId = null, int? vehicleId = null)
    {
        var q = _db.FinanceEntries.AsNoTracking().Where(e => e.EntryDate >= from.Date && e.EntryDate <= to.Date);
        if (categoryId is not null) q = q.Where(e => e.CategoryId == categoryId);
        if (vehicleId is not null) q = q.Where(e => e.VehicleId == vehicleId);
        return await q.OrderByDescending(e => e.EntryDate).ThenByDescending(e => e.Id)
            .Select(e => new FinanceEntryRow
            {
                Id = e.Id, EntryNumber = e.EntryNumber, EntryDate = e.EntryDate, Category = e.Category.Name, Kind = e.Category.Kind, Amount = e.Amount,
                Vehicle = e.Vehicle != null ? e.Vehicle.VehicleName : null, Department = e.Department != null ? e.Department.Name : null,
                PartyName = e.PartyName, ReceiptNumber = e.ReceiptNumber, Notes = e.Notes, IsVoided = e.IsVoided, VoidReason = e.VoidReason,
                CreatedBy = e.CreatedByUser.Username
            }).ToListAsync();
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
