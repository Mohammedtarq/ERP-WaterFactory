using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

public record TempDayInput(int EmployeeId, decimal Days, string? Notes = null);

/// <summary>عامل وقتي وما له: الأيام غير المصروفة ومبلغها.</summary>
public class TempWorkerBalanceRow
{
    public int EmployeeId { get; init; }
    public string FullName { get; init; } = "";
    public string? Department { get; init; }
    public decimal DailyWage { get; init; }
    public decimal UnpaidDays { get; init; }
    public DateTime? FirstUnpaid { get; init; }
    public DateTime? LastUnpaid { get; init; }
    public decimal Due => Math.Round(UnpaidDays * DailyWage, 2);
}

public class TempPaymentRow
{
    public int Id { get; init; }
    public string PaymentNumber { get; init; } = "";
    public string FullName { get; init; } = "";
    public DateTime PaidDate { get; init; }
    public DateTime FromDate { get; init; }
    public DateTime ToDate { get; init; }
    public decimal Days { get; init; }
    public decimal DailyWage { get; init; }
    public decimal Amount { get; init; }
    public bool IsFinal { get; init; }
    public bool IsVoided { get; init; }
    public string? VoidReason { get; init; }
    public string StateText => IsVoided ? $"ملغى: {VoidReason}" : "مصروف";
    public string CreatedBy { get; init; } = "";
    public string KindText => IsFinal ? "نهاية خدمة" : "أسبوعي";
}

/// <summary>
/// العمال الوقتيون (32_temp_workers.sql): كشف أيام يدوي بلا بصمة، وصرف الأجر عن الأيام غير المصروفة
/// أسبوعيًا أو تسويةً نهائية عند إنهاء الخدمة (تُغلق البطاقة وتبقى في الأرشيف). لا سلف ولا رواتب شهرية لهم.
/// </summary>
public class TempWorkersService
{
    public const string WagesRule = "TempWagesPayment";   // مدين مصروف الرواتب والأجور / دائن الصندوق

    private readonly ProjectDbContext _db;
    public TempWorkersService(ProjectDbContext db) => _db = db;

    public Task<List<Employee>> WorkersAsync(bool activeOnly = true) =>
        _db.Employees.AsNoTracking().Include(e => e.Department)
           .Where(e => e.IsTemporary && (!activeOnly || e.IsActive)).OrderBy(e => e.FullName).ToListAsync();

    public Task<List<TempWorkDay>> DaysAsync(DateTime date) =>
        _db.TempWorkDays.AsNoTracking().Where(d => d.WorkDate == date.Date).ToListAsync();

