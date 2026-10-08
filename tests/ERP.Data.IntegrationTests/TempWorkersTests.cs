using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>المرحلة م5 (بلا ملف البصمة): العمال الوقتيون، وإغلاق المسحوبات يوم 25، والمعفى من البصمة.</summary>
[Collection("controls")]
public class TempWorkersTests
{
    private readonly ControlsFixture _f;
    public TempWorkersTests(ControlsFixture f) => _f = f;

    // شهر مستقبلي لا يمسه قفل الفترات
    private static readonly DateTime Monday = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(3);

    [Fact]
    public async Task Temp_worker_days_are_paid_weekly_then_end_of_service_closes_the_card()
    {
        await using var db = _f.NewDb(_f.AdminId);
        var worker = new Employee { FullName = "عامل تحميل وقتي", IsTemporary = true, DailyWage = 15_000, HireDate = Monday.AddDays(-10) };
        db.Employees.Add(worker);
        await db.SaveChangesAsync();
        var svc = new TempWorkersService(db);

        Assert.True((await svc.SaveDayAsync(Monday, new[] { new TempDayInput(worker.Id, 1) }, _f.AdminId)).Success);
        Assert.True((await svc.SaveDayAsync(Monday.AddDays(1), new[] { new TempDayInput(worker.Id, 0.5m, "نصف يوم") }, _f.AdminId)).Success);
        Assert.True((await svc.SaveDayAsync(Monday.AddDays(2), new[] { new TempDayInput(worker.Id, 1.5m) }, _f.AdminId)).Success);
        Assert.False((await svc.SaveDayAsync(Monday.AddDays(3), new[] { new TempDayInput(worker.Id, 2) }, _f.AdminId)).Success);
        // إعادة الحفظ بصفر تحذف اليوم
        Assert.True((await svc.SaveDayAsync(Monday.AddDays(3), new[] { new TempDayInput(worker.Id, 1) }, _f.AdminId)).Success);
        Assert.True((await svc.SaveDayAsync(Monday.AddDays(3), new[] { new TempDayInput(worker.Id, 0) }, _f.AdminId)).Success);

        var balance = (await svc.BalancesAsync()).Single(b => b.EmployeeId == worker.Id);
        Assert.Equal((3m, 45_000m), (balance.UnpaidDays, balance.Due));

        // لا سلف ولا مسحوبات للوقتي
        var noLoan = await new EmployeeDeductionService(db).CreateAsync(new EmployeeDeductionService.CreateRequest(
            EmployeeDeductionKind.Loan, worker.Id, 50_000, Monday, Monday.Month, Monday.Year, MonthlyInstallment: 10_000), _f.AdminId);
        Assert.False(noLoan.result.Success);

        // الصرف الأسبوعي: قيد أجور وحركة صندوق، والأيام تُعلَّم مصروفة
        var (paid, payment) = await svc.PayAsync(worker.Id, Monday.AddDays(6), Monday.AddDays(6), endOfService: false, _f.AdminId);
        Assert.True(paid.Success, paid.ErrorMessage);
        Assert.Equal((3m, 45_000m, Monday, Monday.AddDays(2)), (payment!.Days, payment.Amount, payment.FromDate, payment.ToDate));
        var je = await db.JournalEntries.AsNoTracking().Include(j => j.Lines).ThenInclude(l => l.Account).SingleAsync(j => j.Id == payment.JournalEntryId);
        Assert.Equal(("5102", "1101"), (je.Lines.Single(l => l.Debit > 0).Account.AccountCode, je.Lines.Single(l => l.Credit > 0).Account.AccountCode));
        var cash = await db.CashBoxTransactions.AsNoTracking().SingleAsync(t => t.ReferenceTable == "TempWorkerPayments" && t.ReferenceId == payment.Id);
        Assert.Equal((CashBoxTxType.TempWages, -45_000m), (cash.TxType, cash.Amount));
        Assert.Equal(0m, (await svc.BalancesAsync()).Single(b => b.EmployeeId == worker.Id).UnpaidDays);
        Assert.False((await svc.SaveDayAsync(Monday, new[] { new TempDayInput(worker.Id, 0.5m) }, _f.AdminId)).Success);   // مصروف
        Assert.False((await svc.PayAsync(worker.Id, Monday.AddDays(6), Monday.AddDays(6), false, _f.AdminId)).result.Success);

        // الرواتب الشهرية لا تشمله، والختامية تحسب أجوره ضمن الرواتب
        var (gen, runId) = await new HrService(db).GenerateAsync(Monday.Month, Monday.Year);
        Assert.True(gen.Success, gen.ErrorMessage);
        Assert.False(await db.PayrollLines.AnyAsync(l => l.PayrollRunId == runId && l.EmployeeId == worker.Id));
        Assert.True((await new FinalAccountsService(db).MonthAsync(Monday.Year, Monday.Month)).Salaries >= 45_000m);

        // إنهاء الخدمة: آخر يومين، وتُغلق البطاقة
        Assert.True((await svc.SaveDayAsync(Monday.AddDays(7), new[] { new TempDayInput(worker.Id, 1) }, _f.AdminId)).Success);
        Assert.True((await svc.SaveDayAsync(Monday.AddDays(8), new[] { new TempDayInput(worker.Id, 1) }, _f.AdminId)).Success);
        Assert.False((await svc.PayAsync(worker.Id, Monday.AddDays(7), Monday.AddDays(8), endOfService: true, _f.AdminId)).result.Success); // يوم بعد التاريخ
        var (final, finalPayment) = await svc.PayAsync(worker.Id, Monday.AddDays(8), Monday.AddDays(8), endOfService: true, _f.AdminId);
        Assert.True(final.Success, final.ErrorMessage);
        Assert.Equal((30_000m, true), (finalPayment!.Amount, finalPayment.IsFinal));
        var closed = await db.Employees.AsNoTracking().SingleAsync(e => e.Id == worker.Id);
        Assert.False(closed.IsActive);
        Assert.Equal(Monday.AddDays(8), closed.EndOfServiceDate);
        Assert.DoesNotContain(await svc.BalancesAsync(), b => b.EmployeeId == worker.Id);
        Assert.Contains(await svc.PaymentsAsync(Monday, Monday.AddDays(8)), p => p.Id == finalPayment.Id && p.KindText == "نهاية خدمة");
    }

