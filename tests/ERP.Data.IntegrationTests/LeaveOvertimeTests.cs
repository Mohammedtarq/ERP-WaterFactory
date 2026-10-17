using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>
/// الإجازات والعطل والعمل الإضافي (قرارات المدير): الاعتيادية 3 أيام في الشهر تسقط بنهايته، وما زاد بدون راتب مع تنبيه؛
/// زمنيتان في الشهر مسموحتان ومن الثالثة يوم من الاعتيادية أو يوم بدون راتب؛ العطلة الرسمية والإجازة لا غياب فيهما،
/// والزمنية تُعفي من التأخير؛ الإضافي أيام بالأجر اليومي وساعات بسعر عام أو خاص؛ وكله في كشف الراتب.
/// </summary>
[Collection("provisioned")]
public class LeaveOvertimeTests
{
    private readonly ProvisionedFixture _f;
    public LeaveOvertimeTests(ProvisionedFixture f) => _f = f;

    // آذار 2024 (الأول منه جمعة): أشهر ماضية لا تمسّها اختبارات أخرى
    private static DateTime D(int month, int day) => new(2024, month, day);

    [Fact]
    public async Task Leaves_hourly_holidays_fingerprint_overtime_and_payroll()
    {
        Assert.True(_f.Install.Success, _f.Install.ErrorMessage);
        await using var db = _f.NewDb();
        var admin = _f.AdminLocalId;
        var leaves = new HrLeaveService(db);

        // الأنواع الافتراضية من النظام
        var types = await leaves.GetTypesAsync();
        LeaveType T(string n) => types.Single(t => t.Name == n);
        Assert.Equal((3m, LeaveAllowancePeriod.Month, true), (T("اعتيادية").AllowedDays!.Value, T("اعتيادية").AllowancePeriod, T("اعتيادية").IsRegular));
        Assert.Equal((30m, 4m, 7m), (T("مرضية").AllowedDays!.Value, T("وفاة قريب").AllowedDays!.Value, T("زواج").AllowedDays!.Value));
        Assert.Null(T("بدون راتب").AllowedDays);
        Assert.False(T("بدون راتب").IsPaid);
        Assert.Equal(2, (await leaves.GetPolicyAsync()).FreeHourlyLeavesPerMonth);

        var shift = new Shift { Name = "شفت 10 ساعات — اختبار الإجازات", CheckInTime = new TimeSpan(8, 0, 0), CheckInGraceMinutes = 10, CheckOutTime = new TimeSpan(18, 0, 0) };
        db.Shifts.Add(shift);
        await db.SaveChangesAsync();
        var emp = new Employee { FullName = "موظف الإجازات — اختبار", BaseSalary = 900_000, ShiftId = shift.Id, HireDate = D(1, 1), FingerprintCode = "LV-9001" };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        var regular = T("اعتيادية").Id;

        // ---------- 1) يومية اعتيادية أطول من رصيد الشهر: 3 من الرصيد و2 بدون راتب مع تنبيه ----------
        var five = new LeaveInput(emp.Id, regular, LeaveKind.Day, D(3, 3), D(3, 7), Note: "سفر");
        var (_, quote) = await leaves.QuoteAsync(five);
        Assert.Equal((5m, 3m, 2m), (quote!.WorkDays, quote.PaidDays, quote.UnpaidDays));
        Assert.Contains("بدون راتب", quote.Warning);
        var (r1, created, warning) = await leaves.RegisterAsync(five, admin);
        Assert.True(r1.Success, r1.ErrorMessage);
        Assert.NotNull(warning);
        var fiveDays = Assert.Single(created);
        // التداخل مرفوض
        Assert.False((await leaves.RegisterAsync(new LeaveInput(emp.Id, T("مرضية").Id, LeaveKind.Day, D(3, 6), D(3, 6)), admin)).result.Success);
        // الحضور يتبع الإجازة: أيام العمل «إجازة معتمدة»، والحضور اليدوي فيها يبقى إجازة
        Assert.Equal(5, await db.AttendanceRecords.CountAsync(a => a.EmployeeId == emp.Id && a.Status == AttendanceStatus.ApprovedLeave));
        Assert.True((await new HrService(db).SaveAttendanceAsync(D(3, 4), new[] { new AttendanceInput(emp.Id, null, new TimeSpan(8, 0, 0), null) })).Success);
        Assert.Equal(AttendanceStatus.ApprovedLeave, (await db.AttendanceRecords.AsNoTracking().SingleAsync(a => a.EmployeeId == emp.Id && a.AttendanceDate == D(3, 4))).Status);
        Assert.Equal("إجازة اعتيادية", (await leaves.LeaveTextsAsync(D(3, 5)))[emp.Id]);

        // ---------- 2) زمنية تُعفي من التأخير + عطلة رسمية بلا غياب، من ملف البصمة ----------
        Assert.True((await leaves.RegisterAsync(new LeaveInput(emp.Id, regular, LeaveKind.Hourly, D(3, 9), D(3, 9), new TimeSpan(8, 0, 0), new TimeSpan(10, 0, 0)), admin)).result.Success);
        Assert.True((await leaves.AddHolidayAsync(D(3, 10), "عطلة اختبار", admin)).Success);
        var fp = new FingerprintImportService(db);
        var preview = await fp.PreviewAsync(new[]
        {
            new FingerprintPunch("LV-9001", D(3, 9).AddHours(10).AddMinutes(5)), new FingerprintPunch("LV-9001", D(3, 9).AddHours(18)),
            new FingerprintPunch("LV-9001", D(3, 12).AddHours(8).AddMinutes(1)), new FingerprintPunch("LV-9001", D(3, 12).AddHours(18))
        });
        var mine = preview.Employees.Single(s => s.EmployeeId == emp.Id);
        Assert.Equal((2, 0, 1), (mine.PresentDays, mine.LateDays, mine.AbsentDays));   // 9 حاضر رغم 10:05، 10 عطلة، 11 غياب، 12 حاضر
        Assert.True((await fp.ApplyAsync(preview, "attlog.dat", admin)).result.Success);
        var att = await db.AttendanceRecords.AsNoTracking().Where(a => a.EmployeeId == emp.Id && a.AttendanceDate >= D(3, 9) && a.AttendanceDate <= D(3, 12))
                          .ToDictionaryAsync(a => a.AttendanceDate.Day, a => a.Status);
        Assert.Equal(AttendanceStatus.Present, att[9]);
        Assert.False(att.ContainsKey(10));
        Assert.Equal(AttendanceStatus.Absent, att[11]);
        Assert.Equal("زمنية 08:00–10:00", (await leaves.LeaveTextsAsync(D(3, 9)))[emp.Id]);

        // ---------- 3) الزمنية الثانية مسموحة، والثالثة يوم: لا رصيد اعتيادية في آذار ← يوم بدون راتب ----------
        var hourly2 = await leaves.RegisterAsync(new LeaveInput(emp.Id, regular, LeaveKind.Hourly, D(3, 13), D(3, 13), new TimeSpan(14, 0, 0), new TimeSpan(16, 0, 0)), admin);
        Assert.Equal((0m, 0m), (hourly2.leaves[0].PaidDays, hourly2.leaves[0].UnpaidDays));
        var hourly3 = await leaves.RegisterAsync(new LeaveInput(emp.Id, regular, LeaveKind.Hourly, D(3, 14), D(3, 14), new TimeSpan(9, 0, 0), new TimeSpan(10, 0, 0)), admin);
        Assert.True(hourly3.result.Success);
        Assert.Equal((0m, 1m), (hourly3.leaves[0].PaidDays, hourly3.leaves[0].UnpaidDays));
        Assert.Contains("بدون راتب", hourly3.warning);
        // في نيسان رصيد جديد (لا ترحيل): الثالثة تأخذ يومًا من الاعتيادية
        foreach (var day in new[] { 2, 3 })
            Assert.True((await leaves.RegisterAsync(new LeaveInput(emp.Id, regular, LeaveKind.Hourly, D(4, day), D(4, day), new TimeSpan(9, 0, 0), new TimeSpan(10, 0, 0)), admin)).result.Success);
        var april3rd = await leaves.RegisterAsync(new LeaveInput(emp.Id, regular, LeaveKind.Hourly, D(4, 4), D(4, 4), new TimeSpan(9, 0, 0), new TimeSpan(10, 0, 0)), admin);
        Assert.Equal((1m, 0m), (april3rd.leaves[0].PaidDays, april3rd.leaves[0].UnpaidDays));
        var (balances, hourlyTaken, free) = await leaves.BalancesAsync(emp.Id, D(4, 20));
        Assert.Equal((3, 2), (hourlyTaken, free));
        Assert.Equal(2m, balances.Single(b => b.TypeName == "اعتيادية").Remaining);

        // ---------- 4) يومية تعبر شهرين تُقسم سجلين ----------
        var split = await leaves.RegisterAsync(new LeaveInput(emp.Id, T("مرضية").Id, LeaveKind.Day, D(4, 29), D(5, 2), Note: "تقرير طبي 77"), admin);
        Assert.True(split.result.Success, split.result.ErrorMessage);
        Assert.Equal(2, split.leaves.Count);
        Assert.Equal((D(4, 30), D(5, 1)), (split.leaves[0].ToDate, split.leaves[1].FromDate));
        Assert.All(split.leaves, l => Assert.Equal(0m, l.UnpaidDays));

        // ---------- 5) العمل الإضافي: يوم الجمعة بالأجر اليومي + ساعات بسعر عام ثم خاص ----------
        var ot = new OvertimeService(db);
        Assert.True((await ot.AddAsync(emp.Id, D(3, 15), OvertimeKind.Day, 1, "جرد سنوي", admin)).Success);
        Assert.False((await ot.AddAsync(emp.Id, D(3, 15), OvertimeKind.Day, 1, null, admin)).Success);   // اليوم نفسه مرتين
        Assert.True((await ot.AddAsync(emp.Id, D(3, 12), OvertimeKind.Hours, 4, null, admin)).Success);
        Assert.True((await ot.SetRateAsync(null, 5_000, admin)).Success);                                  // للكل
        Assert.True((await ot.SetRateAsync(emp.Id, 7_000, admin)).Success);                                // خاص بالموظف
        var summary = (await ot.SummaryAsync(3, 2024)).Single(s => s.EmployeeId == emp.Id);
        Assert.Equal((1m, 4m, 30_000m, 7_000m, 58_000m), (summary.Days, summary.Hours, summary.DailyWage, summary.HourlyRate, summary.Amount));

        // ---------- 6) كشف الراتب: الإضافي يُضاف، وبدون راتب والغياب يُخصمان بالأجر اليومي ----------
        var hr = new HrService(db);
        var (gen, runId) = await hr.GenerateAsync(3, 2024);
        Assert.True(gen.Success, gen.ErrorMessage);
        var line = await db.PayrollLines.AsNoTracking().SingleAsync(l => l.PayrollRunId == runId && l.EmployeeId == emp.Id);
        Assert.Equal((1m, 4m, 58_000m), (line.OvertimeDays, line.OvertimeHours, line.OvertimeAmount));
        Assert.Equal((3m, 90_000m), (line.UnpaidLeaveDays, line.UnpaidLeaveDeduction));               // 2 من اليومية + 1 الزمنية الثالثة
        Assert.Equal(30_000m, line.AbsenceDeduction);                                                   // غياب 11 آذار
        Assert.Equal(900_000m + 58_000m - 90_000m - 30_000m, line.NetSalary);

        // سعر للكل يُزيل السعر الخاص
        Assert.True((await ot.SetRateAsync(null, 6_000, admin)).Success);
        Assert.Equal(54_000m, (await ot.SummaryAsync(3, 2024)).Single(s => s.EmployeeId == emp.Id).Amount);

        // ---------- 7) إلغاء اليومية: أيامها الماضية تعود غيابًا حتى يُعاد رفع البصمة، وتسقط من خصم بدون راتب ----------
        Assert.False((await leaves.VoidAsync(fiveDays.Id, " ", admin)).Success);
        Assert.True((await leaves.VoidAsync(fiveDays.Id, "أُلغي السفر", admin)).Success);
        Assert.Equal(5, await db.AttendanceRecords.CountAsync(a => a.EmployeeId == emp.Id && a.AttendanceDate >= D(3, 3) && a.AttendanceDate <= D(3, 7)
                                                              && a.Status == AttendanceStatus.Absent));
        Assert.Equal(1m, (await leaves.UnpaidDaysAsync(3, 2024))[emp.Id]);
    }
}
