using ERP.Data.ProjectDb.Entities;
using ERP.Presentation.ViewModels.Finance;
using ERP.Presentation.ViewModels.HR;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>وحدة الموارد البشرية كما يستخدمها المستخدم: شفت ← موظف ← حضور ← تقييم ← رواتب ← اعتماد.</summary>
[Collection("app")]
public class HrScreenTests
{
    private readonly AppFixture _f;
    public HrScreenTests(AppFixture f) => _f = f;

    private static async Task Open(ModuleViewModel module, SectionViewModel section)
    {
        module.SelectedTab = section;
        await module.IdleAsync();
        await section.IdleAsync();
    }

    [Fact]
    public async Task Hr_module_end_to_end()
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var hr = shell.Open<HrModuleViewModel>(ModuleCode.HR);
        Assert.Equal(10, hr.Home.Sections.Count());

        // 1) شفت صباحي
        var shifts = hr.Section<ShiftsSectionViewModel>();
        await Open(hr, shifts);
        await shifts.NewCommand.ExecuteAsync();
        shifts.Editor!.Name = "الصباحي";
        await shifts.SaveCommand.ExecuteAsync();
        var shiftId = shifts.Items.Single(s => s.Name == "الصباحي").Id;

        // 2) موظف على الشفت
        var emps = hr.Section<EmployeesSectionViewModel>();
        await Open(hr, emps);
        await emps.NewCommand.ExecuteAsync();
        emps.Editor!.FullName = "موظف الإنتاج";
        emps.Editor.BaseSalary = 600_000;
        emps.Editor.ShiftId = shiftId;
        emps.Editor.HireDate = new DateTime(2020, 1, 1);
        await emps.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        var empId = emps.Items.Single(e => e.FullName == "موظف الإنتاج").Id;

        // 3) الحضور: أول يوم من الشهر الماضي، دخول 08:20 ← متأخر 20 دقيقة
        var att = hr.Attendance;
        await Open(hr, att);
        var day = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-1);
        att.Date = day;
        await att.IdleAsync();
        var row = att.Rows.Single(r => r.EmployeeId == empId);
        row.CheckIn = "8:2x";
        await att.SaveCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("وقت غير صحيح"));
        dialogs.Errors.Clear();
        row.CheckIn = "08:20";
        row.CheckOut = "16:00";
        await att.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        row = att.Rows.Single(r => r.EmployeeId == empId);
        Assert.Equal("متأخر", row.SavedStatus);
        Assert.Equal(20, row.SavedLateMinutes);

        // 4) مقياس الحافز: كل النقاط 0–100 ← 25,000
        var settings = hr.Section<IncentiveSettingsSectionViewModel>();
        await Open(hr, settings);
        Assert.Equal("المجموع 100 ✓", settings.WeightsTotalText);
        await settings.NewCommand.ExecuteAsync();
        settings.Editor!.MinScore = 0;
        settings.Editor.MaxScore = 100;
        settings.Editor.Amount = 25_000;
        await settings.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);

        // 5) التقييم: الانضباط = 50 (يوم متأخر واحد)، أداء 80، مهارات 80 ← (50×40+80×30+80×30)/100 = 68
        var inc = hr.Incentives;
        await Open(hr, inc);
        Assert.Equal(day.Month, inc.Month);
        var irow = inc.Rows.Single(r => r.EmployeeId == empId);
        Assert.Equal(50m, irow.AttendanceScore);
        irow.Performance = 80;
        irow.Skills = 80;
        await inc.SaveAllCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        irow = inc.Rows.Single(r => r.EmployeeId == empId);
        Assert.Equal(68m, irow.TotalScore);
        Assert.Equal(25_000m, irow.Amount);

        // 6) الرواتب: قاعدة قيد الرواتب تُضاف تلقائيًا عند فتح المشروع (الترقية الذاتية) ← توليد ← اعتماد
        var fin = shell.Open<FinanceModuleViewModel>(ModuleCode.Finance);
        var rules = fin.Section<MappingRulesSectionViewModel>();
        await Open(fin, rules);
        Assert.Contains(rules.Items, r => r.TransactionType == "PayrollAccrual");
        Assert.Equal("كل القواعد المطلوبة معرّفة ✓", rules.MissingText);

        var pay = hr.Payroll;
        shell.Open<HrModuleViewModel>(ModuleCode.HR);
        await Open(hr, pay);
        Assert.Null(pay.RunId);
        await pay.GenerateCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        var prow = pay.Rows.Single(r => r.EmployeeName == "موظف الإنتاج");
        Assert.Equal(600_000m, prow.BaseSalary);
        Assert.Equal(25_000m, prow.MonthlyIncentive);
        Assert.Equal(625_000m, prow.NetSalary);
        Assert.Contains("مسودة", pay.StatusText);

        await pay.ApproveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.True(pay.IsApproved);
        Assert.Contains(dialogs.Infos, i => i.Contains("تم اعتماد رواتب"));
        Assert.Equal(pay.Summary!.TotalNetIqd, pay.Summary.TotalInIqd);

        await using var db = _f.NewDb();
        var je = await db.JournalEntries.Include(j => j.Lines).SingleAsync(j => j.EntryType == JournalEntryType.AutoPayroll);
        Assert.True(je.IsBalanced && je.IsPosted);

        // الشهر مقفل: الحضور والتوليد يُرفضان
        att.Date = day;
        await att.IdleAsync();
        await att.SaveCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("معتمدة"));
        Assert.Empty(_f.Unhandled);
    }
}
