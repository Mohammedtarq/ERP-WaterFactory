using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>الضمان الاجتماعي والتكافل الاجتماعي: استقطاع ثابت من الراتب عند التفعيل، وقيد مستحقاتهما عند الاعتماد.</summary>
[Collection("controls")]
public class SocialSecurityTests
{
    private readonly ControlsFixture _f;
    public SocialSecurityTests(ControlsFixture f) => _f = f;

    [Fact]
    public async Task Fixed_social_security_and_solidarity_reduce_net_and_post_as_liabilities()
    {
        await using var db = _f.NewDb(_f.AdminId);
        // شهر مستقبلي بعيد خاص بهذا الاختبار
        var period = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(7);
        var both = new Employee { FullName = "موظف بضمان وتكافل", BaseSalary = 1_000_000, HasSocialSecurity = true, SocialSecurityAmount = 50_000,
                                  HasSocialSolidarity = true, SocialSolidarityAmount = 10_000, HireDate = period.AddYears(-1) };
        var disabled = new Employee { FullName = "موظف بمبلغ غير مفعّل", BaseSalary = 800_000, HasSocialSecurity = false, SocialSecurityAmount = 40_000,
                                      HireDate = period.AddYears(-1) };
        db.Employees.AddRange(both, disabled);
        await db.SaveChangesAsync();

        var hr = new HrService(db);
        var (gen, runId) = await hr.GenerateAsync(period.Month, period.Year);
        Assert.True(gen.Success, gen.ErrorMessage);
        var lines = await db.PayrollLines.AsNoTracking().Where(l => l.PayrollRunId == runId).ToDictionaryAsync(l => l.EmployeeId);
        Assert.Equal((50_000m, 10_000m, 940_000m), (lines[both.Id].SocialSecurityDeduction, lines[both.Id].SocialSolidarityDeduction, lines[both.Id].NetSalary));
        Assert.Equal((0m, 0m, 800_000m), (lines[disabled.Id].SocialSecurityDeduction, lines[disabled.Id].SocialSolidarityDeduction, lines[disabled.Id].NetSalary));

        // المبلغ قابل للتعديل: إعادة التوليد تأخذ القيمة الجديدة
        await db.Employees.Where(e => e.Id == both.Id).ExecuteUpdateAsync(u => u.SetProperty(e => e.SocialSecurityAmount, 55_000m));
        Assert.True((await hr.GenerateAsync(period.Month, period.Year)).result.Success);
        var line = await db.PayrollLines.AsNoTracking().SingleAsync(l => l.PayrollRunId == runId && l.EmployeeId == both.Id);
        Assert.Equal((55_000m, 935_000m), (line.SocialSecurityDeduction, line.NetSalary));

        var (approved, _) = await hr.ApproveAsync(runId!.Value, _f.AdminId);
        Assert.True(approved.Success, approved.ErrorMessage);
        var run = await db.PayrollRuns.AsNoTracking().Include(r => r.Lines).SingleAsync(r => r.Id == runId);
        var je = await db.JournalEntries.AsNoTracking().Include(j => j.Lines).ThenInclude(l => l.Account).SingleAsync(j => j.Id == run.JournalEntryId);
        Assert.Equal(je.Lines.Sum(l => l.Debit), je.Lines.Sum(l => l.Credit));
        var socialTotal = run.Lines.Where(l => l.Currency == "IQD").Sum(l => l.SocialSecurityDeduction);
        var solidarityTotal = run.Lines.Where(l => l.Currency == "IQD").Sum(l => l.SocialSolidarityDeduction);
        Assert.True(socialTotal >= 55_000m);
        Assert.Equal(socialTotal, je.Lines.Where(l => l.Account.AccountCode == "2105").Sum(l => l.Credit));
        Assert.Equal(solidarityTotal, je.Lines.Where(l => l.Account.AccountCode == "2106").Sum(l => l.Credit));

        // لا تمر بيانات هذين الموظفين على اختبارات أخرى
        await db.Employees.Where(e => e.Id == both.Id || e.Id == disabled.Id).ExecuteUpdateAsync(u => u.SetProperty(e => e.IsActive, false));
    }
}
