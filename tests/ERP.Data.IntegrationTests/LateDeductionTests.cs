using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>خصم التأخير بالشرائح، المسامحة اليومية والشهرية، الاستثناءات، وربطه بتوليد الرواتب.</summary>
[Collection("provisioned")]
public class LateDeductionTests
{
    private readonly ProvisionedFixture _f;
    public LateDeductionTests(ProvisionedFixture f) => _f = f;

    private static LateDeductionTier T(int from, int? to, LateDeductionKind kind, decimal value) => new() { FromMinutes = from, ToMinutes = to, Kind = kind, Value = value };
    private static readonly LateDeductionTier[] Default = { T(1, 30, LateDeductionKind.Fraction, 0.25m), T(31, 60, LateDeductionKind.Fraction, 0.5m), T(61, null, LateDeductionKind.Fraction, 1m) };
    private static DateTime D(int day) => new(2023, 1, day);   // يناير 2023: لا تمسّه اختبارات أخرى

    [Fact]
    public void ComputeMonth_applies_tier_per_day_forgiveness_pool_cap_and_exceptions()
    {
        const decimal wage = 20_000m;
        decimal Id(decimal x) => x;
        var days = new[] { (D(3), 13, 0), (D(9), 40, 0), (D(21), 75, 0) };

        // 13 د ← ربع يوم (5,000) · 40 د ← نصف يوم (10,000) · 75 د ← يوم (20,000)
        var all = LateDeductionService.ComputeMonth(days, 0, Default, wage, Id, false, null);
        Assert.Equal((128, 3, 35_000m), (all.NetMinutes, all.DaysCharged, all.Deduction));

        // مسامحة يومية كاملة ليوم 3
        Assert.Equal(30_000m, LateDeductionService.ComputeMonth(new[] { (D(3), 13, 13), (D(9), 40, 0), (D(21), 75, 0) }, 0, Default, wage, Id, false, null).Deduction);
        // مسامحة جزئية: 75 − 20 = 55 ← الشريحة الثانية (10,000) بدل الثالثة
        Assert.Equal(25_000m, LateDeductionService.ComputeMonth(new[] { (D(3), 13, 0), (D(9), 40, 0), (D(21), 75, 20) }, 0, Default, wage, Id, false, null).Deduction);

        // رصيد شهري 30 د يُستهلك بترتيب الأيام: يوم 3 (13) ثم 17 من يوم 9 فيصير 23 ← الأولى (5,000)
        var pooled = LateDeductionService.ComputeMonth(days, 30, Default, wage, Id, false, null);
        Assert.Equal((98, 2, 25_000m), (pooled.NetMinutes, pooled.DaysCharged, pooled.Deduction));

        // معفى: الدقائق تُسجَّل ولا خصم
        var exempt = LateDeductionService.ComputeMonth(days, 0, Default, wage, Id, true, null);
        Assert.Equal((128, 0, 0m), (exempt.NetMinutes, exempt.DaysCharged, exempt.Deduction));
        // سماح خاص 20 د: 13 لا تُخصم، و40 و75 تُخصم
        Assert.Equal(30_000m, LateDeductionService.ComputeMonth(days, 0, Default, wage, Id, false, 20).Deduction);

        // مبلغ ثابت، ولا يتجاوز أجر يوم واحد، و«لا خصم»
        Assert.Equal(3_000m, LateDeductionService.ComputeMonth(new[] { (D(3), 13, 0) }, 0, new[] { T(1, null, LateDeductionKind.Fixed, 3_000m) }, wage, Id, false, null).Deduction);
        Assert.Equal(20_000m, LateDeductionService.ComputeMonth(new[] { (D(3), 13, 0) }, 0, new[] { T(1, null, LateDeductionKind.Fixed, 50_000m) }, wage, Id, false, null).Deduction);
        Assert.Equal(0m, LateDeductionService.ComputeMonth(new[] { (D(3), 13, 0) }, 0, new[] { T(1, null, LateDeductionKind.None, 0) }, wage, Id, false, null).Deduction);
        // فجوة بين الشرائح: دقائق لا تقع في أي شريحة لا تُخصم
        Assert.Equal(0m, LateDeductionService.ComputeMonth(new[] { (D(3), 45, 0) }, 0, new[] { T(1, 30, LateDeductionKind.Fraction, 1m) }, wage, Id, false, null).Deduction);
        // مبلغ ثابت لموظف بالدولار يُحوَّل بسعر الصرف
        Assert.Equal(2m, LateDeductionService.ComputeMonth(new[] { (D(3), 13, 0) }, 0, new[] { T(1, null, LateDeductionKind.Fixed, 3_000m) }, 100m, x => x / 1500m, false, null).Deduction);
    }

