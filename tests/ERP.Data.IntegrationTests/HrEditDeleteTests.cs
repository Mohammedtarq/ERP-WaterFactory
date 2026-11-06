using ERP.Data.ProjectDb.Entities;
using ERP.Data.Security;
using ERP.Data.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>
/// التعديل والحذف في الموارد البشرية (بعد الفحص): حذف نوع إجازة غير مستعمل، وتعديل العطلة، وتعديل الإجازة والإضافي
/// بإلغاء القديم وتسجيل الجديد معًا، ومسح يوم حضور، وإلغاء علاوة، وإعادة فتح رواتب معتمدة، وإلغاء صرف أجر وقتي —
/// ومعها موافقة القسم المالي على الصرف.
/// </summary>
[Collection("provisioned")]
public class HrEditDeleteTests
{
    private readonly ProvisionedFixture _f;
    public HrEditDeleteTests(ProvisionedFixture f) => _f = f;

    // أشهر ماضية (الحضور والإضافي لا يُسجَّلان لأيام قادمة) وسنة رواتب بعيدة — لا تمسّها اختبارات أخرى
    private static DateTime Past(int day) => new(2022, 5, day);
    private const int PayYear = 2033;

    [Fact]
    public async Task Leave_types_holidays_leaves_overtime_and_attendance_are_editable_with_a_trace()
    {
        Assert.True(_f.Install.Success, _f.Install.ErrorMessage);
        await using var db = _f.NewDb();
        var admin = _f.AdminLocalId;
        var leaves = new HrLeaveService(db);

        // نوع غير مستعمل يُحذف، والمستعمل/الاعتيادية لا
        db.LeaveTypes.Add(new LeaveType { Name = "نوع للحذف — اختبار", AllowedDays = 1, IsPaid = true, IsActive = true, SortOrder = 99 });
        await db.SaveChangesAsync();
        var spare = await db.LeaveTypes.SingleAsync(t => t.Name == "نوع للحذف — اختبار");
        Assert.True((await leaves.DeleteTypeAsync(spare.Id, admin)).Success);
        var regular = await db.LeaveTypes.SingleAsync(t => t.IsRegular && t.IsActive);
        Assert.False((await leaves.DeleteTypeAsync(regular.Id, admin)).Success);

        // العطلة تُعدَّل اسمًا وتاريخًا، ولا تتكرر على تاريخ مسجّل
        Assert.True((await leaves.AddHolidayAsync(Past(2), "عطلة أ — اختبار", admin)).Success);
        Assert.True((await leaves.AddHolidayAsync(Past(3), "عطلة ب — اختبار", admin)).Success);
        var a = await db.PublicHolidays.AsNoTracking().SingleAsync(h => h.HolidayDate == Past(2));
        Assert.False((await leaves.UpdateHolidayAsync(a.Id, Past(3), "مكررة", admin)).Success);
        Assert.True((await leaves.UpdateHolidayAsync(a.Id, Past(4), "عطلة أ معدّلة", admin)).Success);
        Assert.Equal("عطلة أ معدّلة", (await db.PublicHolidays.AsNoTracking().SingleAsync(h => h.Id == a.Id)).Name);

        var emp = new Employee { FullName = "موظف التعديل — اختبار", BaseSalary = 600_000, HireDate = new DateTime(2020, 1, 1) };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();

        // تعديل الإجازة: القديمة ملغاة بسبب والجديدة مسجّلة؛ إن رُفض الجديد تبقى القديمة كما هي
        var (r1, first, _) = await leaves.RegisterAsync(new LeaveInput(emp.Id, regular.Id, LeaveKind.Day, Past(9), Past(9)), admin);
        Assert.True(r1.Success, r1.ErrorMessage);
        var oldId = Assert.Single(first).Id;
        Assert.False((await leaves.ReplaceAsync(oldId, new LeaveInput(emp.Id, regular.Id, LeaveKind.Day, Past(10), Past(10)), " ", admin)).result.Success);
        Assert.False((await leaves.ReplaceAsync(oldId, new LeaveInput(0, regular.Id, LeaveKind.Day, Past(10), Past(10)), "موظف خطأ", admin)).result.Success);
        Assert.False((await db.EmployeeLeaves.AsNoTracking().SingleAsync(l => l.Id == oldId)).IsVoided);
        var (r2, replaced, _) = await leaves.ReplaceAsync(oldId, new LeaveInput(emp.Id, regular.Id, LeaveKind.Day, Past(10), Past(10)), "اليوم خطأ", admin);
        Assert.True(r2.Success, r2.ErrorMessage);
        var oldLeave = await db.EmployeeLeaves.AsNoTracking().SingleAsync(l => l.Id == oldId);
        Assert.True(oldLeave.IsVoided);
        Assert.Contains("اليوم خطأ", oldLeave.VoidReason);
        Assert.Equal(Past(10), Assert.Single(replaced).FromDate);

        // تعديل الإضافي بالأسلوب نفسه
        var overtime = new OvertimeService(db);
        Assert.True((await overtime.AddAsync(emp.Id, Past(11), OvertimeKind.Hours, 2, null, admin)).Success);
        var ot = await db.OvertimeEntries.AsNoTracking().SingleAsync(o => o.EmployeeId == emp.Id && !o.IsVoided);
        Assert.True((await overtime.ReplaceAsync(ot.Id, emp.Id, Past(11), OvertimeKind.Hours, 3, null, "الساعات 3", admin)).Success);
        Assert.Equal(3m, (await db.OvertimeEntries.AsNoTracking().SingleAsync(o => o.EmployeeId == emp.Id && !o.IsVoided)).Quantity);
        Assert.True((await db.OvertimeEntries.AsNoTracking().SingleAsync(o => o.Id == ot.Id)).IsVoided);

        // مسح يوم حضور سُجّل بالخطأ؛ يوم الإجازة لا يُمسح من هنا
        var hr = new HrService(db);
        Assert.True((await hr.SaveAttendanceAsync(Past(12), new[] { new AttendanceInput(emp.Id, null, new TimeSpan(8, 0, 0), null) })).Success);
        Assert.True((await hr.ClearAttendanceDayAsync(emp.Id, Past(12), admin)).Success);
        Assert.False(await db.AttendanceRecords.AnyAsync(x => x.EmployeeId == emp.Id && x.AttendanceDate == Past(12)));
        Assert.False((await hr.ClearAttendanceDayAsync(emp.Id, Past(12), admin)).Success);
        Assert.False((await hr.ClearAttendanceDayAsync(emp.Id, Past(10), admin)).Success);            // إجازة معتمدة
    }

