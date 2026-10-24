using ERP.Data.ProjectDb.Entities;
using ERP.Presentation.ViewModels.Reps;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>كشف المحفظة يفتح على عمليات اليوم؛ ولأيام أخرى تُحدَّد الفترة (من / إلى) ويظهر الرصيد السابق.</summary>
[Collection("app")]
public class WalletStatementPeriodTests
{
    private readonly AppFixture _f;
    public WalletStatementPeriodTests(AppFixture f) => _f = f;

    [Fact]
    public async Task Wallet_statement_opens_on_today_and_a_period_shows_other_days_with_the_opening_balance()
    {
        int repId;
        await using (var db = _f.NewDb())
        {
            var rep = new Employee { FullName = "مندوب كشف الفترة", IsSalesRep = true, BaseSalary = 1 };
            db.Employees.Add(rep);
            await db.SaveChangesAsync();
            db.RepWalletTransactions.AddRange(
                new RepWalletTransaction { EmployeeId = rep.Id, AmountIn = 10_000, Description = "قديم", TransactionDate = DateTime.Today.AddDays(-5) },
                new RepWalletTransaction { EmployeeId = rep.Id, AmountOut = 2_000, Description = "أمس", TransactionDate = DateTime.Today.AddDays(-1) },
                new RepWalletTransaction { EmployeeId = rep.Id, AmountIn = 500, Description = "اليوم", TransactionDate = DateTime.Today });
            await db.SaveChangesAsync();
            repId = rep.Id;
        }
        var (shell, _) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var reps = shell.Open<RepsModuleViewModel>(ModuleCode.Reps);
        var wallet = reps.Wallet;
        reps.SelectedTab = wallet;
        await reps.IdleAsync();
        await wallet.IdleAsync();
        wallet.Rep = wallet.Reps.Single(r => r.Id == repId);
        await wallet.IdleAsync();

        Assert.Equal(DateTime.Today, wallet.StatementFrom.Date);
        Assert.Equal("اليوم", Assert.Single(wallet.Rows).Description);          // اليوم فقط
        Assert.Equal(8_000m, wallet.OpeningBalance);                              // 10,000 − 2,000 قبل اليوم
        Assert.Equal(8_500m, wallet.Balance);                                     // الرصيد الحالي كاملًا
        Assert.Contains("الرصيد السابق 8,000", wallet.PeriodHint);

        wallet.StatementFrom = DateTime.Today.AddDays(-5);
        await wallet.ShowStatementCommand.ExecuteAsync();
        Assert.Equal(new[] { "قديم", "أمس", "اليوم" }, wallet.Rows.Select(r => r.Description).ToArray());
        Assert.Equal(0m, wallet.OpeningBalance);

        wallet.StatementFrom = DateTime.Today.AddDays(-2);
        wallet.StatementTo = DateTime.Today.AddDays(-1);
        await wallet.ShowStatementCommand.ExecuteAsync();
        Assert.Equal("أمس", Assert.Single(wallet.Rows).Description);
        Assert.Equal(10_000m, wallet.OpeningBalance);
    }
}