    [Fact]
    public async Task Settings_validation_forgiveness_exceptions_and_payroll_flow()
    {
        Assert.True(_f.Install.Success, _f.Install.ErrorMessage);
        await using var db = _f.NewDb();
        var admin = _f.AdminLocalId;
        var svc = new LateDeductionService(db);
        var hr = new HrService(db);

        // معطّل افتراضيًا والشرائح الثلاث الافتراضية موجودة
        var initial = await svc.GetSettingsAsync();
        Assert.False(initial.Enabled);
        Assert.Equal(3, initial.Tiers.Count);

        // موظف بشفت 6:00 وسماح 10 دقائق وراتب 600,000 (أجر اليوم 20,000)
        var shift = new Shift { Name = "شفت 6 صباحًا — تأخير", CheckInTime = new TimeSpan(6, 0, 0), CheckInGraceMinutes = 10, CheckOutTime = new TimeSpan(14, 0, 0) };
        db.Shifts.Add(shift);
        await db.SaveChangesAsync();
        var emp = new Employee { FullName = "موظف التأخير", BaseSalary = 600_000, ShiftId = shift.Id };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        Assert.True((await hr.SaveAttendanceAsync(D(3), new[] { new AttendanceInput(emp.Id, null, new TimeSpan(6, 13, 0), null) })).Success);   // تجاوز السماح بـ 3: يُحسب 13
        Assert.True((await hr.SaveAttendanceAsync(D(9), new[] { new AttendanceInput(emp.Id, null, new TimeSpan(6, 40, 0), null) })).Success);
        Assert.True((await hr.SaveAttendanceAsync(D(10), new[] { new AttendanceInput(emp.Id, null, new TimeSpan(6, 8, 0), null) })).Success);   // ضمن السماح
        Assert.True((await hr.SaveAttendanceAsync(D(21), new[] { new AttendanceInput(emp.Id, null, new TimeSpan(7, 15, 0), null) })).Success);
        Assert.Equal(new[] { 13, 40, 0, 75 }, await db.AttendanceRecords.Where(a => a.EmployeeId == emp.Id).OrderBy(a => a.AttendanceDate).Select(a => a.LateMinutes).ToArrayAsync());

        async Task<PayrollLine> GenerateAsync()
        {
            var (r, runId) = await hr.GenerateAsync(1, 2023);
            Assert.True(r.Success, r.ErrorMessage);
            return await db.PayrollLines.AsNoTracking().SingleAsync(l => l.PayrollRunId == runId && l.EmployeeId == emp.Id);
        }

        // قبل التفعيل: لا خصم ولا دقائق مسجّلة في الراتب
        var off = await GenerateAsync();
        Assert.Equal((0, 0m, 600_000m), (off.LateMinutes, off.LateDeduction, off.NetSalary));

        // التحقق من الإعدادات: تداخل، نهاية قبل البداية، جزء فوق يوم، تفعيل بلا شرائح
        Assert.False((await svc.SaveSettingsAsync(true, D(1), new[] { new LateTierInput(1, 30, LateDeductionKind.Fraction, 0.25m), new LateTierInput(20, null, LateDeductionKind.Fraction, 1m) }, admin)).Success);
        Assert.False((await svc.SaveSettingsAsync(true, D(1), new[] { new LateTierInput(10, 5, LateDeductionKind.Fraction, 0.25m) }, admin)).Success);
        Assert.False((await svc.SaveSettingsAsync(true, D(1), new[] { new LateTierInput(1, null, LateDeductionKind.Fraction, 1.5m) }, admin)).Success);
        Assert.False((await svc.SaveSettingsAsync(true, D(1), Array.Empty<LateTierInput>(), admin)).Success);
        // الشريحة المفتوحة (بلا حد أعلى) يجب أن تكون الأخيرة
        Assert.False((await svc.SaveSettingsAsync(true, D(1), new[] { new LateTierInput(1, null, LateDeductionKind.Fraction, 0.25m), new LateTierInput(31, 60, LateDeductionKind.Fraction, 0.5m) }, admin)).Success);

        // التفعيل من يناير 2023 بالشرائح الثلاث
        var tiers = new[] { new LateTierInput(1, 30, LateDeductionKind.Fraction, 0.25m), new LateTierInput(31, 60, LateDeductionKind.Fraction, 0.5m), new LateTierInput(61, null, LateDeductionKind.Fraction, 1m) };
        Assert.True((await svc.SaveSettingsAsync(true, D(17), tiers, admin)).Success);   // أي يوم من الشهر يُقرَّب لأوله
        Assert.Equal(D(1), (await svc.GetSettingsAsync()).From);
        var on = await GenerateAsync();
        Assert.Equal((128, 35_000m, 565_000m), (on.LateMinutes, on.LateDeduction, on.NetSalary));   // 5,000 + 10,000 + 20,000

        // قبل شهر السريان لا خصم
        Assert.True((await svc.SaveSettingsAsync(true, new DateTime(2023, 2, 1), tiers, admin)).Success);
        Assert.Equal(0m, (await GenerateAsync()).LateDeduction);
        Assert.True((await svc.SaveSettingsAsync(true, D(1), tiers, admin)).Success);

        // ---- مسامحة يومية: السبب إلزامي والمدى 0..دقائق اليوم ----
        Assert.False((await svc.ForgiveDayAsync(emp.Id, D(3), 13, " ", admin)).Success);
        Assert.False((await svc.ForgiveDayAsync(emp.Id, D(3), 14, "سبب", admin)).Success);
        Assert.False((await svc.ForgiveDayAsync(emp.Id, D(10), 1, "سبب", admin)).Success);          // لا تأخير في هذا اليوم
        Assert.True((await svc.ForgiveDayAsync(emp.Id, D(3), 13, "انقطاع المواصلات", admin)).Success);
        var rec3 = await db.AttendanceRecords.AsNoTracking().SingleAsync(a => a.EmployeeId == emp.Id && a.AttendanceDate == D(3));
        Assert.Equal((13, 13, 0, "انقطاع المواصلات", admin), (rec3.LateMinutes, rec3.ForgivenMinutes, rec3.NetLateMinutes, rec3.ForgiveReason, rec3.ForgivenByUserId));
        var afterDay = await GenerateAsync();
        Assert.Equal((115, 30_000m), (afterDay.LateMinutes, afterDay.LateDeduction));
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "Update" && a.TableName == "AttendanceRecords" && a.Summary!.Contains("مسامحة تأخير") && a.Summary.Contains("انقطاع المواصلات")));

        // إعادة رفع حضور اليوم لا تُسقط المسامحة، وتُقصّ إن صار التأخير أقل
        Assert.True((await hr.SaveAttendanceAsync(D(3), new[] { new AttendanceInput(emp.Id, null, new TimeSpan(6, 13, 0), null) })).Success);
        Assert.Equal(13, (await db.AttendanceRecords.AsNoTracking().SingleAsync(a => a.EmployeeId == emp.Id && a.AttendanceDate == D(3))).ForgivenMinutes);

        // إلغاء المسامحة (0) يعيد الخصم الكامل
        Assert.True((await svc.ForgiveDayAsync(emp.Id, D(3), 0, null, admin)).Success);
        Assert.Equal(35_000m, (await GenerateAsync()).LateDeduction);

        // ---- مسامحة شهرية 30 د: تُستهلك بترتيب الأيام ----
        Assert.False((await svc.SetMonthlyForgivenessAsync(emp.Id, 1, 2023, 30, "", admin)).Success);
        Assert.True((await svc.SetMonthlyForgivenessAsync(emp.Id, 1, 2023, 30, "ظرف عائلي", admin)).Success);
        var pooled = await GenerateAsync();
        Assert.Equal((98, 25_000m), (pooled.LateMinutes, pooled.LateDeduction));    // 13 تسقط، 40 ← 23 (ربع يوم)، 75 (يوم)
        Assert.True((await svc.SetMonthlyForgivenessAsync(emp.Id, 1, 2023, 0, null, admin)).Success);   // حذف الرصيد
        Assert.Equal(35_000m, (await GenerateAsync()).LateDeduction);

        // ---- استثناء الموظف: سماح خاص ثم إعفاء كامل ----
        Assert.True((await svc.SetEmployeeExceptionAsync(emp.Id, false, 20, admin)).Success);
        Assert.Equal(30_000m, (await GenerateAsync()).LateDeduction);                 // 13 د ضمن السماح الخاص
        Assert.True((await svc.SetEmployeeExceptionAsync(emp.Id, true, 20, admin)).Success);
        var exempt = await GenerateAsync();
        Assert.Equal((128, 0m, 600_000m), (exempt.LateMinutes, exempt.LateDeduction, exempt.NetSalary));
        Assert.True((await svc.SetEmployeeExceptionAsync(emp.Id, false, null, admin)).Success);
        Assert.Equal(35_000m, (await GenerateAsync()).LateDeduction);

        // ---- الصلاحية: مستخدم بلا «مسامحة التأخير» يُرفض، وبها يُقبل ----
        var role = new Role { Name = "مسامحة-اختبار" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        var user = new User { Username = "late-user", PasswordHash = ERP.Data.Security.PasswordHasher.Hash("x"), RoleId = role.Id };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        Assert.False((await svc.ForgiveDayAsync(emp.Id, D(9), 10, "سبب", user.Id)).Success);
        Assert.False((await svc.SetEmployeeExceptionAsync(emp.Id, true, null, user.Id)).Success);
        db.RolePermissions.Add(new RolePermission { RoleId = role.Id, ModuleCode = SpecialPermission.LateForgive, CanView = true });
        await db.SaveChangesAsync();
        Assert.True((await svc.ForgiveDayAsync(emp.Id, D(9), 10, "سبب", user.Id)).Success);
        Assert.True((await svc.ForgiveDayAsync(emp.Id, D(9), 0, null, user.Id)).Success);

        // ---- بعد اعتماد الرواتب: لا مسامحة ولا تعديل ----
        await db.PayrollRuns.Where(r => r.PeriodMonth == 1 && r.PeriodYear == 2023).ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, PayrollRunStatus.Approved));
        Assert.False((await svc.ForgiveDayAsync(emp.Id, D(3), 5, "متأخر", admin)).Success);
        Assert.False((await svc.SetMonthlyForgivenessAsync(emp.Id, 1, 2023, 10, "متأخر", admin)).Success);
    }
}
