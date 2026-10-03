using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>سطر في قائمة السلف والمسحوبات والعقوبات مع ما استُقطع والمتبقي.</summary>
public class EmployeeDeductionRow
{
    public int Id { get; init; }
    public string DeductionNumber { get; init; } = "";
    public int EmployeeId { get; init; }
    public string EmployeeName { get; init; } = "";
    public EmployeeDeductionKind Kind { get; init; }
    public string KindText => EmployeeDeductionService.KindLabel(Kind);
    public DateTime EntryDate { get; init; }
    public decimal Amount { get; init; }
    public decimal? MonthlyInstallment { get; init; }
    public string StartPeriod { get; init; } = "";
    /// <summary>ما خُصم في رواتب معتمدة.</summary>
    public decimal Deducted { get; init; }
    public decimal Remaining => IsVoided ? 0 : Amount - Deducted;
    public string? Reason { get; init; }
    public bool IsOpening { get; init; }
    public bool IsVoided { get; init; }
    public string? VoidReason { get; init; }
    public string CreatedBy { get; init; } = "";
    public string StatusText => IsVoided ? "ملغى" : Remaining <= 0 ? "مُستقطع بالكامل" : Deducted > 0 ? "قيد الاستقطاع" : "بانتظار الراتب";
}

/// <summary>
/// سلف ومسحوبات وعقوبات الموظفين:
/// - السلفة: تُصرف من الصندوق وتُستقطع بأقساط شهرية بدءًا من شهر البداية حتى السداد.
/// - المسحوب: يُصرف حسب الطلب ويُستقطع كاملًا من راتب شهر البداية.
/// - العقوبة: خصم من راتب شهر البداية بسبب مكتوب، دون صرف نقدي.
/// الصرف النقدي يُقيَّد مدين "سلف ومسحوبات الموظفين" / دائن الصندوق؛ واعتماد الرواتب يُقفل الأقساط ويُسوّي الحساب.
/// </summary>
public class EmployeeDeductionService
{
    public const string PayoutRule = "EmployeeAdvancePayout";    // مدين سلف ومسحوبات الموظفين / دائن الصندوق
    public const string OpeningRule = "EmployeeAdvanceOpening";  // مدين سلف ومسحوبات الموظفين / دائن رأس المال (نقل من نظام سابق)

    private readonly ProjectDbContext _db;
    public EmployeeDeductionService(ProjectDbContext db) => _db = db;

    public static string KindLabel(EmployeeDeductionKind k) => k switch
    {
        EmployeeDeductionKind.Loan => "سلفة",
        EmployeeDeductionKind.Withdrawal => "مسحوب",
        _ => "عقوبة"
    };

    public record CreateRequest(
        EmployeeDeductionKind Kind, int EmployeeId, decimal Amount, DateTime EntryDate, int StartMonth, int StartYear,
        decimal? MonthlyInstallment = null, string? Reason = null, int? CashBoxId = null, bool IsOpening = false);

    public async Task<(FinanceOperationResult result, EmployeeDeduction? deduction)> CreateAsync(CreateRequest r, int userId)
    {
        if (r.Amount <= 0) return Fail("المبلغ يجب أن يكون أكبر من صفر");
        if (r.StartMonth is < 1 or > 12) return Fail("شهر الاستقطاع غير صحيح");
        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == r.EmployeeId);
        if (employee is null || !employee.IsActive) return Fail("اختر موظفًا فعّالًا");
        decimal? installment = null;
        if (r.Kind == EmployeeDeductionKind.Loan)
        {
            if (r.MonthlyInstallment is null or <= 0) return Fail("أدخل قسط السلفة الشهري");
            if (r.MonthlyInstallment > r.Amount) return Fail("القسط الشهري لا يمكن أن يتجاوز مبلغ السلفة");
            installment = r.MonthlyInstallment;
        }
        if (r.Kind == EmployeeDeductionKind.Penalty && string.IsNullOrWhiteSpace(r.Reason)) return Fail("اكتب سبب العقوبة");
        if (await _db.PayrollRuns.AnyAsync(p => p.PeriodMonth == r.StartMonth && p.PeriodYear == r.StartYear && p.Status == PayrollRunStatus.Approved))
            return Fail($"رواتب {r.StartMonth}/{r.StartYear} معتمدة ومقفلة — اختر شهر الاستقطاع التالي");

        var paysCash = r.Kind != EmployeeDeductionKind.Penalty && !r.IsOpening;
        int? boxId = null;
        if (paysCash)
        {
            boxId = r.CashBoxId ?? await ResolveBoxAsync(userId);
            if (boxId is null) return Fail("لا يوجد صندوق مفعّل للصرف منه — أنشئ صندوقًا من المالية ← الصناديق");
            if (!await _db.CashBoxes.AnyAsync(b => b.Id == boxId && b.IsActive)) return Fail("الصندوق غير موجود أو موقوف");
            var balance = await new CashBoxService(_db).GetBalanceAsync(boxId.Value);
            if (balance < r.Amount) return Fail($"رصيد الصندوق غير كافٍ: المتاح {balance:N0} د.ع");
        }

