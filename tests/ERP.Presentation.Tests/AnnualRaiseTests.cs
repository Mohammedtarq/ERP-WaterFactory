using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.ViewModels.HR;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>
/// شفت القسم الافتراضي يُطبَّق على موظفيه عدا صاحب «الشفت الخاص»؛ والعلاوة السنوية: سنة من آخر علاوة (أو المكتوب، أو التعيين)
/// ← مستحق، وقبل 30 يومًا يظهر للتخطيط؛ المبلغ من الموظف وإلا القسم؛ «منح» يسجّل علاوة دائمة ويخرجه من القائمة، و«تأجيل» بسبب.
/// </summary>
[Collection("app")]
public class AnnualRaiseTests
{
    private readonly AppFixture _f;
    public AnnualRaiseTests(AppFixture f) => _f = f;

    [Fact]
    public async Task Department_shift_and_annual_raise_due_list_grant_and_snooze()
    {
        int followerId, overdueId, soonId, legacyId;
        await using (var db = _f.NewDb())
        {
            var day = new Shift { Name = "شفت النهار — علاوة", CheckInTime = new TimeSpan(8, 0, 0), CheckOutTime = new TimeSpan(16, 0, 0) };
            var production = new Shift { Name = "شفت الإنتاج — علاوة", CheckInTime = new TimeSpan(7, 0, 0), CheckOutTime = new TimeSpan(15, 0, 0) };
            var night = new Shift { Name = "شفت ليلي — علاوة", CheckInTime = new TimeSpan(18, 0, 0), CheckOutTime = new TimeSpan(6, 0, 0) };
            db.Shifts.AddRange(day, production, night);
            var dept = new Department { Name = "قسم العلاوات", AnnualRaiseAmount = 25_000 };
            db.Departments.Add(dept);
            await db.SaveChangesAsync();
            var today = DateTime.Today;
            Employee E(string name, DateTime? hire, bool own = false, Shift? shift = null, decimal? amount = null, DateTime? legacy = null) => new()
            {
                FullName = name, DepartmentId = dept.Id, HireDate = hire, HasOwnShift = own, ShiftId = (shift ?? day).Id,
                AnnualRaiseAmount = amount, LastRaiseDate = legacy, BaseSalary = 500_000
            };
            var follower = E("تابع القسم — علاوة", today.AddYears(-3));
            var own = E("شفت خاص — علاوة", today.AddYears(-3), own: true, shift: night);
            var overdue = E("متأخر — علاوة", today.AddYears(-1).AddDays(-47));
            var soon = E("قريب — علاوة", today.AddYears(-1).AddDays(18), amount: 40_000);
            var legacy = E("قديم — علاوة", today.AddYears(-10), legacy: today.AddYears(-1));
            var recent = E("حديث — علاوة", today.AddMonths(-3));
            db.Employees.AddRange(follower, own, overdue, soon, legacy, recent);
            await db.SaveChangesAsync();
            // علاوة مسجّلة قبل سنة وأسبوع لـ«تابع القسم» تعلو على تاريخ تعيينه
            var admin = await db.Users.Select(u => u.Id).FirstAsync();
            db.PromotionsAndRaises.Add(new PromotionAndRaise { EmployeeId = follower.Id, MovementType = PromotionMovementType.AnnualRaise, Amount = 10_000,
                                                               EffectiveDate = today.AddYears(-1).AddDays(-7), CreatedByUserId = admin });
            await db.SaveChangesAsync();

            // شفت القسم: يُطبَّق على من بلا «شفت خاص»
            dept.DefaultShiftId = production.Id;
            await db.SaveChangesAsync();
            await AnnualRaiseService.ApplyDepartmentShiftAsync(db, dept.Id);
            db.ChangeTracker.Clear();
            Assert.Equal(production.Id, (await db.Employees.SingleAsync(e => e.Id == follower.Id)).ShiftId);
            Assert.Equal(night.Id, (await db.Employees.SingleAsync(e => e.Id == own.Id)).ShiftId);
            (followerId, overdueId, soonId, legacyId) = (follower.Id, overdue.Id, soon.Id, legacy.Id);
        }

        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var hr = shell.Open<HrModuleViewModel>(ModuleCode.HR);
        await hr.IdleAsync();
        var raises = hr.AnnualRaises;
        hr.SelectedTab = raises;
        await hr.IdleAsync();
        await hr.LastActivation;
        await raises.IdleAsync();
        var mine = raises.Rows.Where(r => r.Row.EmployeeName.EndsWith("— علاوة")).ToList();

        var overdueRow = mine.Single(r => r.Row.EmployeeId == overdueId);
        Assert.Equal(("متأخرة 47 يومًا", 25_000m), (overdueRow.Row.StatusText, overdueRow.Amount));          // مبلغ القسم
        Assert.Contains("التعيين", overdueRow.Row.LastRaiseText);
        var soonRow = mine.Single(r => r.Row.EmployeeId == soonId);
        Assert.Equal(("بعد 18 يومًا", 40_000m, false), (soonRow.Row.StatusText, soonRow.Amount, soonRow.Row.IsDue));   // مبلغ الموظف يعلو
        Assert.Equal("مستحقة اليوم", mine.Single(r => r.Row.EmployeeId == legacyId).Row.StatusText);           // التاريخ المكتوب للقدامى
        Assert.Contains("مكتوب", mine.Single(r => r.Row.EmployeeId == legacyId).Row.LastRaiseText);
        Assert.Equal("متأخرة 7 يومًا", mine.Single(r => r.Row.EmployeeId == followerId).Row.StatusText);      // آخر علاوة مسجّلة
        Assert.DoesNotContain(mine, r => r.Row.EmployeeName.StartsWith("حديث"));                              // أقل من 11 شهرًا

        // الجرس للمدير
        await using (var db = _f.NewDb())
        {
            var perms = new UserPermissions(await db.Users.Where(u => u.Username == AppFixture.AdminUser).SelectMany(u => u.Role.Permissions).ToListAsync());
            Assert.Contains(await new AlertsService(db).ForAsync(perms), a => a.Key == "annual-raise" && a.Title.StartsWith("مستحقون للعلاوة السنوية"));
        }

        // منح: علاوة دائمة بتاريخ اليوم، ويخرج من القائمة
        await raises.GrantCommand.ExecuteAsync(overdueRow);
        Assert.Empty(dialogs.Errors);
        Assert.DoesNotContain(raises.Rows, r => r.Row.EmployeeId == overdueId);
        await using (var db = _f.NewDb())
        {
            var granted = await db.PromotionsAndRaises.SingleAsync(p => p.EmployeeId == overdueId);
            Assert.Equal((PromotionMovementType.AnnualRaise, PromotionApplicationType.PermanentAddition, 25_000m, DateTime.Today),
                         (granted.MovementType, granted.ApplicationType, granted.Amount, granted.EffectiveDate));
        }

        // تأجيل: يحتاج سببًا، ثم يختفي حتى انتهاء المدة
        var legacyRow = raises.Rows.Single(r => r.Row.EmployeeId == legacyId);
        raises.SnoozeReason = null;
        await raises.SnoozeCommand.ExecuteAsync(legacyRow);
        Assert.Contains(dialogs.Errors, e => e.Contains("سبب التأجيل"));
        dialogs.Errors.Clear();
        raises.SnoozeReason = "بانتظار تقييم الأداء";
        raises.SnoozeDays = 60;
        await raises.SnoozeCommand.ExecuteAsync(legacyRow);
        Assert.Empty(dialogs.Errors);
        Assert.DoesNotContain(raises.Rows, r => r.Row.EmployeeId == legacyId);
        await using (var db = _f.NewDb())
            Assert.Equal(DateTime.Today.AddDays(60), (await db.Employees.SingleAsync(e => e.Id == legacyId)).RaiseSnoozedUntil);
        Assert.Empty(_f.Unhandled);
    }
}
