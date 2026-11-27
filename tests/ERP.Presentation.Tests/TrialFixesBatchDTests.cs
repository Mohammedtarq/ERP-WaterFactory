using ERP.Data.ProjectDb.Entities;
using ERP.Presentation.ViewModels.HR;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>
/// ملاحظات التجربة (الدفعة د): القسم يظهر ويُختار في الموظفين والحضور وتقييم الحوافز والرواتب،
/// والحضور يُدخل لموظف واحد دون تعبئة الجميع (يُحفظ المعدَّل فقط، ولا غياب تلقائي لمن لم يُلمس).
/// </summary>
[Collection("app")]
public class TrialFixesBatchDTests
{
    private readonly AppFixture _f;
    public TrialFixesBatchDTests(AppFixture f) => _f = f;

    [Fact]
    public async Task Departments_filter_hr_screens_and_attendance_saves_only_the_touched_employee()
    {
        int deptId, aliId, saraId, noDeptId;
        await using (var db = _f.NewDb())
        {
            var dept = new Department { Name = "قسم التعبئة د" };
            db.Departments.Add(dept);
            await db.SaveChangesAsync();
            var ali = new Employee { FullName = "علي قسم التعبئة", DepartmentId = dept.Id, BaseSalary = 500_000, HireDate = DateTime.Today.AddYears(-1) };
            var sara = new Employee { FullName = "سارة قسم التعبئة", DepartmentId = dept.Id, BaseSalary = 500_000, HireDate = DateTime.Today.AddYears(-1) };
            var none = new Employee { FullName = "موظف بلا قسم د", BaseSalary = 500_000, HireDate = DateTime.Today.AddYears(-1) };
            db.Employees.AddRange(ali, sara, none);
            await db.SaveChangesAsync();
            (deptId, aliId, saraId, noDeptId) = (dept.Id, ali.Id, sara.Id, none.Id);
        }

        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var hr = shell.Open<HrModuleViewModel>(ModuleCode.HR);

        // (9) الموظفون: عمود القسم ومرشّحه، والبحث باسم القسم
        var emps = hr.Section<EmployeesSectionViewModel>();
        hr.SelectedTab = emps;
        await hr.LastActivation;
        await emps.IdleAsync();
        emps.DepartmentFilter = emps.DepartmentFilters.Single(d => d.Id == deptId);
        Assert.Equal(new[] { aliId, saraId }.OrderBy(x => x), emps.Items.Select(e => e.Id).OrderBy(x => x));
        emps.DepartmentFilter = emps.DepartmentFilters.Single(d => d.Name == DepartmentChoice.NoDepartment);
        Assert.Contains(emps.Items, e => e.Id == noDeptId);
        Assert.All(emps.Items, e => Assert.Null(e.DepartmentId));
        emps.DepartmentFilter = DepartmentChoice.All;
        emps.SearchText = "قسم التعبئة د";
        Assert.Equal(2, emps.Items.Count(e => e.DepartmentId == deptId));
        emps.SearchText = "";

        // (10) الحضور: قسم واحد، ثم موظف واحد بالبحث، ويُحفظ هو وحده
        var att = hr.Attendance;
        hr.SelectedTab = att;
        await hr.LastActivation;
        await att.IdleAsync();
        att.Department = att.Departments.Single(d => d.Id == deptId);
        Assert.Equal(new[] { "سارة قسم التعبئة", "علي قسم التعبئة" }, att.VisibleRows.Select(r => r.EmployeeName).OrderBy(n => n));
        Assert.All(att.VisibleRows, r => Assert.Equal("قسم التعبئة د", r.DepartmentName));
        att.Filter = "علي";
        var aliRow = Assert.Single(att.VisibleRows);
        aliRow.CheckIn = "08:00";
        aliRow.CheckOut = "15:00";
        Assert.Equal(1, att.ChangedCount);
        await att.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Contains("لـ 1 موظف", att.StatusMessage);
        await using (var db = _f.NewDb())
        {
            var mine = await db.AttendanceRecords.Where(a => a.AttendanceDate == DateTime.Today && new[] { aliId, saraId, noDeptId }.Contains(a.EmployeeId)).ToListAsync();
            var rec = Assert.Single(mine);                                    // سارة والموظف بلا قسم: لا غياب تلقائي
            Assert.Equal(aliId, rec.EmployeeId);
        }
        Assert.Equal(0, att.ChangedCount);
        await att.SaveCommand.ExecuteAsync();
        Assert.Contains("لا تعديلات", att.StatusMessage);

        // «تعبئة الحضور للظاهر» تمس الظاهر فقط
        att.Filter = "";
        att.Department = att.Departments.Single(d => d.Id == deptId);
        att.AllPresentCommand.Execute(null);
        Assert.Equal(1, att.ChangedCount);                                    // سارة فقط (علي مسجّل)
        Assert.True(att.Rows.Single(r => r.EmployeeId == saraId).IsChanged);
        Assert.False(att.Rows.Single(r => r.EmployeeId == noDeptId).IsChanged);

        // (11) تقييم الحوافز: القسم يظهر ويُختار، والحفظ للظاهر فقط
        var inc = hr.Incentives;
        hr.SelectedTab = inc;
        await hr.LastActivation;
        await inc.IdleAsync();
        inc.Month = DateTime.Today.Month;
        inc.Year = DateTime.Today.Year;
        await inc.IdleAsync();
        inc.Department = inc.Departments.Single(d => d.Id == deptId);
        Assert.Equal(2, inc.VisibleRows.Count);
        Assert.All(inc.VisibleRows, r => Assert.Equal("قسم التعبئة د", r.DepartmentName));
        await inc.SaveAllCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Contains("قسم قسم التعبئة د", inc.StatusMessage);
        await using (var db = _f.NewDb())
        {
            var evaluated = await db.MonthlyIncentiveEvaluations.Where(e => e.PeriodMonth == DateTime.Today.Month && e.PeriodYear == DateTime.Today.Year)
                .Select(e => e.EmployeeId).ToListAsync();
            Assert.Contains(aliId, evaluated);
            Assert.Contains(saraId, evaluated);
            Assert.DoesNotContain(noDeptId, evaluated);
        }

        // الرواتب: اختيار القسم متاح ويُرشّح الجدول
        var pay = hr.Payroll;
        hr.SelectedTab = pay;
        await hr.LastActivation;
        await pay.IdleAsync();
        Assert.Contains(pay.Departments, d => d.Id == deptId);
        pay.Department = pay.Departments.Single(d => d.Id == deptId);
        Assert.All(pay.VisibleRows, r => Assert.Equal(deptId, r.DepartmentId));

        await shell.IdleAllAsync();
        Assert.Empty(_f.Unhandled);
    }
}