    /// <summary>
    /// يحفظ كشف يوم: لكل عامل عدد أيامه (صفر = لم يحضر، فيُحذف سجله). اليوم المصروف لا يُعدَّل.
    /// </summary>
    public async Task<FinanceOperationResult> SaveDayAsync(DateTime date, IReadOnlyCollection<TempDayInput> inputs, int userId)
    {
        if (inputs.Any(i => i.Days is < 0 or > 1.5m)) return FinanceOperationResult.Fail("الأيام بين 0 و1.5 (نصف يوم، يوم، يوم ونصف)");
        var ids = inputs.Select(i => i.EmployeeId).ToList();
        var workers = await _db.Employees.Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id);
        if (workers.Values.Any(w => !w.IsTemporary)) return FinanceOperationResult.Fail("الكشف للعمال الوقتيين فقط");
        var existing = await _db.TempWorkDays.Where(d => d.WorkDate == date.Date && ids.Contains(d.EmployeeId)).ToDictionaryAsync(d => d.EmployeeId);
        foreach (var i in inputs)
        {
            var w = workers[i.EmployeeId];
            existing.TryGetValue(i.EmployeeId, out var row);
            if (row?.PaymentId is not null)
            {
                if (row.Days != i.Days) return FinanceOperationResult.Fail($"يوم {date:yyyy/MM/dd} للعامل {w.FullName} مصروف ولا يُعدَّل");
                continue;
            }
            if (i.Days == 0) { if (row is not null) _db.TempWorkDays.Remove(row); continue; }
            if (!w.IsActive) return FinanceOperationResult.Fail($"{w.FullName}: انتهت خدمته");
            if (w.HireDate is { } hire && date.Date < hire.Date) return FinanceOperationResult.Fail($"{w.FullName}: التاريخ قبل بدء عمله");
            if (row is null)
                _db.TempWorkDays.Add(new TempWorkDay { EmployeeId = i.EmployeeId, WorkDate = date.Date, Days = i.Days, Notes = Clean(i.Notes), CreatedByUserId = userId });
            else
            {
                row.Days = i.Days;
                row.Notes = Clean(i.Notes);
            }
        }
        await _db.SaveChangesAsync();
        return FinanceOperationResult.Ok();
    }

    public async Task<List<TempWorkerBalanceRow>> BalancesAsync(bool activeOnly = true)
    {
        var unpaid = await _db.TempWorkDays.AsNoTracking().Where(d => d.PaymentId == null)
            .GroupBy(d => d.EmployeeId)
            .Select(g => new { g.Key, Days = g.Sum(d => d.Days), First = g.Min(d => d.WorkDate), Last = g.Max(d => d.WorkDate) })
            .ToDictionaryAsync(x => x.Key);
        return (await WorkersAsync(activeOnly)).Select(w =>
        {
            var u = unpaid.GetValueOrDefault(w.Id);
            return new TempWorkerBalanceRow
            {
                EmployeeId = w.Id, FullName = w.FullName, Department = w.Department?.Name, DailyWage = w.DailyWage ?? 0,
                UnpaidDays = u?.Days ?? 0, FirstUnpaid = u?.First, LastUnpaid = u?.Last
            };
        }).ToList();
    }

    /// <summary>
    /// يصرف أجر الأيام غير المصروفة حتى تاريخ معيّن نقدًا من صندوق المستخدم بقيد (مدين الأجور / دائن الصندوق).
    /// <paramref name="endOfService"/>: تسوية نهائية تغلق بطاقة العامل (يبقى في الأرشيف).
    /// </summary>
    public async Task<(FinanceOperationResult result, TempWorkerPayment? payment)> PayAsync(int employeeId, DateTime upTo, DateTime paidDate, bool endOfService, int userId)
    {
        var w = await _db.Employees.FirstOrDefaultAsync(e => e.Id == employeeId);
        if (w is null || !w.IsTemporary) return (FinanceOperationResult.Fail("اختر عاملًا وقتيًا"), null);
        if (!w.IsActive) return (FinanceOperationResult.Fail($"{w.FullName}: انتهت خدمته مسبقًا"), null);
        if (w.DailyWage is null or <= 0) return (FinanceOperationResult.Fail($"حدّد الأجر اليومي لـ {w.FullName} من بطاقة الموظف"), null);
        var days = await _db.TempWorkDays.Where(d => d.EmployeeId == employeeId && d.PaymentId == null && d.WorkDate <= upTo.Date).ToListAsync();
        var total = days.Sum(d => d.Days);
        if (total == 0 && !endOfService) return (FinanceOperationResult.Fail("لا أيام غير مصروفة حتى هذا التاريخ"), null);
        if (endOfService && await _db.TempWorkDays.AnyAsync(d => d.EmployeeId == employeeId && d.PaymentId == null && d.WorkDate > upTo.Date))
            return (FinanceOperationResult.Fail("توجد أيام مسجّلة بعد تاريخ إنهاء الخدمة — احذفها أو غيّر التاريخ"), null);
        var amount = Math.Round(total * w.DailyWage.Value, 2);
        if (amount > 0 && await ApprovalLimits.CheckPaymentAsync(_db, userId, amount) is string limitError)
            return (FinanceOperationResult.Fail(limitError), null);

        var ownTx = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync() : null;
        try
        {
            var count = await _db.TempWorkerPayments.CountAsync(p => p.PaidDate.Year == paidDate.Year);
            var payment = new TempWorkerPayment
            {
                PaymentNumber = $"TW-{paidDate.Year}-{count + 1:D5}", EmployeeId = employeeId, PaidDate = paidDate.Date,
                FromDate = days.Count == 0 ? upTo.Date : days.Min(d => d.WorkDate), ToDate = days.Count == 0 ? upTo.Date : days.Max(d => d.WorkDate),
                Days = total, DailyWage = w.DailyWage.Value, Amount = amount, IsFinal = endOfService, CreatedByUserId = userId
            };
            _db.TempWorkerPayments.Add(payment);
            await _db.SaveChangesAsync();
            foreach (var d in days) d.PaymentId = payment.Id;

            if (amount > 0)
            {
                var text = $"أجور {w.FullName}: {total:0.##} يوم × {w.DailyWage:N0} ({payment.FromDate:yyyy/MM/dd}–{payment.ToDate:yyyy/MM/dd})"
                           + (endOfService ? " — نهاية خدمة" : "");
                var (je, error) = await LedgerHelper.PostJournalAsync(_db, WagesRule, amount, paidDate, JournalEntryType.AutoVoucher, text, userId,
                                                                      "TempWorkerPayments", payment.Id, "TW");
                if (error is not null)
                {
                    if (ownTx is not null) await ownTx.RollbackAsync();
                    _db.ChangeTracker.Clear();
                    return (FinanceOperationResult.Fail(error), null);
                }
                await _db.SaveChangesAsync();
                payment.JournalEntryId = je!.Id;
                await new CashBoxService(_db).RecordAutoAsync(userId, CashBoxTxType.TempWages, -amount, paidDate, "TempWorkerPayments", payment.Id,
                                                              w.FullName, text, je.Id);
            }
            if (endOfService)
            {
                w.IsActive = false;
                w.EndOfServiceDate = upTo.Date;
            }
            await _db.SaveChangesAsync();
            await new AuditService(_db).LogAsync(userId, "Post", "TempWorkerPayments", payment.Id, $"{payment.PaymentNumber} — {w.FullName} — {amount:N0}");
            if (ownTx is not null) await ownTx.CommitAsync();
            return (FinanceOperationResult.Ok(), payment);
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

    /// <summary>
    /// إلغاء صرف أجر بسبب: يعود النقد للصندوق (حركة الصندوق ملغاة + قيد عكسي)، وتعود أيامه غير مصروفة لتُصرف من جديد،
    /// ونهاية الخدمة تُلغى فيعود العامل فعّالًا. يبقى السند ظاهرًا مشطوبًا.
    /// </summary>
    public async Task<FinanceOperationResult> CancelPaymentAsync(int paymentId, string reason, int userId)
    {
        if (string.IsNullOrWhiteSpace(reason)) return FinanceOperationResult.Fail("اكتب سبب الإلغاء");
        if (!await SpecialPermission.HasAsync(_db, userId, SpecialPermission.VoidPosted))
            return FinanceOperationResult.Fail($"إلغاء صرف الأجر يحتاج صلاحية «{SpecialPermission.NameOf(SpecialPermission.VoidPosted)}»");
        var p = await _db.TempWorkerPayments.Include(x => x.Employee).Include(x => x.JournalEntry!).ThenInclude(j => j.Lines).FirstOrDefaultAsync(x => x.Id == paymentId);
        if (p is null) return FinanceOperationResult.Fail("سند الصرف غير موجود");
        if (p.IsVoided) return FinanceOperationResult.Fail("ملغى مسبقًا");
        if (await _db.PeriodLocks.OrderByDescending(l => l.Id).Select(l => l.LockedThrough).FirstOrDefaultAsync() is { } locked && p.PaidDate.Date <= locked.Date)
            return FinanceOperationResult.Fail($"تاريخ الصرف داخل فترة مقفلة حتى {locked:yyyy/MM/dd}");
        var text = reason.Trim();
        await using var tx = await _db.Database.BeginTransactionAsync();
        (p.IsVoided, p.VoidReason) = (true, text);
        foreach (var d in await _db.TempWorkDays.Where(d => d.PaymentId == p.Id).ToListAsync()) d.PaymentId = null;
        if (p.IsFinal) (p.Employee.IsActive, p.Employee.EndOfServiceDate) = (true, null);
        foreach (var c in await _db.CashBoxTransactions.Where(t => t.ReferenceTable == "TempWorkerPayments" && t.ReferenceId == p.Id && !t.IsVoided).ToListAsync())
            (c.IsVoided, c.VoidReason, c.ModifiedByUserId, c.ModifiedAt) = (true, text, userId, DateTime.UtcNow);
        if (p.JournalEntry is { } je)
        {
            var reversal = new JournalEntry
            {
                EntryNumber = $"TWR-{p.PaymentNumber}", EntryDate = DateTime.Today, EntryType = JournalEntryType.AutoVoucher, CreatedByUserId = userId, IsPosted = true,
                Description = $"عكس صرف أجور ملغى {p.PaymentNumber} — {p.Employee.FullName}: {text}", SourceTable = "TempWorkerPayments", SourceId = p.Id
            };
            foreach (var line in je.Lines)
                reversal.Lines.Add(new JournalEntryLine { AccountId = line.AccountId, Debit = line.Credit, Credit = line.Debit, Description = "عكس: " + line.Description });
            _db.JournalEntries.Add(reversal);
        }
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Void", "TempWorkerPayments", p.Id, $"إلغاء صرف {p.PaymentNumber} — {p.Employee.FullName} — {p.Amount:N0}: {text}");
        await tx.CommitAsync();
        return FinanceOperationResult.Ok();
    }

    public Task<List<TempPaymentRow>> PaymentsAsync(DateTime from, DateTime to) =>
        _db.TempWorkerPayments.AsNoTracking().Where(p => p.PaidDate >= from.Date && p.PaidDate <= to.Date)
           .OrderByDescending(p => p.PaidDate).ThenByDescending(p => p.Id)
           .Select(p => new TempPaymentRow
           {
               Id = p.Id, PaymentNumber = p.PaymentNumber, FullName = p.Employee.FullName, PaidDate = p.PaidDate, FromDate = p.FromDate, ToDate = p.ToDate,
               Days = p.Days, DailyWage = p.DailyWage, Amount = p.Amount, IsFinal = p.IsFinal, CreatedBy = p.CreatedByUser.Username,
               IsVoided = p.IsVoided, VoidReason = p.VoidReason
           }).ToListAsync();

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