        await using var tx = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync() : null;   // أو داخل معاملة المستدعي (النقل من نظام سابق)
        var number = await NextNumberAsync(r.EntryDate);
        var text = $"{KindLabel(r.Kind)} {number} — {employee.FullName}" + (string.IsNullOrWhiteSpace(r.Reason) ? "" : $" ({r.Reason.Trim()})");
        JournalEntry? entry = null;
        if (r.Kind != EmployeeDeductionKind.Penalty)
        {
            var (je, error) = await LedgerHelper.PostJournalAsync(_db, r.IsOpening ? OpeningRule : PayoutRule, r.Amount, r.EntryDate,
                                                                  JournalEntryType.AutoVoucher, text, userId, "EmployeeDeductions", null, "ED");
            if (error is not null) return Fail(error);
            entry = je;
            await _db.SaveChangesAsync();
        }

        var d = new EmployeeDeduction
        {
            DeductionNumber = number, EmployeeId = r.EmployeeId, Kind = r.Kind, EntryDate = r.EntryDate.Date, Amount = r.Amount,
            MonthlyInstallment = installment, StartMonth = r.StartMonth, StartYear = r.StartYear, IsOpening = r.IsOpening,
            Reason = Clean(r.Reason), JournalEntryId = entry?.Id, CreatedByUserId = userId
        };
        _db.EmployeeDeductions.Add(d);
        await _db.SaveChangesAsync();
        if (entry is not null) { entry.SourceId = d.Id; await _db.SaveChangesAsync(); }

