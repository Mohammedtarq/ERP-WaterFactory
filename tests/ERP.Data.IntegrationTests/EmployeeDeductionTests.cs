using ERP.Data.ProjectDb.Entities;
using ERP.Data.Security;
using ERP.Data.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>سلف (أقساط) ومسحوبات وعقوبات الموظفين: الصرف من الصندوق، الاستقطاع التلقائي في الرواتب، قيد الاعتماد، والإلغاء.</summary>
[Collection("provisioned")]
public class EmployeeDeductionTests
{
    private readonly ProvisionedFixture _f;
    public EmployeeDeductionTests(ProvisionedFixture f) => _f = f;

    [Fact]
    public async Task Loan_installments_withdrawals_and_penalties_flow_through_payroll()
    {
        Assert.True(_f.Install.Success, _f.Install.ErrorMessage);
        await using var db = _f.NewDb();
        var admin = _f.AdminLocalId;
        // شهور بعيدة لا تمسّها اختبارات أخرى على نفس المشروع
        const int y = 2031;
        var emp = new Employee { FullName = "سائق الرافعة — اختبار الاستقطاع", BaseSalary = 600_000, HireDate = new DateTime(2030, 1, 1) };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();

        var svc = new EmployeeDeductionService(db);
        var cash = new CashBoxService(db);
        var boxId = await db.CashBoxes.Where(b => b.IsActive && b.IsDefault).Select(b => b.Id).FirstAsync();
        await cash.DepositAsync(boxId, 2_000_000, new DateTime(y, 1, 1), "تمويل الاختبار", null, admin);
        var boxBefore = await cash.GetBalanceAsync(boxId);
        EmployeeDeductionService.CreateRequest Req(EmployeeDeductionKind k, decimal amount, int month, decimal? inst = null, string? reason = null) =>
            new(k, emp.Id, amount, new DateTime(y, month, 5), month, y, inst, reason, boxId);

        // التحقق من المدخلات
        Assert.False((await svc.CreateAsync(Req(EmployeeDeductionKind.Loan, 300_000, 1), admin)).result.Success);              // بلا قسط
        Assert.False((await svc.CreateAsync(Req(EmployeeDeductionKind.Loan, 300_000, 1, 400_000), admin)).result.Success);     // قسط > المبلغ
        Assert.False((await svc.CreateAsync(Req(EmployeeDeductionKind.Penalty, 25_000, 1), admin)).result.Success);            // عقوبة بلا سبب
        Assert.False((await svc.CreateAsync(Req(EmployeeDeductionKind.Withdrawal, 99_000_000, 1), admin)).result.Success);     // يفوق الصندوق

        var (r1, loan) = await svc.CreateAsync(Req(EmployeeDeductionKind.Loan, 250_000, 1, 100_000, "سلفة زواج"), admin);
        Assert.True(r1.Success, r1.ErrorMessage);
        Assert.StartsWith($"ED-{y}-", loan!.DeductionNumber);
        var (r2, withdrawal) = await svc.CreateAsync(Req(EmployeeDeductionKind.Withdrawal, 50_000, 1), admin);
        Assert.True(r2.Success, r2.ErrorMessage);
        var (r3, penalty) = await svc.CreateAsync(Req(EmployeeDeductionKind.Penalty, 25_000, 1, reason: "تأخر متكرر"), admin);
        Assert.True(r3.Success, r3.ErrorMessage);

        // السلفة والمسحوب يخرجان من الصندوق، والعقوبة لا
        Assert.Equal(boxBefore - 300_000, await cash.GetBalanceAsync(boxId));
        Assert.Equal(CashBoxTxType.EmployeeAdvance,
            (await db.CashBoxTransactions.SingleAsync(t => t.ReferenceTable == "EmployeeDeductions" && t.ReferenceId == loan.Id)).TxType);
        Assert.False(await db.CashBoxTransactions.AnyAsync(t => t.ReferenceTable == "EmployeeDeductions" && t.ReferenceId == penalty!.Id));
        var lines = await db.JournalEntryLines.Include(l => l.Account).Where(l => l.JournalEntryId == loan.JournalEntryId).ToListAsync();
        Assert.Equal(250_000, lines.Single(l => l.Account.AccountCode == "1104").Debit);
        Assert.Equal(250_000, lines.Single(l => l.Account.AccountCode == "1101").Credit);

        // رواتب الشهر الأول: قسط 100,000 + مسحوب 50,000 + عقوبة 25,000
        var hr = new HrService(db);
        var (g1, run1) = await hr.GenerateAsync(1, y);
        Assert.True(g1.Success, g1.ErrorMessage);
        var line1 = await db.PayrollLines.AsNoTracking().SingleAsync(l => l.PayrollRunId == run1 && l.EmployeeId == emp.Id);
        Assert.Equal(100_000, line1.LoanDeduction);
        Assert.Equal(50_000, line1.WithdrawalDeduction);
        Assert.Equal(25_000, line1.PenaltyDeduction);
        Assert.Equal(600_000 - 175_000, line1.NetSalary);

        // إعادة التوليد لا تكرر الأقساط
        Assert.True((await hr.GenerateAsync(1, y)).result.Success);
        Assert.Equal(3, await db.EmployeeDeductionInstallments.CountAsync(i => i.PayrollRunId == run1));

        // مسحوب جديد بعد التوليد ← الاعتماد يطلب إعادة التوليد
        var (r4, late) = await svc.CreateAsync(Req(EmployeeDeductionKind.Withdrawal, 20_000, 1), admin);
        Assert.True(r4.Success, r4.ErrorMessage);
        var stale = await hr.ApproveAsync(run1!.Value, admin);
        Assert.False(stale.result.Success);
        Assert.Contains("أعد توليد", stale.result.ErrorMessage);
        Assert.True((await svc.VoidAsync(late!.Id, "سُجّل بالخطأ", admin)).Success);

        // الاعتماد: المصروف = الصافي + السلف والمسحوبات، والدائن رواتب مستحقة + حساب السلف
        var a1 = await hr.ApproveAsync(run1.Value, admin);
        Assert.True(a1.result.Success, a1.result.ErrorMessage);
        var runEntry = await db.PayrollRuns.AsNoTracking().Where(r => r.Id == run1).Select(r => r.JournalEntryId).SingleAsync();
        var je = await db.JournalEntryLines.Include(l => l.Account).Where(l => l.JournalEntryId == runEntry).ToListAsync();
        Assert.Equal(je.Sum(l => l.Debit), je.Sum(l => l.Credit));
        Assert.Equal(150_000, je.Single(l => l.Account.AccountCode == "1104").Credit);

        // بعد الاعتماد: لا إلغاء لما استُقطع، ولا تسجيل على شهر مقفل
        Assert.False((await svc.VoidAsync(loan.Id, "تجربة", admin)).Success);
        Assert.Contains("معتمدة", (await svc.CreateAsync(Req(EmployeeDeductionKind.Withdrawal, 10_000, 1), admin)).result.ErrorMessage);
        var row = (await svc.GetListAsync(emp.Id)).Single(r => r.Id == loan.Id);
        Assert.Equal(100_000, row.Deducted);
        Assert.Equal(150_000, row.Remaining);
        Assert.Equal("قيد الاستقطاع", row.StatusText);
        Assert.Equal("مُستقطع بالكامل", (await svc.GetListAsync(emp.Id)).Single(r => r.Id == withdrawal!.Id).StatusText);

        // الشهر الثاني: قسط فقط، والثالث: المتبقي 50,000
        var (_, run2) = await hr.GenerateAsync(2, y);
        Assert.Equal(100_000, (await db.PayrollLines.AsNoTracking().SingleAsync(l => l.PayrollRunId == run2 && l.EmployeeId == emp.Id)).LoanDeduction);
        Assert.True((await hr.ApproveAsync(run2!.Value, admin)).result.Success);
        var (_, run3) = await hr.GenerateAsync(3, y);
        var line3 = await db.PayrollLines.AsNoTracking().SingleAsync(l => l.PayrollRunId == run3 && l.EmployeeId == emp.Id);
        Assert.Equal(50_000, line3.LoanDeduction);
        Assert.Equal(0, line3.WithdrawalDeduction + line3.PenaltyDeduction);
        Assert.True((await hr.ApproveAsync(run3!.Value, admin)).result.Success);
        Assert.Equal(0, (await svc.GetListAsync(emp.Id)).Single(r => r.Id == loan.Id).Remaining);
        var (_, run4) = await hr.GenerateAsync(4, y);
        Assert.Equal(0, (await db.PayrollLines.AsNoTracking().SingleAsync(l => l.PayrollRunId == run4 && l.EmployeeId == emp.Id)).LoanDeduction);

        // حساب السلف في الدفتر = صفر بعد سداد كل شيء (لهذا الموظف)
        var ids = await db.EmployeeDeductions.Where(d => d.EmployeeId == emp.Id).Select(d => d.Id).ToListAsync();
        var paid = await db.JournalEntryLines.Where(l => l.Account.AccountCode == "1104" && l.JournalEntry.SourceTable == "EmployeeDeductions"
                                                         && ids.Contains(l.JournalEntry.SourceId!.Value)).SumAsync(l => l.Debit - l.Credit);
        Assert.Equal(300_000, paid);   // صُرف 300,000 (السلفة + المسحوب) واستُقطع 150,000 + 100,000 + 50,000 في الرواتب

        // الإلغاء للأدمن فقط
        var (r5, fresh) = await svc.CreateAsync(Req(EmployeeDeductionKind.Penalty, 10_000, 5, reason: "إتلاف معدات"), admin);
        Assert.True(r5.Success, r5.ErrorMessage);
        var clerkRole = await db.Roles.FirstAsync(r => r.Name == "موظف مبيعات");
        var clerk = new User { Username = "ded_clerk", PasswordHash = PasswordHasher.Hash("x"), RoleId = clerkRole.Id };
        db.Users.Add(clerk);
        await db.SaveChangesAsync();
        Assert.False((await svc.VoidAsync(fresh!.Id, "خطأ", clerk.Id)).Success);
        Assert.True((await svc.VoidAsync(fresh.Id, "خطأ", admin)).Success);
    }
}
