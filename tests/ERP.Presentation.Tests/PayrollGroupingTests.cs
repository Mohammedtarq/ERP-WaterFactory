using ERP.Presentation.ViewModels.HR;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>تشغيل الرواتب بمجموعتين (المستحقات / الاستقطاعات) مع مجموع لكل مجموعة، والحوافز الثلاثة في عمود واحد.</summary>
public class PayrollGroupingTests
{
    [Fact]
    public void Earnings_and_deductions_are_summed_per_group()
    {
        var row = new PayrollRow
        {
            BaseSalary = 650_000, Allowances = 50_000, OvertimeAmount = 30_000,
            RepIncentive = 10_000, ManagerIncentive = 5_000, MonthlyIncentive = 10_000,
            AbsenceDeduction = 0, UnpaidLeaveDeduction = 0, LoanDeduction = 50_000, WithdrawalDeduction = 0,
            PenaltyDeduction = 0, SocialSecurity = 32_500, SocialSolidarity = 6_500
        };
        Assert.Equal(25_000m, row.Incentives);
        Assert.Equal("المندوب 10,000 · المدير 5,000 · الشهري 10,000", row.IncentivesHint);
        Assert.Equal(755_000m, row.TotalEarnings);
        Assert.Equal(89_000m, row.TotalDeductions);
        Assert.Equal("", new PayrollRow().IncentivesHint);
    }
}