        if (boxId is int box)
            await new CashBoxService(_db).RecordAutoAsync(userId, CashBoxTxType.EmployeeAdvance, -r.Amount, r.EntryDate,
                                                          "EmployeeDeductions", d.Id, employee.FullName, text, entry?.Id, box);
        if (tx is not null) await tx.CommitAsync();
        return (FinanceOperationResult.Ok(), d);
    }

    /// <summary>
    /// إلغاء (للأدمن): يُرفض إن استُقطع منه شيء في رواتب معتمدة. يلغي حركة الصندوق ويعكس القيد،
    /// ويحذف أقساطه من مسودات الرواتب (يلزم إعادة توليد المسودة، والاعتماد يرفض مسودة قديمة).
    /// </summary>
    public async Task<FinanceOperationResult> VoidAsync(int deductionId, string reason, int userId)
    {
        var cash = new CashBoxService(_db);
        if (!await cash.IsAdminAsync(userId)) return FinanceOperationResult.Fail("إلغاء السلف والمسحوبات والعقوبات للأدمن فقط");
        if (string.IsNullOrWhiteSpace(reason)) return FinanceOperationResult.Fail("اكتب سبب الإلغاء");
        var d = await _db.EmployeeDeductions.Include(x => x.JournalEntry!).ThenInclude(j => j.Lines)
                         .Include(x => x.Installments).ThenInclude(i => i.PayrollRun)
                         .FirstOrDefaultAsync(x => x.Id == deductionId);
        if (d is null) return FinanceOperationResult.Fail("السجل غير موجود");
        if (d.IsVoided) return FinanceOperationResult.Fail("ملغى مسبقًا");
        if (d.Installments.Any(i => i.PayrollRun.Status == PayrollRunStatus.Approved))
            return FinanceOperationResult.Fail("استُقطع منه في رواتب معتمدة — لا يمكن إلغاؤه");

        var cashTx = await _db.CashBoxTransactions.FirstOrDefaultAsync(t => t.ReferenceTable == "EmployeeDeductions" && t.ReferenceId == d.Id && !t.IsVoided);

        await using var tx = await _db.Database.BeginTransactionAsync();
        d.IsVoided = true;
        d.VoidReason = reason.Trim();
        _db.EmployeeDeductionInstallments.RemoveRange(d.Installments);
        if (cashTx is not null)
        {
            cashTx.IsVoided = true;
            cashTx.VoidReason = reason.Trim();
            cashTx.ModifiedByUserId = userId;
            cashTx.ModifiedAt = DateTime.UtcNow;
        }
        if (d.JournalEntry is { } je)
        {
            var count = await _db.JournalEntries.CountAsync();
            var reversal = new JournalEntry
            {
                EntryNumber = $"EDR-{count + 1:D5}", EntryDate = DateTime.Today, EntryType = JournalEntryType.AutoVoucher,
                Description = $"عكس قيد {KindLabel(d.Kind)} ملغاة {d.DeductionNumber}: {reason.Trim()}", CreatedByUserId = userId, IsPosted = true,
                SourceTable = "EmployeeDeductions", SourceId = d.Id
            };
            foreach (var line in je.Lines)
                reversal.Lines.Add(new JournalEntryLine { AccountId = line.AccountId, Debit = line.Credit, Credit = line.Debit, Description = "عكس: " + line.Description });
            _db.JournalEntries.Add(reversal);
        }
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return FinanceOperationResult.Ok();
    }

    /// <summary>
    /// ما يُستقطع من موظف في شهر معيّن (بالدينار) دون حفظ: كل استقطاع غير ملغى بدأ في هذا الشهر أو قبله وبقي منه شيء.
    /// السلفة بقسطها (أو المتبقي إن كان أقل)، والمسحوب والعقوبة بكامل المتبقي — فما فات استقطاعه يُستقطع لاحقًا.
    /// المتبقي = المبلغ − الأقساط في دورات رواتب أخرى.
    /// </summary>
    public async Task<List<(EmployeeDeduction deduction, decimal amount)>> PlanForPeriodAsync(int month, int year, int? runId)
    {
        var key = year * 12 + month;
        var open = await _db.EmployeeDeductions.Include(d => d.Installments)
            .Where(d => !d.IsVoided && d.StartYear * 12 + d.StartMonth <= key).ToListAsync();
        var plan = new List<(EmployeeDeduction, decimal)>();
        foreach (var d in open)
        {
            var remaining = d.Amount - d.Installments.Where(i => i.PayrollRunId != runId).Sum(i => i.Amount);
            if (remaining <= 0) continue;
            var take = d.Kind == EmployeeDeductionKind.Loan ? Math.Min(d.MonthlyInstallment ?? remaining, remaining) : remaining;
            plan.Add((d, take));
        }
        return plan;
    }

    public async Task<List<EmployeeDeductionRow>> GetListAsync(int? employeeId = null, bool openOnly = false)
    {
        var raw = await _db.EmployeeDeductions.AsNoTracking()
            .Where(d => employeeId == null || d.EmployeeId == employeeId)
            .OrderByDescending(d => d.EntryDate).ThenByDescending(d => d.Id)
            .Select(d => new
            {
                d.Id, d.DeductionNumber, d.EmployeeId, Name = d.Employee.FullName, d.Kind, d.EntryDate, d.Amount, d.MonthlyInstallment,
                d.StartMonth, d.StartYear, d.Reason, d.IsOpening, d.IsVoided, d.VoidReason, User = d.CreatedByUser.Username,
                Deducted = d.Installments.Where(i => i.PayrollRun.Status == PayrollRunStatus.Approved).Sum(i => (decimal?)i.Amount) ?? 0
            }).ToListAsync();
        var rows = raw.Select(d => new EmployeeDeductionRow
        {
            Id = d.Id, DeductionNumber = d.DeductionNumber, EmployeeId = d.EmployeeId, EmployeeName = d.Name, Kind = d.Kind, EntryDate = d.EntryDate,
            Amount = d.Amount, MonthlyInstallment = d.MonthlyInstallment, StartPeriod = $"{d.StartMonth:00}/{d.StartYear}", Deducted = d.Deducted,
            Reason = d.Reason, IsOpening = d.IsOpening, IsVoided = d.IsVoided, VoidReason = d.VoidReason, CreatedBy = d.User
        }).ToList();
        return openOnly ? rows.Where(r => r.Remaining > 0).ToList() : rows;
    }

    public Task<EmployeeDeduction?> GetAsync(int id) =>
        _db.EmployeeDeductions.AsNoTracking().Include(d => d.Employee).Include(d => d.CreatedByUser)
           .Include(d => d.Installments).ThenInclude(i => i.PayrollRun)
           .FirstOrDefaultAsync(d => d.Id == id);

    private async Task<int?> ResolveBoxAsync(int userId) =>
        await _db.CashBoxes.Where(b => b.IsActive && (b.OwnerUserId == userId || b.IsDefault || b.BoxType == CashBoxType.Main))
            .OrderBy(b => b.OwnerUserId == userId ? 0 : b.IsDefault ? 1 : 2).ThenBy(b => b.Id)
            .Select(b => (int?)b.Id).FirstOrDefaultAsync();

    private async Task<string> NextNumberAsync(DateTime date)
    {
        var n = (await _db.Database.SqlQueryRaw<int>("SELECT NEXT VALUE FOR seq_EmployeeDeduction AS [Value]").ToListAsync())[0];
        return $"ED-{date.Year}-{n:D6}";
    }

    private static (FinanceOperationResult, EmployeeDeduction?) Fail(string m) => (FinanceOperationResult.Fail(m), null);
    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
