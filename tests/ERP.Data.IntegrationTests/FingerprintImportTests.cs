using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>م5: استيراد ملف بصمة ZKTeco ببيانات مصطنعة (لا يُستعمل ملف حقيقي في الاختبارات).</summary>
[Collection("controls")]
public class FingerprintImportTests
{
    private readonly ControlsFixture _f;
    public FingerprintImportTests(ControlsFixture f) => _f = f;

    [Fact]
    public void Parser_reads_zkteco_lines_and_counts_unreadable_ones()
    {
        var text = "      901\t2026-09-01 05:50:10\t1\t0\t1\t0\r\n  902\t2026-09-01 16:00:00\t1\t1\t1\t0\r\nبيانات تالفة\r\n903 2026-09-01 07:00:00\r\n\r\n";
        var (punches, skipped) = FingerprintImportService.Parse(text);
        Assert.Equal(3, punches.Count);
        Assert.Equal(1, skipped);
        Assert.Equal(("901", new DateTime(2026, 9, 1, 5, 50, 10)), (punches[0].Code, punches[0].Time));
        Assert.Equal("903", punches[2].Code);
    }

    [Fact]
    public async Task Import_classifies_day_night_and_entry_only_shifts_and_keeps_leaves()
    {
        await using var db = _f.NewDb(_f.AdminId);
        var day = new Shift { Name = "بصمة صباحي", CheckInTime = new TimeSpan(6, 0, 0), CheckOutTime = new TimeSpan(16, 0, 0), CheckInGraceMinutes = 10 };
        var night = new Shift { Name = "بصمة مسائي", CheckInTime = new TimeSpan(16, 0, 0), CheckOutTime = new TimeSpan(2, 0, 0), CheckInGraceMinutes = 10 };
        var sales = new Shift { Name = "بصمة مبيعات", CheckInTime = new TimeSpan(7, 0, 0), CheckOutTime = new TimeSpan(15, 0, 0), CheckInGraceMinutes = 10, IsEntryOnly = true };
        db.Shifts.AddRange(day, night, sales);
        await db.SaveChangesAsync();
        var a = new Employee { FullName = "بصمة نهاري", BaseSalary = 600_000, ShiftId = day.Id, FingerprintCode = "9101" };
        var b = new Employee { FullName = "بصمة ليلي", BaseSalary = 600_000, ShiftId = night.Id, FingerprintCode = "9102" };
        var c = new Employee { FullName = "بصمة مندوب", BaseSalary = 600_000, ShiftId = sales.Id, FingerprintCode = "9103" };
        var x = new Employee { FullName = "بصمة معفى", BaseSalary = 600_000, AttendanceExempt = true, FingerprintCode = "9104" };
        db.Employees.AddRange(a, b, c, x);
        await db.SaveChangesAsync();

        // أسبوع مضى: الاثنين ... الأحد (الجمعة عطلة افتراضية)
        var mon = DateTime.Today.AddDays(-30);
        while (mon.DayOfWeek != DayOfWeek.Monday) mon = mon.AddDays(-1);
        DateTime At(int d, int h, int m) => mon.AddDays(d).AddHours(h).AddMinutes(m);
        string L(string code, DateTime t) => $"  {code}\t{t:yyyy-MM-dd HH:mm:ss}\t1\t0\t1\t0";
        var lines = new List<string>
        {
            L("9101", At(0, 5, 50)), L("9101", At(0, 5, 51)), L("9101", At(0, 16, 5)),   // حاضر، والبصمة المكررة تُدمج
            L("9101", At(1, 6, 25)),                                                       // متأخر 25 ولا خروج
            //                                                                                الأربعاء: غياب
            L("9101", At(3, 6, 0)), L("9101", At(3, 16, 0)),
            L("9101", At(5, 6, 5)), L("9101", At(5, 16, 0)),                               // السبت: إجازة معتمدة مسبقًا تبقى
            L("9102", At(0, 16, 0)), L("9102", At(1, 2, 10)),                              // ليلي: الخروج بعد منتصف الليل لليوم نفسه
            L("9102", At(1, 16, 20)), L("9102", At(2, 1, 50)),
            L("9103", At(0, 7, 0)),                                                        // دخول فقط
            L("9104", At(0, 9, 0)),                                                        // معفى: يُتجاهل
            L("99999", At(0, 6, 0)), L("99999", At(1, 6, 0)),                              // رقم غير مربوط
        };
        var hr = new HrService(db);
        Assert.True((await hr.SaveAttendanceAsync(mon.AddDays(5), new[] { new AttendanceInput(a.Id, AttendanceStatus.ApprovedLeave, null, null) })).Success);

        var svc = new FingerprintImportService(db);
        var (punches, skipped) = FingerprintImportService.Parse(string.Join("\r\n", lines));
        Assert.Equal(0, skipped);
        var preview = await svc.PreviewAsync(punches);
        Assert.Equal(1, preview.Duplicates);
        Assert.Equal((mon, mon.AddDays(5)), (preview.PeriodFrom, preview.PeriodTo));
        var sa = preview.Employees.Single(e => e.EmployeeId == a.Id);
        Assert.Equal((2, 1, 1, 1, 1), (sa.PresentDays, sa.LateDays, sa.AbsentDays, sa.MissingCheckOut, sa.KeptLeaves));
        var sb = preview.Employees.Single(e => e.EmployeeId == b.Id);
        Assert.Equal((1, 1, 0), (sb.PresentDays, sb.LateDays, sb.MissingCheckOut));
        var sc = preview.Employees.Single(e => e.EmployeeId == c.Id);
        Assert.Equal((1, 0), (sc.PresentDays, sc.MissingCheckOut));                        // الدخول يكفي
        Assert.Equal(4, sc.AbsentDays);                                                      // الثلاثاء والأربعاء والخميس والسبت
        Assert.DoesNotContain(preview.Employees, e => e.EmployeeId == x.Id);
        Assert.Contains(preview.Warnings, w => w.Contains("معفى"));
        var un = Assert.Single(preview.Unmapped, u => u.Code == "99999");
        Assert.Equal((2, 2), (un.Punches, un.Days));

        var (applied, days) = await svc.ApplyAsync(preview, "attlog.dat", _f.AdminId);
        Assert.True(applied.Success, applied.ErrorMessage);
        Assert.Equal(preview.Plan.Count, days);

        async Task<AttendanceRecord?> Rec(Employee e, int d) =>
            await db.AttendanceRecords.AsNoTracking().SingleOrDefaultAsync(r => r.EmployeeId == e.Id && r.AttendanceDate == mon.AddDays(d));
        var r0 = (await Rec(a, 0))!;
        Assert.Equal((AttendanceStatus.Present, new TimeSpan(5, 50, 0), (TimeSpan?)new TimeSpan(16, 5, 0)), (r0.Status, r0.CheckInTime!.Value, r0.CheckOutTime));
        var r1 = (await Rec(a, 1))!;
        Assert.Equal((AttendanceStatus.Late, 25, (TimeSpan?)null), (r1.Status, r1.LateMinutes, r1.CheckOutTime));
        Assert.Equal(AttendanceStatus.Absent, (await Rec(a, 2))!.Status);
        Assert.Null(await Rec(a, 4));                                                        // الجمعة عطلة
        Assert.Equal(AttendanceStatus.ApprovedLeave, (await Rec(a, 5))!.Status);
        var n0 = (await Rec(b, 0))!;
        Assert.Equal((new TimeSpan(16, 0, 0), (TimeSpan?)new TimeSpan(2, 10, 0)), (n0.CheckInTime!.Value, n0.CheckOutTime));
        var n1 = (await Rec(b, 1))!;
        Assert.Equal((AttendanceStatus.Late, 20, (TimeSpan?)new TimeSpan(1, 50, 0)), (n1.Status, n1.LateMinutes, n1.CheckOutTime));
        Assert.Null(await Rec(x, 0));
        Assert.True(await db.FingerprintImports.AnyAsync(i => i.PeriodFrom == mon && i.UnmappedCodes!.Contains("99999")));

        // إعادة الاستيراد نفسه تحدّث ولا تكرر
        var before = await db.AttendanceRecords.CountAsync(r => r.EmployeeId == a.Id);
        Assert.True((await svc.ApplyAsync(await svc.PreviewAsync(punches), "attlog.dat", _f.AdminId)).result.Success);
        Assert.Equal(before, await db.AttendanceRecords.CountAsync(r => r.EmployeeId == a.Id));

        // الربط من الشاشة: رقم فريد
        Assert.False((await svc.LinkAsync("9101", b.Id, _f.AdminId)).Success);
        var z = new Employee { FullName = "بصمة جديد", BaseSalary = 500_000, ShiftId = day.Id };
        db.Employees.Add(z);
        await db.SaveChangesAsync();
        Assert.True((await svc.LinkAsync("99999", z.Id, _f.AdminId)).Success);
        Assert.DoesNotContain((await svc.PreviewAsync(punches)).Unmapped, u => u.Code == "99999");

        // لا تمر بيانات هؤلاء الموظفين على اختبارات أخرى
        await db.Employees.Where(e => new[] { a.Id, b.Id, c.Id, x.Id, z.Id }.Contains(e.Id))
                .ExecuteUpdateAsync(u => u.SetProperty(e => e.IsActive, false).SetProperty(e => e.FingerprintCode, (string?)null));
    }
}