    [Fact]
    public async Task Payment_requests_payroll_reopen_raise_undo_and_temp_wage_cancel()
    {
        Assert.True(_f.Install.Success, _f.Install.ErrorMessage);
        await using var db = _f.NewDb();
        var admin = _f.AdminLocalId;
        var cash = new CashBoxService(db);
        var boxId = await db.CashBoxes.Where(b => b.IsActive && b.IsDefault).Select(b => b.Id).FirstAsync();
        await cash.DepositAsync(boxId, 50_000_000, DateTime.Today, "تمويل اختبار الصرف", null, admin);
        var emp = new Employee { FullName = "موظف الصرف — اختبار", BaseSalary = 500_000, HireDate = new DateTime(2020, 1, 1) };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        var payments = new PaymentApprovalService(db);

        // ---- طلب سلفة: لا نقد قبل الاعتماد، ومن لا يملك «اعتماد الصرف» لا يعتمد ----
        var before = await cash.GetBalanceAsync(boxId);
        var loan = new EmployeeDeductionService.CreateRequest(EmployeeDeductionKind.Loan, emp.Id, 120_000, DateTime.Today, DateTime.Today.Month, DateTime.Today.Year, 40_000, null, boxId);
        var (asked, request) = await payments.RequestEmployeePayoutAsync(loan, admin);
        Assert.True(asked.Success, asked.ErrorMessage);
        Assert.Equal(before, await cash.GetBalanceAsync(boxId));
        Assert.False(await db.EmployeeDeductions.AnyAsync(d => d.EmployeeId == emp.Id));
        var clerkRole = await db.Roles.FirstAsync(r => r.Name == "موظف مبيعات");
        var clerk = new User { Username = "pay_clerk_" + Guid.NewGuid().ToString("N")[..6], PasswordHash = PasswordHasher.Hash("x"), RoleId = clerkRole.Id };
        db.Users.Add(clerk);
        await db.SaveChangesAsync();
        Assert.False((await payments.ApproveAsync(request!.Id, clerk.Id)).Success);
        Assert.False((await payments.WithdrawRequestAsync(request.Id, clerk.Id)).Success);                // يسحبه مقدّمه فقط
        Assert.True((await payments.ApproveAsync(request.Id, admin)).Success);
        Assert.Equal(before - 120_000, await cash.GetBalanceAsync(boxId));
        Assert.Equal(120_000, (await db.EmployeeDeductions.SingleAsync(d => d.EmployeeId == emp.Id && !d.IsVoided)).Amount);
        Assert.False((await payments.ApproveAsync(request.Id, admin)).Success);                            // مرة واحدة فقط

        // رفض بسبب، وسحب الطلب من مقدّمه
        var (_, second) = await payments.RequestEmployeePayoutAsync(loan with { Amount = 30_000, MonthlyInstallment = 10_000 }, admin);
        Assert.False((await payments.RejectAsync(second!.Id, " ", admin)).Success);
        Assert.True((await payments.RejectAsync(second.Id, "لا حاجة", admin)).Success);
        var (_, third) = await payments.RequestEmployeePayoutAsync(loan with { Amount = 30_000, MonthlyInstallment = 10_000 }, admin);
        Assert.True((await payments.WithdrawRequestAsync(third!.Id, admin)).Success);
        Assert.Equal(before - 120_000, await cash.GetBalanceAsync(boxId));

        // ---- رواتب سنة بعيدة: اعتماد ← إعادة فتح (قيد عكسي) ← اعتماد ← صرف ← لا إعادة فتح ----
        var hr = new HrService(db);
        var (g, runId) = await hr.GenerateAsync(3, PayYear);
        Assert.True(g.Success, g.ErrorMessage);
        Assert.True((await hr.ApproveAsync(runId!.Value, admin)).result.Success);
        Assert.False((await hr.ReopenAsync(runId.Value, " ", admin)).Success);
        Assert.False((await hr.ReopenAsync(runId.Value, "تصحيح", clerk.Id)).Success);                     // بلا صلاحية
        Assert.True((await hr.ReopenAsync(runId.Value, "تصحيح حضور", admin)).Success);
        var reopened = await db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == runId);
        Assert.Equal((PayrollRunStatus.Draft, (int?)null), (reopened.Status, reopened.JournalEntryId));
        var entries = await db.JournalEntries.AsNoTracking().Include(j => j.Lines).Where(j => j.SourceTable == "PayrollRuns" && j.SourceId == runId).ToListAsync();
        Assert.Equal(2, entries.Count);
        Assert.Equal(0, entries.SelectMany(j => j.Lines).Sum(l => l.Debit - l.Credit));                    // الاستحقاق وعكسه
        Assert.True((await hr.GenerateAsync(3, PayYear)).result.Success);
        Assert.True((await hr.ApproveAsync(runId.Value, admin)).result.Success);
        var (payAsked, payout) = await payments.RequestPayrollPayoutAsync(runId.Value, boxId, admin);
        Assert.True(payAsked.Success, payAsked.ErrorMessage);
        Assert.False((await hr.ReopenAsync(runId.Value, "قبل الصرف", admin)).Success);                     // عليها طلب معلّق
        Assert.True((await payments.ApproveAsync(payout!.Id, admin)).Success);
        Assert.NotNull((await db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == runId)).PaidAt);
        Assert.False((await hr.ReopenAsync(runId.Value, "بعد الصرف", admin)).Success);

        // ---- العلاوة: الممنوحة بالخطأ تُلغى ما لم تدخل رواتب معتمدة ----
        var raises = new AnnualRaiseService(db);
        var (g1, early) = await raises.GrantWithIdAsync(emp.Id, 25_000, admin, new DateTime(PayYear, 2, 1));
        Assert.True(g1.Success, g1.ErrorMessage);
        Assert.False((await raises.UndoAsync(early!.Value, "خطأ", admin)).Success);                        // آذار معتمد بعدها
        var (g2, late) = await raises.GrantWithIdAsync(emp.Id, 30_000, admin, new DateTime(PayYear + 1, 6, 1));
        Assert.True(g2.Success, g2.ErrorMessage);
        Assert.True((await raises.RecentGrantsAsync()).Single(r => r.Id == late).CanUndo);
        Assert.True((await raises.UndoAsync(late!.Value, "منحت بالخطأ", admin)).Success);
        Assert.False(await db.PromotionsAndRaises.AnyAsync(r => r.Id == late));

        // ---- أجر العامل الوقتي: إلغاء الصرف يعيد النقد والأيام ----
        var temps = new TempWorkersService(db);
        var worker = new Employee { FullName = "وقتي الإلغاء — اختبار", IsTemporary = true, DailyWage = 20_000, HireDate = Past(1) };
        db.Employees.Add(worker);
        await db.SaveChangesAsync();
        Assert.True((await temps.SaveDayAsync(Past(16), new[] { new TempDayInput(worker.Id, 1) }, admin)).Success);
        Assert.True((await temps.SaveDayAsync(Past(17), new[] { new TempDayInput(worker.Id, 1) }, admin)).Success);
        var boxBeforePay = await cash.GetBalanceAsync(boxId);
        var (paid, payment) = await temps.PayAsync(worker.Id, Past(17), DateTime.Today, endOfService: true, admin);
        Assert.True(paid.Success, paid.ErrorMessage);
        Assert.Equal(boxBeforePay - 40_000, await cash.GetBalanceAsync(boxId));
        Assert.False((await temps.CancelPaymentAsync(payment!.Id, "خطأ", clerk.Id)).Success);              // بلا صلاحية
        Assert.True((await temps.CancelPaymentAsync(payment.Id, "صُرف لعامل آخر", admin)).Success);
        Assert.Equal(boxBeforePay, await cash.GetBalanceAsync(boxId));
        Assert.Equal((2m, 40_000m), (await temps.BalancesAsync()).Where(b => b.EmployeeId == worker.Id).Select(b => (b.UnpaidDays, b.Due)).Single());
        Assert.True((await db.Employees.AsNoTracking().SingleAsync(e => e.Id == worker.Id)).IsActive);     // نهاية الخدمة أُلغيت
        Assert.False((await temps.CancelPaymentAsync(payment.Id, "مرة ثانية", admin)).Success);
        var tw = await db.JournalEntries.AsNoTracking().Include(j => j.Lines).Where(j => j.SourceTable == "TempWorkerPayments" && j.SourceId == payment.Id).ToListAsync();
        Assert.Equal(2, tw.Count);                                                                          // القيد وعكسه
        Assert.Equal(0, tw.SelectMany(j => j.Lines).Where(l => l.Debit > 0 || l.Credit > 0).GroupBy(l => l.AccountId).Sum(x => x.Sum(l => l.Debit - l.Credit)));
    }
}
