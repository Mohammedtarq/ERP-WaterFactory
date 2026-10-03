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
        Assert.Equal(11, hr.Home.Sections.Count());

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
        await pay.PrintCommand.ExecuteAsync();
        Assert.StartsWith("كشف رواتب", dialogs.Reports.Last().Title);
        Assert.Contains(dialogs.Reports.Last().Rows, r => r[1] == "موظف الإنتاج" && r[10] == "625,000");

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
    [Fact]
    public async Task Loan_withdrawal_and_penalty_screen_with_receipts_and_admin_void()
    {
        // موظف خاص بالاختبار؛ يُوقَف في النهاية حتى لا يدخل رواتب الاختبارات الأخرى
        int empId;
        await using (var db = _f.NewDb())
        {
            var e = new Employee { FullName = "عامل التعبئة — شاشة السلف", BaseSalary = 500_000 };
            db.Employees.Add(e);
            await db.SaveChangesAsync();
            empId = e.Id;
        }
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        await using (var db = _f.NewDb())
        {
            // تمويل الصندوق بقدر ما سيُصرف بالضبط، فيبقى رصيده كما كان بعد الاختبار
            var cash = new Data.Services.CashBoxService(db);
            var boxId = await db.CashBoxes.Where(b => b.IsActive && b.IsDefault).Select(b => b.Id).FirstAsync();
            var adminId = await db.Users.Where(u => u.Username == AppFixture.AdminUser).Select(u => u.Id).SingleAsync();
            Assert.True((await cash.DepositAsync(boxId, 260_000, DateTime.Today, "تمويل سلف الاختبار", null, adminId)).result.Success);
        }

        var hr = shell.Open<HrModuleViewModel>(ModuleCode.HR);
        await hr.IdleAsync();
        var ded = hr.Deductions;
        await Open(hr, ded);
        ded.Employee = ded.Employees.Single(e => e.Id == empId);

        // سلفة بأقساط
        ded.Kind = ded.Kinds.Single(k => k.Value == EmployeeDeductionKind.Loan);
        Assert.True(ded.IsLoan);
        ded.Amount = 200_000;
        ded.Installment = 50_000;
        Assert.Equal("عدد الأقساط: 4 شهر", ded.InstallmentsText);
        await ded.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        var loanReceipt = dialogs.Reports.Last();
        Assert.Equal("سند صرف سلفة موظف", loanReceipt.Title);
        Assert.Contains(loanReceipt.Totals, t => t.Label == "القسط الشهري" && t.Value == "50,000 د.ع");
        Assert.Contains(loanReceipt.Totals, t => t.Label == "عدد الأقساط" && t.Value == "4");

        // مسحوب
        ded.Kind = ded.Kinds.Single(k => k.Value == EmployeeDeductionKind.Withdrawal);
        ded.Amount = 60_000;
        await ded.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Equal("سند صرف مسحوب من الراتب", dialogs.Reports.Last().Title);

        // عقوبة: السبب إلزامي
        ded.Kind = ded.Kinds.Single(k => k.Value == EmployeeDeductionKind.Penalty);
        Assert.True(ded.IsPenalty);
        ded.Amount = 15_000;
        await ded.SaveCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("سبب العقوبة"));
        dialogs.Errors.Clear();
        ded.Reason = "غياب دون إذن";
        await ded.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        var notice = dialogs.Reports.Last();
        Assert.Equal("إشعار عقوبة (خصم من الراتب)", notice.Title);
        Assert.Contains(notice.HeaderFields, f => f.Label == "سبب العقوبة" && f.Value == "غياب دون إذن");

        // السجل مصفّى على الموظف: ثلاثة بانتظار الراتب
        ded.FilterEmployee = ded.Employees.Single(e => e.Id == empId);
        await ded.IdleAsync();
        Assert.Equal(3, ded.Rows.Count);
        Assert.All(ded.Rows, r => Assert.Equal("بانتظار الراتب", r.StatusText));
        Assert.Equal(275_000, ded.TotalRemaining);

        // إلغاء العقوبة (للأدمن) بسبب
        var penaltyRow = ded.Rows.Single(r => r.Kind == EmployeeDeductionKind.Penalty);
        ded.BeginVoidCommand.Execute(penaltyRow);
        ded.VoidReason = "أُلغيت بقرار المدير";
        await ded.VoidCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.DoesNotContain(ded.Rows, r => r.Id == penaltyRow.Id);           // "غير المُستقطع بالكامل فقط"
        ded.OpenOnly = false;
        await ded.IdleAsync();
        Assert.Equal("ملغى", ded.Rows.Single(r => r.Id == penaltyRow.Id).StatusText);

        await using (var db = _f.NewDb())
        {
            var e = await db.Employees.SingleAsync(x => x.Id == empId);
            e.IsActive = false;
            await db.SaveChangesAsync();
        }
        Assert.Empty(_f.Unhandled);
    }
}
