using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>
/// كل ثوابت معادلات الموارد البشرية في مكان واحد — تعديل أي قاعدة = تعديل سطر هنا فقط.
/// </summary>
public static class HrRules
{
    /// <summary>أجر اليوم = الراتب الأساسي ÷ 30 (العرف المعتمد في العراق).</summary>
    public const decimal DaysPerMonthForDailyWage = 30m;

    /// <summary>وزن يوم الحضور في نقاط الانضباط: حاضر = 1، متأخر = 0.5، غائب = 0؛ الإجازة المعتمدة لا تُحسب.</summary>
    public const decimal PresentDayPoints = 1m;
    public const decimal LateDayPoints = 0.5m;

    /// <summary>قاعدة الربط المحاسبي لقيد الرواتب: مدين مصروف الرواتب / دائن رواتب مستحقة.</summary>
    public const string PayrollMappingRule = "PayrollAccrual";

    public const string Iqd = "IQD";
    public const string Usd = "USD";
}

public record AttendanceInput(int EmployeeId, AttendanceStatus? ForcedStatus, TimeSpan? CheckIn, TimeSpan? CheckOut);

public record IncentiveBreakdown(decimal AttendanceScore, decimal PerformanceScore, decimal SkillsScore, decimal TotalScore, decimal Amount);

public class PayrollSummary
{
    public int RunId { get; init; }
    public int EmployeeCount { get; init; }
    public decimal TotalNetIqd { get; init; }
    public decimal TotalNetUsd { get; init; }
    public decimal? UsdRate { get; init; }
    /// <summary>إجمالي القيد بالدينار (الدولار محوّل بسعر الصرف).</summary>
    public decimal TotalInIqd { get; init; }
}

public class HrService
{
    private readonly ProjectDbContext _db;

    public HrService(ProjectDbContext db)
    {
        _db = db;
    }

    private static (DateTime start, DateTime end) Period(int month, int year)
    {
        var start = new DateTime(year, month, 1);
        return (start, start.AddMonths(1).AddDays(-1));
    }

    private Task<bool> IsPeriodApprovedAsync(int month, int year) =>
        _db.PayrollRuns.AnyAsync(r => r.PeriodMonth == month && r.PeriodYear == year && r.Status == PayrollRunStatus.Approved);

    // ============================ الحضور ============================

    /// <summary>
    /// يحسب حالة اليوم ودقائق التأخير من الشفت: التأخير = وقت الدخول − بداية الشفت، ويُحسب
    /// فقط إذا تجاوز فترة السماح. بلا وقت دخول = غائب (ما لم تُحدَّد إجازة معتمدة).
    /// </summary>
    public static (AttendanceStatus status, int lateMinutes) Classify(Shift? shift, AttendanceStatus? forced, TimeSpan? checkIn)
    {
        if (forced is AttendanceStatus.Absent or AttendanceStatus.ApprovedLeave) return (forced.Value, 0);
        if (checkIn is null) return (forced ?? AttendanceStatus.Absent, 0);
        if (shift is null) return (AttendanceStatus.Present, 0);

        var late = (int)Math.Floor((checkIn.Value - shift.CheckInTime).TotalMinutes);
        return late > shift.CheckInGraceMinutes ? (AttendanceStatus.Late, late) : (AttendanceStatus.Present, 0);
    }

