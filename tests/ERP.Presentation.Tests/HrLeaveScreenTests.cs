using ERP.Data.ProjectDb.Entities;
using ERP.Presentation.ViewModels.HR;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>
/// شاشات الإجازات والعطل والعمل الإضافي كما يستعملها مسؤول الموارد البشرية: الأنواع والعطلة، إجازة يومية وزمنية مع
/// معاينة الاحتساب وطباعة الإشعار، الحضور يُظهر الإجازة بدل الغياب، العمل الإضافي مع تنبيه السعر غير المحدد وسعر خاص بموظف.
/// </summary>
[Collection("app")]
public class HrLeaveScreenTests
{
    private readonly AppFixture _f;
    public HrLeaveScreenTests(AppFixture f) => _f = f;

    // شباط 2025: أول يوم سبت، شهر ماضٍ لا تمسّه اختبارات أخرى
    private static DateTime D(int day) => new(2025, 2, day);

    [Fact]
    public async Task Leave_types_holiday_daily_and_hourly_leave_attendance_and_overtime_from_the_screens()
    {
        int empId;
        await using (var db = _f.NewDb())
        {
            var shift = new Shift { Name = "شفت شاشة الإجازات", CheckInTime = new TimeSpan(8, 0, 0), CheckInGraceMinutes = 10, CheckOutTime = new TimeSpan(18, 0, 0) };
            db.Shifts.Add(shift);
            await db.SaveChangesAsync();
            var e = new Employee { FullName = "موظفة شاشة الإجازات", BaseSalary = 750_000, ShiftId = shift.Id, HireDate = new DateTime(2024, 1, 1) };
            db.Employees.Add(e);
            await db.SaveChangesAsync();
            empId = e.Id;
        }

        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var hr = shell.Open<HrModuleViewModel>(ModuleCode.HR);
        async Task Open(SectionViewModel s) { hr.SelectedTab = s; await hr.LastActivation; await s.IdleAsync(); }
        void NoErrors(string step) => Assert.True(dialogs.Errors.Count == 0, $"{step}: " + string.Join(" | ", dialogs.Errors));

        // ---------- الأنواع (افتراضية قابلة للتعديل) والعطلة الرسمية ----------
        var settings = hr.LeaveSettings;
        await Open(settings);
        Assert.Equal(new[] { "اعتيادية", "مرضية", "وفاة قريب", "زواج", "بدون راتب" }, settings.Types.Select(t => t.Name));
        var regular = settings.Types.Single(t => t.Name == "اعتيادية");
        Assert.Equal(("3", LeaveAllowancePeriod.Month, true), (regular.Days, regular.Period.Value, regular.IsRegular));
        Assert.Equal("", settings.Types.Single(t => t.Name == "بدون راتب").Days);
        regular.Days = "abc";
        await settings.SaveTypesCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("غير صحيحة"));
        dialogs.Errors.Clear();
        regular.Days = "3";
        await settings.SaveTypesCommand.ExecuteAsync();
        NoErrors("حفظ الأنواع");
        settings.NewHolidayDate = D(10);
        settings.NewHolidayName = "عطلة شاشة الاختبار";
        await settings.AddHolidayCommand.ExecuteAsync();
        NoErrors("العطلة");
        Assert.Contains(settings.Holidays, h => h.HolidayDate == D(10));

        // ---------- إجازة يومية: المعاينة ثم التسجيل والإشعار والرصيد ----------
        var leaves = hr.Leaves;
        await Open(leaves);
        leaves.Employee = leaves.Employees.Single(e => e.Id == empId);
        leaves.Type = leaves.Types.Single(t => t.Name == "مرضية");
        leaves.FromDate = D(8);
        leaves.ToDate = D(11);                          // السبت 8 ← الثلاثاء 11، والاثنين 10 عطلة رسمية
        leaves.Note = "تقرير طبي 15";
        await leaves.IdleAsync();
        Assert.Equal("3 يوم عمل: 3 من رصيد «مرضية»", leaves.QuoteText);
        Assert.False(leaves.HasQuoteWarning);
        await leaves.RegisterCommand.ExecuteAsync();
        NoErrors("تسجيل اليومية");
        Assert.Contains(dialogs.Reports, r => r.Title == "إشعار إجازة مرضية");
        (leaves.Month, leaves.Year) = (2, 2025);
        await leaves.IdleAsync();
        var sick = Assert.Single(leaves.Rows, r => r.EmployeeId == empId);
        Assert.Equal("3 من الرصيد", sick.ChargeText);
        Assert.Equal(27m, leaves.Balances.Single(b => b.TypeName == "مرضية").Remaining);

