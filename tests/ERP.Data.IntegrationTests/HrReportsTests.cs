using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>
/// تقارير الموارد البشرية: تقرير الموظف بكل بنوده لقسم أو موظف، وسجله التفصيلي، وحضور اليوم والمتأخرون بوقت دخولهم والغائبون
/// (الإجازة المسجّلة لا تُعدّ غيابًا).
/// </summary>
[Collection("controls")]
public class HrReportsTests
{
    private readonly ControlsFixture _f;
    public HrReportsTests(ControlsFixture f) => _f = f;

    [Fact]
    public async Task Employee_report_detail_and_attendance_late_absent_by_department()
    {
        await using var db = _f.NewDb(_f.AdminId);
        var dept = new Department { Name = "قسم تقارير الإنتاج" };
        var other = new Department { Name = "قسم تقارير المبيعات" };
        db.AddRange(dept, other);
        await db.SaveChangesAsync();
        var ali = new Employee { FullName = "علي تقارير", DepartmentId = dept.Id, JobTitle = "مشغل", BaseSalary = 600_000 };
        var omar = new Employee { FullName = "عمر تقارير", DepartmentId = dept.Id, BaseSalary = 600_000 };
        var sara = new Employee { FullName = "سارة تقارير", DepartmentId = other.Id, BaseSalary = 600_000 };
        db.AddRange(ali, omar, sara);
        await db.SaveChangesAsync();
        var day = new DateTime(2026, 9, 14);
        var leaveType = await db.LeaveTypes.FirstOrDefaultAsync() ?? db.LeaveTypes.Add(new LeaveType { Name = "اعتيادية" }).Entity;
        await db.SaveChangesAsync();

        db.AttendanceRecords.AddRange(
            new AttendanceRecord { EmployeeId = ali.Id, AttendanceDate = day, CheckInTime = new TimeSpan(8, 25, 0), CheckOutTime = new TimeSpan(16, 0, 0), LateMinutes = 25, Status = AttendanceStatus.Late },
            new AttendanceRecord { EmployeeId = ali.Id, AttendanceDate = day.AddDays(1), Status = AttendanceStatus.Absent },
            new AttendanceRecord { EmployeeId = omar.Id, AttendanceDate = day, CheckInTime = new TimeSpan(7, 55, 0), CheckOutTime = new TimeSpan(16, 0, 0), Status = AttendanceStatus.Present },
            new AttendanceRecord { EmployeeId = omar.Id, AttendanceDate = day.AddDays(1), Status = AttendanceStatus.Absent },   // لكنه في إجازة
            new AttendanceRecord { EmployeeId = sara.Id, AttendanceDate = day, Status = AttendanceStatus.Absent });
        db.EmployeeLeaves.AddRange(
            new EmployeeLeave { LeaveNumber = "LV-RPT-1", EmployeeId = omar.Id, LeaveTypeId = leaveType.Id, Kind = LeaveKind.Day, FromDate = day.AddDays(1), ToDate = day.AddDays(2),
                                WorkDays = 2, PaidDays = 1, UnpaidDays = 1, CreatedByUserId = _f.AdminId },
            new EmployeeLeave { LeaveNumber = "LV-RPT-2", EmployeeId = ali.Id, LeaveTypeId = leaveType.Id, Kind = LeaveKind.Hourly, FromDate = day.AddDays(3), ToDate = day.AddDays(3),
                                FromTime = new TimeSpan(10, 0, 0), ToTime = new TimeSpan(12, 30, 0), CreatedByUserId = _f.AdminId });
        db.EmployeeDeductions.AddRange(
            new EmployeeDeduction { DeductionNumber = "DD-RPT-1", EmployeeId = ali.Id, Kind = EmployeeDeductionKind.Penalty, Amount = 25_000, EntryDate = day, StartMonth = 9, StartYear = 2026, Reason = "تأخر متكرر", CreatedByUserId = _f.AdminId },
            new EmployeeDeduction { DeductionNumber = "DD-RPT-2", EmployeeId = ali.Id, Kind = EmployeeDeductionKind.Loan, Amount = 300_000, MonthlyInstallment = 100_000, EntryDate = day, StartMonth = 10, StartYear = 2026, CreatedByUserId = _f.AdminId },
            new EmployeeDeduction { DeductionNumber = "DD-RPT-3", EmployeeId = ali.Id, Kind = EmployeeDeductionKind.Penalty, Amount = 99_000, EntryDate = day, StartMonth = 9, StartYear = 2026, IsVoided = true, CreatedByUserId = _f.AdminId });
        db.PromotionsAndRaises.Add(new PromotionAndRaise { EmployeeId = ali.Id, MovementType = PromotionMovementType.Promotion, NewJobTitle = "رئيس خط", Amount = 50_000, EffectiveDate = day, CreatedByUserId = _f.AdminId });
        db.MonthlyIncentiveEvaluations.Add(new MonthlyIncentiveEvaluation { EmployeeId = ali.Id, PeriodMonth = 9, PeriodYear = 2026, AttendanceScoreAuto = 30, PerformanceScoreManual = 25, SkillsScoreManual = 25, TotalScore = 80, IncentiveAmount = 40_000 });
        var run = new PayrollRun { PeriodMonth = 9, PeriodYear = 2026 };
        run.Lines.Add(new PayrollLine { EmployeeId = ali.Id, BaseSalary = 600_000, AbsenceDeduction = 20_000, NetSalary = 580_000 });
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();

        var hr = new HrReportsService(db);
        var month = new HrReportRequest(new DateTime(2026, 9, 1), new DateTime(2026, 9, 30), dept.Id);

        // ---- تقرير الموظفين لقسم: علي بكل بنوده، والملغى لا يُحسب ----
        var summary = await hr.EmployeeSummaryAsync(month);
        Assert.Equal(new[] { "علي تقارير", "عمر تقارير" }, summary.Rows.Select(r => r[0]));
        Assert.Equal(new[] { "علي تقارير", "قسم تقارير الإنتاج", "1", "0", "1", "25", "1", "2.5", "1", "25,000", "20,000", "300,000", "1", "80", "40,000" },
                     summary.Rows[0]);
        Assert.Equal(new[] { "1", "2" }, new[] { summary.Rows[1][2], summary.Rows[1][3] });   // عمر: غياب مسجّل + إجازة يومين
        Assert.Contains(("قطع الرواتب", "20,000"), summary.Totals);
        Assert.Equal("تقرير الموظف", (await hr.EmployeeSummaryAsync(month with { DepartmentId = null, EmployeeId = ali.Id })).Title);

        // ---- السجل التفصيلي: كل حدث بتاريخه ----
        var detail = await hr.EmployeeDetailAsync(month with { EmployeeId = ali.Id });
        Assert.StartsWith("علي تقارير — قسم تقارير الإنتاج — مشغل", detail.Subtitle);
        Assert.Contains(detail.Rows, r => r[1] == "تأخر" && r[2] == "دخول 08:25 — تأخر 25 دقيقة");
        Assert.Contains(detail.Rows, r => r[1] == "غياب" && r[0] == "2026/09/15");
        Assert.Contains(detail.Rows, r => r[1] == "إجازة زمنية" && r[2].EndsWith("10:00–12:30"));
        Assert.Contains(detail.Rows, r => r[1] == "عقوبة" && r[3] == "25,000");
        Assert.Contains(detail.Rows, r => r[1] == "سلفة" && r[3] == "300,000");
        Assert.Contains(detail.Rows, r => r[1] == "ترقية" && r[2] == "إلى رئيس خط");
        Assert.Contains(detail.Rows, r => r[1] == "تقييم وحافز شهري" && r[3] == "40,000");
        Assert.Contains(detail.Rows, r => r[1] == "قطع راتب" && r[3] == "20,000");
        Assert.DoesNotContain(detail.Rows, r => r[3] == "99,000");
        Assert.Contains("اختر الموظف", (await hr.EmployeeDetailAsync(month)).Subtitle);

        // ---- حضور اليوم لقسم، والمتأخرون بوقت دخولهم، والغائبون (الإجازة ليست غيابًا) ----
        var today = await hr.AttendanceAsync(new HrReportRequest(day, day, dept.Id));
        Assert.Equal("حضور اليوم", today.Title);
        Assert.Contains(today.Rows, r => r[2] == "علي تقارير" && r[3] == "متأخر" && r[4] == "08:25" && r[6] == "25");
        Assert.Contains(today.Rows, r => r[2] == "عمر تقارير" && r[3] == "حاضر" && r[4] == "07:55");
        Assert.DoesNotContain(today.Rows, r => r[2] == "سارة تقارير");
        var late = await hr.AttendanceAsync(new HrReportRequest(day, day.AddDays(1), dept.Id), AttendanceStatus.Late);
        Assert.Equal("علي تقارير", Assert.Single(late.Rows)[2]);
        Assert.Contains(("مجموع دقائق التأخر", "25"), late.Totals);
        var absent = await hr.AttendanceAsync(new HrReportRequest(day, day.AddDays(1)), AttendanceStatus.Absent);
        Assert.Contains(absent.Rows, r => r[2] == "علي تقارير" && r[0] == "2026/09/15");
        Assert.Contains(absent.Rows, r => r[2] == "سارة تقارير" && r[0] == "2026/09/14");
        Assert.DoesNotContain(absent.Rows, r => r[2] == "عمر تقارير");                       // في إجازة
        var both = await hr.AttendanceAsync(new HrReportRequest(day.AddDays(1), day.AddDays(1), dept.Id));
        Assert.Contains(both.Rows, r => r[2] == "عمر تقارير" && r[3] == "إجازة");
    }
}