    public async Task<FinanceOperationResult> SaveAttendanceAsync(DateTime date, IReadOnlyCollection<AttendanceInput> inputs)
    {
        date = date.Date;
        if (date > DateTime.Today) return FinanceOperationResult.Fail("لا يمكن تسجيل حضور ليوم لم يأتِ بعد");
        if (await IsPeriodApprovedAsync(date.Month, date.Year))
            return FinanceOperationResult.Fail("رواتب هذا الشهر معتمدة؛ لا يمكن تعديل حضوره");
        foreach (var i in inputs)
            if (i.CheckIn is not null && i.CheckOut is not null && i.CheckOut < i.CheckIn)
                return FinanceOperationResult.Fail("وقت الخروج قبل وقت الدخول لأحد الموظفين");

        var ids = inputs.Select(i => i.EmployeeId).ToList();
        var employees = await _db.Employees.Include(e => e.Shift).Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id);
        var existing = await _db.AttendanceRecords.Where(a => a.AttendanceDate == date && ids.Contains(a.EmployeeId))
                                                  .ToDictionaryAsync(a => a.EmployeeId);
        foreach (var input in inputs)
        {
            var (status, late) = Classify(employees[input.EmployeeId].Shift, input.ForcedStatus, input.CheckIn);
            if (!existing.TryGetValue(input.EmployeeId, out var rec))
                _db.AttendanceRecords.Add(rec = new AttendanceRecord { EmployeeId = input.EmployeeId, AttendanceDate = date });
            rec.Status = status;
            rec.LateMinutes = late;
            rec.CheckInTime = status is AttendanceStatus.Absent or AttendanceStatus.ApprovedLeave ? null : input.CheckIn;
            rec.CheckOutTime = status is AttendanceStatus.Absent or AttendanceStatus.ApprovedLeave ? null : input.CheckOut;
        }
        await _db.SaveChangesAsync();
        return FinanceOperationResult.Ok();
    }

    /// <summary>نقاط الانضباط (0–100) = نقاط أيام الحضور ÷ أيام العمل المسجّلة × 100 (الإجازات المعتمدة مستثناة).</summary>
    public async Task<decimal> ComputeAttendanceScoreAsync(int employeeId, int month, int year)
    {
        var (start, end) = Period(month, year);
        var counts = await _db.AttendanceRecords
            .Where(a => a.EmployeeId == employeeId && a.AttendanceDate >= start && a.AttendanceDate <= end)
            .GroupBy(a => a.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        int C(AttendanceStatus s) => counts.FirstOrDefault(c => c.Key == s)?.Count ?? 0;

        var workDays = C(AttendanceStatus.Present) + C(AttendanceStatus.Late) + C(AttendanceStatus.Absent);
        if (workDays == 0) return 0;
        var points = C(AttendanceStatus.Present) * HrRules.PresentDayPoints + C(AttendanceStatus.Late) * HrRules.LateDayPoints;
        return Math.Round(points / workDays * 100m, 2);
    }

    public async Task<int> CountWorkedDaysAsync(int employeeId, int month, int year)
    {
        var (start, end) = Period(month, year);
        return await _db.AttendanceRecords.CountAsync(a => a.EmployeeId == employeeId && a.AttendanceDate >= start && a.AttendanceDate <= end
                                                           && (a.Status == AttendanceStatus.Present || a.Status == AttendanceStatus.Late));
    }

    // ============================ الحافز الشهري ============================

    public async Task<IncentiveScoreWeights> GetWeightsAsync() =>
        await _db.IncentiveScoreWeights.AsNoTracking().OrderBy(w => w.Id).FirstOrDefaultAsync() ?? new IncentiveScoreWeights();

    /// <summary>
    /// المجموع = (الانضباط × وزنه + الأداء × وزنه + المهارات × وزنها) ÷ 100،
    /// والمبلغ = مبلغ الشريحة التي يقع فيها المجموع في مقياس التحويل (صفر إن لم تقع في أي شريحة).
    /// </summary>
    public async Task<IncentiveBreakdown> CalculateIncentiveAsync(int employeeId, int month, int year, decimal performance, decimal skills)
    {
        var w = await GetWeightsAsync();
        var attendance = await ComputeAttendanceScoreAsync(employeeId, month, year);
        var total = Math.Round((attendance * w.AttendanceWeight + performance * w.PerformanceWeight + skills * w.SkillsWeight) / 100m, 2);
        var amount = await _db.IncentiveScoreToAmountScale.AsNoTracking()
            .Where(s => s.MinScore <= total && total <= s.MaxScore)
            .OrderByDescending(s => s.MinScore).Select(s => (decimal?)s.Amount).FirstOrDefaultAsync() ?? 0;
        return new IncentiveBreakdown(attendance, performance, skills, total, amount);
    }

    public async Task<(FinanceOperationResult result, IncentiveBreakdown? breakdown)> SaveEvaluationAsync(
        int employeeId, int month, int year, decimal performance, decimal skills)
    {
        if (performance is < 0 or > 100 || skills is < 0 or > 100)
            return (FinanceOperationResult.Fail("درجتا الأداء والمهارات يجب أن تكونا بين 0 و 100"), null);
        if (await IsPeriodApprovedAsync(month, year))
            return (FinanceOperationResult.Fail("رواتب هذا الشهر معتمدة؛ لا يمكن تعديل تقييم الحوافز"), null);

        var w = await GetWeightsAsync();
        if (w.AttendanceWeight + w.PerformanceWeight + w.SkillsWeight != 100)
            return (FinanceOperationResult.Fail("مجموع أوزان الحافز يجب أن يساوي 100 — عدّلها من إعدادات الحوافز"), null);

        var b = await CalculateIncentiveAsync(employeeId, month, year, performance, skills);
        var e = await _db.MonthlyIncentiveEvaluations
            .FirstOrDefaultAsync(x => x.EmployeeId == employeeId && x.PeriodMonth == month && x.PeriodYear == year);
        if (e is null)
            _db.MonthlyIncentiveEvaluations.Add(e = new MonthlyIncentiveEvaluation { EmployeeId = employeeId, PeriodMonth = month, PeriodYear = year });
        e.AttendanceScoreAuto = b.AttendanceScore;
        e.PerformanceScoreManual = performance;
        e.SkillsScoreManual = skills;
        e.TotalScore = b.TotalScore;
        e.IncentiveAmount = b.Amount;
        await _db.SaveChangesAsync();
        return (FinanceOperationResult.Ok(), b);
    }

    // ============================ حوافز المبيعات ============================

    private IQueryable<SalesInvoiceLine> PostedSalesLines(int month, int year)
    {
        var (start, end) = Period(month, year);
        return _db.SalesInvoiceLines.Where(l => l.SalesInvoice.Status == DocumentStatus.Posted && !l.SalesInvoice.IsFreeSale
                                                && l.SalesInvoice.InvoiceDate >= start && l.SalesInvoice.InvoiceDate <= end);
    }

    /// <summary>حافز المندوب = Σ (الكمية المباعة بالقطعة من فواتيره المرحّلة × حافز القطعة لذلك الصنف).</summary>
    public async Task<decimal> ComputeRepIncentiveAsync(int repEmployeeId, int month, int year)
    {
        var rates = _db.RepItemIncentiveRates;
        var total = await PostedSalesLines(month, year)
            .Where(l => l.SalesInvoice.SalesRepEmployeeId == repEmployeeId)
            .Join(rates, l => l.ItemId, r => r.ItemId, (l, r) => l.QuantityBaseUnits * r.IncentiveRatePerUnit)
            .SumAsync(x => (decimal?)x) ?? 0;
        return Math.Round(total, 2);
    }

    public async Task<decimal> TotalSoldQuantityAsync(int month, int year) =>
        await PostedSalesLines(month, year).SumAsync(l => (decimal?)l.QuantityBaseUnits) ?? 0;

    /// <summary>
    /// حافز مدير المبيعات = Σ (الكمية الواقعة في كل شريحة × معدلها) × أيام دوامه الفعلية.
    /// الشريحة تغطي الكميات من FromQuantity إلى ToQuantity (بلا حد أعلى إن كانت فارغة)، تصاعديًا.
    /// </summary>
    public async Task<(decimal amount, decimal soldQuantity, decimal tierSum, int workedDays)> ComputeSalesManagerIncentiveAsync(
        int managerEmployeeId, int month, int year)
    {
        var sold = await TotalSoldQuantityAsync(month, year);
        var tiers = await _db.SalesManagerIncentiveTiers.AsNoTracking()
            .Where(t => t.EmployeeId == managerEmployeeId).OrderBy(t => t.FromQuantity).ToListAsync();

        decimal tierSum = 0;
        foreach (var t in tiers)
        {
            var upper = Math.Min(sold, t.ToQuantity ?? decimal.MaxValue);
            var inTier = Math.Max(0, upper - t.FromQuantity);
            tierSum += inTier * t.RatePerUnit;
        }
        var days = await CountWorkedDaysAsync(managerEmployeeId, month, year);
        return (Math.Round(tierSum * days, 2), sold, tierSum, days);
    }

    // ============================ الرواتب ============================

    public async Task<decimal?> GetUsdRateAsync(DateTime onDate) =>
        await _db.ExchangeRates.AsNoTracking()
            .Where(r => r.CurrencyCode == HrRules.Usd && r.EffectiveDate <= onDate)
            .OrderByDescending(r => r.EffectiveDate).ThenByDescending(r => r.Id)
            .Select(r => (decimal?)r.RateToIQD).FirstOrDefaultAsync();

    /// <summary>
    /// يولّد (أو يعيد توليد) مسودة رواتب الشهر لكل الموظفين الفعّالين:
    /// الأساسي = الراتب + كل الزيادات الدائمة السارية حتى نهاية الشهر،
    /// البدلات = المبالغ لمرة واحدة السارية داخل الشهر،
    /// الخصم = (الأساسي ÷ 30) × أيام الغياب،
    /// الحوافز (مندوب + مدير مبيعات + شهري) بالدينار، وتُحوَّل للدولار لموظفي الدولار بسعر الصرف،
    /// الاستقطاعات (قسط السلفة، المسحوبات، العقوبات) بالدينار وتُحوَّل لموظفي الدولار،
    /// الصافي = الأساسي + البدلات + الحوافز − خصم الغياب − الاستقطاعات.
    /// </summary>
    public async Task<(FinanceOperationResult result, int? runId)> GenerateAsync(int month, int year)
    {
        if (month is < 1 or > 12) return (FinanceOperationResult.Fail("شهر غير صحيح"), null);
        var (start, end) = Period(month, year);

        var run = await _db.PayrollRuns.Include(r => r.Lines).FirstOrDefaultAsync(r => r.PeriodMonth == month && r.PeriodYear == year);
        if (run?.Status == PayrollRunStatus.Approved)
            return (FinanceOperationResult.Fail($"رواتب {month}/{year} معتمدة مسبقًا ولا يمكن إعادة توليدها"), run.Id);

        var employees = await _db.Employees.AsNoTracking().Where(e => e.IsActive && (e.HireDate == null || e.HireDate <= end))
                                 .OrderBy(e => e.FullName).ToListAsync();
        if (employees.Count == 0) return (FinanceOperationResult.Fail("لا يوجد موظفون فعّالون"), null);

        decimal? usdRate = null;
        if (employees.Any(e => e.SalaryCurrency == SalaryCurrency.USD))
        {
            usdRate = await GetUsdRateAsync(end);
            if (usdRate is null or <= 0)
                return (FinanceOperationResult.Fail($"يوجد موظفون برواتب بالدولار ولا يوجد سعر صرف للدولار ساري حتى {end:yyyy/MM/dd}. أضفه من المالية ← أسعار الصرف."), null);
        }

        var movements = await _db.PromotionsAndRaises.AsNoTracking().Where(p => p.EffectiveDate <= end).ToListAsync();
        var evaluations = await _db.MonthlyIncentiveEvaluations.AsNoTracking()
            .Where(e => e.PeriodMonth == month && e.PeriodYear == year).ToDictionaryAsync(e => e.EmployeeId, e => e.IncentiveAmount);
        var absences = await _db.AttendanceRecords.AsNoTracking()
            .Where(a => a.AttendanceDate >= start && a.AttendanceDate <= end && a.Status == AttendanceStatus.Absent)
            .GroupBy(a => a.EmployeeId).Select(g => new { g.Key, Days = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Days);

        if (run is null)
        {
            run = new PayrollRun { PeriodMonth = month, PeriodYear = year };
            _db.PayrollRuns.Add(run);
        }
        else
        {
            _db.PayrollLines.RemoveRange(run.Lines);
            _db.EmployeeDeductionInstallments.RemoveRange(await _db.EmployeeDeductionInstallments.Where(i => i.PayrollRunId == run.Id).ToListAsync());
        }
        var deductionPlan = (await new EmployeeDeductionService(_db).PlanForPeriodAsync(month, year, run.Id == 0 ? null : run.Id))
            .Where(p => employees.Any(e => e.Id == p.deduction.EmployeeId)).ToList();

        foreach (var e in employees)
        {
            var mine = movements.Where(m => m.EmployeeId == e.Id).ToList();
            var baseSalary = e.BaseSalary + mine.Where(m => m.ApplicationType == PromotionApplicationType.PermanentAddition).Sum(m => m.Amount);
            var allowances = mine.Where(m => m.ApplicationType == PromotionApplicationType.OneTime && m.EffectiveDate >= start).Sum(m => m.Amount);
            var deduction = Math.Round(baseSalary / HrRules.DaysPerMonthForDailyWage * absences.GetValueOrDefault(e.Id), 2);

            var rep = e.IsSalesRep ? await ComputeRepIncentiveAsync(e.Id, month, year) : 0;
            var manager = e.IsSalesManager ? (await ComputeSalesManagerIncentiveAsync(e.Id, month, year)).amount : 0;
            var monthly = evaluations.GetValueOrDefault(e.Id);

            // الحوافز محسوبة بالدينار؛ موظف الدولار يستلمها محوّلة
            decimal Fx(decimal iqd) => e.SalaryCurrency == SalaryCurrency.USD ? Math.Round(iqd / usdRate!.Value, 2) : iqd;
            rep = Fx(rep); manager = Fx(manager); monthly = Fx(monthly);
            var mineDeductions = deductionPlan.Where(p => p.deduction.EmployeeId == e.Id).ToList();
            decimal Ded(EmployeeDeductionKind k) => Fx(mineDeductions.Where(p => p.deduction.Kind == k).Sum(p => p.amount));
            var loan = Ded(EmployeeDeductionKind.Loan);
            var withdrawal = Ded(EmployeeDeductionKind.Withdrawal);
            var penalty = Ded(EmployeeDeductionKind.Penalty);

            run.Lines.Add(new PayrollLine
            {
                EmployeeId = e.Id,
                Currency = e.SalaryCurrency.ToString(),
                BaseSalary = baseSalary,
                Allowances = allowances,
                AbsenceDeduction = deduction,
                RepIncentiveAmount = rep,
                SalesManagerIncentiveAmount = manager,
                MonthlyIncentiveAmount = monthly,
                LoanDeduction = loan,
                WithdrawalDeduction = withdrawal,
                PenaltyDeduction = penalty,
                NetSalary = baseSalary + allowances + rep + manager + monthly - deduction - loan - withdrawal - penalty
            });
        }
        foreach (var (d, amount) in deductionPlan)
            _db.EmployeeDeductionInstallments.Add(new EmployeeDeductionInstallment { EmployeeDeduction = d, PayrollRun = run, Amount = amount });

        await _db.SaveChangesAsync();
        return (FinanceOperationResult.Ok(), run.Id);
    }

    public async Task<PayrollSummary> SummarizeAsync(int runId)
    {
        var run = await _db.PayrollRuns.AsNoTracking().Include(r => r.Lines).FirstAsync(r => r.Id == runId);
        var (_, end) = Period(run.PeriodMonth, run.PeriodYear);
        var iqd = run.Lines.Where(l => l.Currency == HrRules.Iqd).Sum(l => l.NetSalary);
        var usd = run.Lines.Where(l => l.Currency == HrRules.Usd).Sum(l => l.NetSalary);
        var rate = usd != 0 ? await GetUsdRateAsync(end) : null;
        return new PayrollSummary
        {
            RunId = run.Id, EmployeeCount = run.Lines.Count, TotalNetIqd = iqd, TotalNetUsd = usd, UsdRate = rate,
            TotalInIqd = iqd + Math.Round(usd * (rate ?? 0), 2)
        };
    }

    /// <summary>
    /// اعتماد الرواتب: يُنشئ قيد الاستحقاق (مدين مصروف الرواتب / دائن رواتب مستحقة) بالدينار
    /// عبر العقل المالي، ويقفل الشهر (لا تعديل حضور أو تقييم أو إعادة توليد بعده). عملية واحدة ذرّية.
    /// </summary>
    public async Task<(FinanceOperationResult result, PayrollSummary? summary)> ApproveAsync(int runId, int userId)
    {
        await using var tx = await _db.Database.BeginTransactionAsync();
        var run = await _db.PayrollRuns.Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == runId);
        if (run is null) return (FinanceOperationResult.Fail("مسودة الرواتب غير موجودة"), null);
        if (run.Status == PayrollRunStatus.Approved) return (FinanceOperationResult.Fail("هذه الرواتب معتمدة مسبقًا"), null);
        if (run.Lines.Count == 0) return (FinanceOperationResult.Fail("لا توجد سطور رواتب؛ ولّد الرواتب أولًا"), null);
        if (run.Lines.Any(l => l.NetSalary < 0)) return (FinanceOperationResult.Fail("يوجد صافي راتب سالب؛ راجع الخصومات قبل الاعتماد"), null);

        var rule = await _db.AccountMappingRules.FirstOrDefaultAsync(r => r.TransactionType == HrRules.PayrollMappingRule);
        if (rule is null)
            return (FinanceOperationResult.Fail($"قاعدة الربط المحاسبي \"{HrRules.PayrollMappingRule}\" (مصروف الرواتب / رواتب مستحقة) غير معرّفة. أضفها من المالية ← العقل المالي."), null);

        // مسودة قديمة: استقطاع أُلغي أو أُضيف بعد التوليد ← يلزم إعادة التوليد قبل الاعتماد
        var installments = await _db.EmployeeDeductionInstallments.Include(i => i.EmployeeDeduction)
                                    .Where(i => i.PayrollRunId == run.Id).ToListAsync();
        var plan = await new EmployeeDeductionService(_db).PlanForPeriodAsync(run.PeriodMonth, run.PeriodYear, run.Id);
        var activeIds = run.Lines.Select(l => l.EmployeeId).ToHashSet();
        var planned = plan.Where(p => activeIds.Contains(p.deduction.EmployeeId)).ToDictionary(p => p.deduction.Id, p => p.amount);
        var recorded = installments.ToDictionary(i => i.EmployeeDeductionId, i => i.Amount);
        if (planned.Count != recorded.Count || planned.Any(p => recorded.GetValueOrDefault(p.Key) != p.Value))
            return (FinanceOperationResult.Fail("تغيّرت السلف أو المسحوبات أو العقوبات بعد توليد الرواتب — أعد توليد الرواتب ثم اعتمدها"), null);
        var advancesIqd = installments.Where(i => i.EmployeeDeduction.Kind != EmployeeDeductionKind.Penalty).Sum(i => i.Amount);
        var advancesAccountId = 0;
        if (advancesIqd > 0)
        {
            var payout = await _db.AccountMappingRules.FirstOrDefaultAsync(r => r.TransactionType == EmployeeDeductionService.PayoutRule);
            if (payout is null)
                return (FinanceOperationResult.Fail($"قاعدة الربط المحاسبي \"{EmployeeDeductionService.PayoutRule}\" غير معرّفة. أضفها من المالية ← العقل المالي."), null);
            advancesAccountId = payout.DebitAccountId;
        }

        var summary = await SummarizeAsync(run.Id);
        if (summary.TotalNetUsd != 0 && summary.UsdRate is null)
            return (FinanceOperationResult.Fail("لا يوجد سعر صرف للدولار لتحويل رواتب الدولار في القيد"), null);

        var entry = new JournalEntry
        {
            EntryNumber = $"PR-{run.PeriodYear}-{run.PeriodMonth:D2}",
            EntryDate = Period(run.PeriodMonth, run.PeriodYear).end,
            EntryType = JournalEntryType.AutoPayroll,
            Description = $"استحقاق رواتب {run.PeriodMonth}/{run.PeriodYear} — {run.Lines.Count} موظف" +
                          (summary.TotalNetUsd != 0 ? $" (منها {summary.TotalNetUsd:N2}$ بسعر {summary.UsdRate:N0})" : ""),
            CreatedByUserId = userId,
            IsPosted = true,
            SourceTable = "PayrollRuns",
            SourceId = run.Id
        };
        // المصروف = الصافي + ما استُقطع من سلف ومسحوبات (صُرفت سابقًا نقدًا)؛ العقوبة تُنقص المصروف نفسه
        entry.Lines.Add(new JournalEntryLine { AccountId = rule.DebitAccountId, Debit = summary.TotalInIqd + advancesIqd, Description = "مصروف الرواتب والحوافز" });
        entry.Lines.Add(new JournalEntryLine { AccountId = rule.CreditAccountId, Credit = summary.TotalInIqd, Description = "رواتب مستحقة الدفع" });
        if (advancesIqd > 0)
            entry.Lines.Add(new JournalEntryLine { AccountId = advancesAccountId, Credit = advancesIqd, Description = "استقطاع سلف ومسحوبات الموظفين" });
        _db.JournalEntries.Add(entry);
        await _db.SaveChangesAsync();

        run.Status = PayrollRunStatus.Approved;
        run.JournalEntryId = entry.Id;
        run.ApprovedByUserId = userId;
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return (FinanceOperationResult.Ok(), summary);
    }
}