    [Fact]
    public async Task Withdrawals_after_day_25_go_to_next_month_payroll()
    {
        await using var db = _f.NewDb(_f.AdminId);
        var emp = new Employee { FullName = "موظف مسحوبات", BaseSalary = 800_000 };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        var svc = new EmployeeDeductionService(db);
        var late = new DateTime(Monday.Year, Monday.Month, 26);
        var next = late.AddMonths(1);

        Assert.Equal((next.Month, next.Year), HrRules.FirstWithdrawalPeriod(late));
        Assert.Equal((Monday.Month, Monday.Year), HrRules.FirstWithdrawalPeriod(late.AddDays(-1)));
        var (refused, _) = await svc.CreateAsync(new EmployeeDeductionService.CreateRequest(
            EmployeeDeductionKind.Withdrawal, emp.Id, 25_000, late, late.Month, late.Year), _f.AdminId);
        Assert.False(refused.Success);
        Assert.Contains("25", refused.ErrorMessage);
        // صندوق ممول بقدر المسحوب: لا يعتمد على ترتيب الاختبارات الأخرى
        var cash = new CashBoxService(db);
        var box = (await cash.GetBoxesAsync(_f.AdminId)).First(b => b.BoxType is CashBoxType.Main or CashBoxType.User);
        Assert.True((await cash.DepositAsync(box.Id, 25_000 + Math.Max(0, -box.Balance), DateTime.Today, "تمويل", null, _f.AdminId)).result.Success);
        var (ok, _) = await svc.CreateAsync(new EmployeeDeductionService.CreateRequest(
            EmployeeDeductionKind.Withdrawal, emp.Id, 25_000, late, next.Month, next.Year, CashBoxId: box.Id), _f.AdminId);
        Assert.True(ok.Success, ok.ErrorMessage);
    }

    [Fact]
    public async Task Attendance_exempt_employee_is_not_docked_for_absence()
    {
        await using var db = _f.NewDb(_f.AdminId);
        var exempt = new Employee { FullName = "معفى من البصمة", BaseSalary = 900_000, AttendanceExempt = true };
        var regular = new Employee { FullName = "ملتزم بالبصمة", BaseSalary = 900_000 };
        db.Employees.AddRange(exempt, regular);
        await db.SaveChangesAsync();
        var hr = new HrService(db);
        Assert.True((await hr.SaveAttendanceAsync(DateTime.Today, new[]
        {
            new AttendanceInput(exempt.Id, null, null, null), new AttendanceInput(regular.Id, null, null, null)
        })).Success);
        var (gen, runId) = await hr.GenerateAsync(DateTime.Today.Month, DateTime.Today.Year);
        Assert.True(gen.Success, gen.ErrorMessage);
        var lines = await db.PayrollLines.AsNoTracking().Where(l => l.PayrollRunId == runId).ToDictionaryAsync(l => l.EmployeeId);
        Assert.Equal(0m, lines[exempt.Id].AbsenceDeduction);
        Assert.Equal(30_000m, lines[regular.Id].AbsenceDeduction);      // 900,000 ÷ 30
    }
}