        // ---------- إجازة زمنية: ضمن المسموح شهريًا ----------
        leaves.Kind = leaves.Kinds.Single(k => k.Value == LeaveKind.Hourly);
        leaves.FromDate = D(12);
        leaves.FromTime = "08:00";
        leaves.ToTime = "10:00";
        await leaves.IdleAsync();
        Assert.Contains("ضمن المسموح", leaves.QuoteText);
        await leaves.RegisterCommand.ExecuteAsync();
        NoErrors("تسجيل الزمنية");
        await leaves.IdleAsync();
        Assert.Contains("1 من 2", leaves.HourlyText);

        // ---------- الحضور: الإجازة بدل الغياب، والزمنية مع الحالة ----------
        var att = hr.Attendance;
        await Open(att);
        att.Date = D(9);
        await att.IdleAsync();
        Assert.Equal("إجازة مرضية", att.Rows.Single(r => r.EmployeeId == empId).SavedStatus);
        att.Date = D(12);
        await att.IdleAsync();
        var row12 = att.Rows.Single(r => r.EmployeeId == empId);
        row12.CheckIn = "10:04";                         // بعد نهاية الزمنية بأربع دقائق: حاضر لا متأخر
        await att.SaveCommand.ExecuteAsync();
        NoErrors("الحضور");
        await att.IdleAsync();
        Assert.Equal("حاضر — زمنية 08:00–10:00", att.Rows.Single(r => r.EmployeeId == empId).SavedStatus);

        // ---------- العمل الإضافي: تنبيه السعر غير المحدد ثم سعر خاص بالموظفة ----------
        var ot = hr.Overtime;
        await Open(ot);
        ot.Employee = ot.Employees.Single(e => e.Id == empId);
        ot.Kind = ot.Kinds.Single(k => k.Value == OvertimeKind.Hours);
        ot.Date = D(13);
        ot.Quantity = 3;
        await ot.AddCommand.ExecuteAsync();
        NoErrors("الساعات");
        Assert.Equal((2, 2025), (ot.Month, ot.Year));
        var mine = ot.Summary.Single(s => s.EmployeeId == empId);
        if (ot.GlobalRate == 0) Assert.Contains("موظفة شاشة الإجازات", ot.MissingRateText);
        ot.RateForEmployee = true;
        ot.RateEmployee = ot.Employees.Single(e => e.Id == empId);
        ot.Rate = 8_000;
        await ot.ApplyRateCommand.ExecuteAsync();
        NoErrors("السعر الخاص");
        await ot.IdleAsync();
        mine = ot.Summary.Single(s => s.EmployeeId == empId);
        Assert.Equal((3m, 8_000m, 24_000m), (mine.Hours, mine.HourlyRate, mine.Amount));
        Assert.True(mine.HasOwnRate);
        Assert.DoesNotContain("موظفة شاشة الإجازات", ot.MissingRateText);
        // يوم إضافي (الجمعة 14) بالأجر اليومي 25,000
        ot.Kind = ot.Kinds.Single(k => k.Value == OvertimeKind.Day);
        ot.Date = D(14);
        await ot.AddCommand.ExecuteAsync();
        NoErrors("اليوم الإضافي");
        await ot.IdleAsync();
        Assert.Equal(24_000m + 25_000m, ot.Summary.Single(s => s.EmployeeId == empId).Amount);

        // ---------- إلغاء الإجازة اليومية بسبب ----------
        await Open(leaves);
        (leaves.Month, leaves.Year) = (2, 2025);
        await leaves.IdleAsync();
        leaves.BeginVoidCommand.Execute(leaves.Rows.Single(r => r.EmployeeId == empId && r.Kind == LeaveKind.Day));
        Assert.True(leaves.IsVoiding);
        await leaves.VoidCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("سبب الإلغاء"));
        dialogs.Errors.Clear();
        leaves.VoidReason = "عاد للدوام";
        await leaves.VoidCommand.ExecuteAsync();
        NoErrors("الإلغاء");
        await leaves.IdleAsync();
        Assert.True(leaves.Rows.Single(r => r.EmployeeId == empId && r.Kind == LeaveKind.Day).IsVoided);

        await using (var db = _f.NewDb())
            Assert.Equal(AttendanceStatus.Absent, (await db.AttendanceRecords.AsNoTracking().SingleAsync(a => a.EmployeeId == empId && a.AttendanceDate == D(9))).Status);
        await shell.IdleAllAsync();
        Assert.Empty(_f.Unhandled);
    }
}
