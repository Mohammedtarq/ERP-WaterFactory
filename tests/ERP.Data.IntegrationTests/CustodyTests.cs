using System.Text.Json;
using ERP.Cloud.Contracts;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>العهد: الصرف من الصندوق، المصروف بباب (يظهر في مصاريفه)، الإعادة والتسوية، التجديد، التعديل والإلغاء، وحدود الرصيد.</summary>
[Collection("provisioned")]
public class CustodyTests
{
    private readonly ProvisionedFixture _f;
    public CustodyTests(ProvisionedFixture f) => _f = f;

    private static readonly DateTime Day = new(2032, 3, 10);

    private async Task<(ERP.Data.ProjectDb.ProjectDbContext db, CustodyService svc, int emp, int box, int category)> ArrangeAsync(string name, decimal boxFunds = 1_000_000)
    {
        Assert.True(_f.Install.Success, _f.Install.ErrorMessage);
        var db = _f.NewDb();
        var emp = new Employee { FullName = name, BaseSalary = 500_000, HireDate = new DateTime(2030, 1, 1) };
        db.Employees.Add(emp);
        var category = new FinanceCategory { Name = "عهدة اختبار — " + name, Kind = FinanceCategoryKind.Operating };
        db.FinanceCategories.Add(category);
        await db.SaveChangesAsync();
        var box = new CashBox { Name = "صندوق اختبار العهدة — " + name, BoxType = CashBoxType.Bank };   // بلا مالك: لا يلتقطه التسجيل التلقائي لحركات الاختبارات الأخرى
        db.CashBoxes.Add(box);
        await db.SaveChangesAsync();
        await new CashBoxService(db).DepositAsync(box.Id, boxFunds, Day, "تمويل", null, _f.AdminLocalId);
        return (db, new CustodyService(db), emp.Id, box.Id, category.Id);
    }

    [Fact]
    public async Task Issue_expense_return_and_settle_post_balanced_entries_and_move_the_cash_box()
    {
        var (db, svc, emp, box, cat) = await ArrangeAsync("مسؤول مشتريات — اختبار الصرف");
        await using var _ = db;
        var admin = _f.AdminLocalId;
        var cash = new CashBoxService(db);

        Assert.False((await svc.IssueAsync(emp, 0, "مشتريات", Day, admin, box)).result.Success);
        Assert.False((await svc.IssueAsync(emp, 100_000, " ", Day, admin, box)).result.Success);
        Assert.False((await svc.IssueAsync(emp, 5_000_000, "أكبر من الصندوق", Day, admin, box)).result.Success);

        var (r, id) = await svc.IssueAsync(emp, 300_000, "مشتريات صغيرة", Day, admin, box);
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(700_000, await cash.GetBalanceAsync(box));
        var issueTx = await db.CustodyTransactions.Include(t => t.JournalEntry!).ThenInclude(j => j.Lines).ThenInclude(l => l.Account).SingleAsync(t => t.CustodyId == id);
        Assert.Equal(300_000, issueTx.JournalEntry!.Lines.Single(l => l.Account.AccountCode == "1105").Debit);
        Assert.Equal(300_000, issueTx.JournalEntry.Lines.Single(l => l.Account.AccountCode == "1101").Credit);
        Assert.NotNull(issueTx.CashBoxTxId);

        // مصروف بباب: يخرج من العهدة ويظهر في مصاريف الباب (FinanceEntries) بقيد مدين المصروف / دائن عهد الموظفين
        var (e1, dup1) = await svc.AddExpenseAsync(new CustodyExpenseInput(id!.Value, 80_000, cat, "قطع غيار", "R-1", Day), admin);
        Assert.True(e1.Success, e1.ErrorMessage);
        Assert.False(dup1);
        Assert.Equal(220_000, await svc.BalanceAsync(id.Value));
        var entry = await db.FinanceEntries.SingleAsync(x => x.CategoryId == cat);
        Assert.Equal(80_000, entry.Amount);
        Assert.StartsWith("EX-2032-", entry.EntryNumber);
        var expLines = await db.JournalEntryLines.Include(l => l.Account).Where(l => l.JournalEntryId == entry.JournalEntryId).ToListAsync();
        Assert.Equal(80_000, expLines.Single(l => l.Account.AccountCode == "5101").Debit);
        Assert.Equal(80_000, expLines.Single(l => l.Account.AccountCode == "1105").Credit);
        Assert.Equal(700_000, await cash.GetBalanceAsync(box));   // المصروف لا يمسّ الصندوق

        // لا تجاوز لرصيد العهدة، ولا باب فارغ، وصاحب العهدة وحده من الهاتف
        Assert.False((await svc.AddExpenseAsync(new CustodyExpenseInput(id.Value, 999_000, cat, null, null, Day), admin)).result.Success);
        Assert.False((await svc.AddExpenseAsync(new CustodyExpenseInput(id.Value, 10_000, 0, null, null, Day), admin)).result.Success);
        Assert.False((await svc.AddExpenseAsync(new CustodyExpenseInput(id.Value, 10_000, cat, null, null, Day), admin, ownerEmployeeId: emp + 9999)).result.Success);

        // إعادة الإرسال من الهاتف برقم واحد لا تكرر
        var cid = Guid.NewGuid();
        var input = new CustodyExpenseInput(id.Value, 20_000, cat, "نقل", null, Day, ClientId: cid);
        Assert.True((await svc.AddExpenseAsync(input, admin, emp)).result.Success);
        var again = await svc.AddExpenseAsync(input, admin, emp);
        Assert.True(again.result.Success);
        Assert.True(again.duplicate);
        Assert.Equal(200_000, await svc.BalanceAsync(id.Value));
        Assert.Equal(2, await db.FinanceEntries.CountAsync(x => x.CategoryId == cat));

        // التسوية لا تتم وفيها متبقٍّ دون إعادته
        Assert.False((await svc.ReturnAsync(id.Value, 50_000, settle: true, Day, admin, box)).Success);
        var ret = await svc.ReturnAsync(id.Value, null, settle: true, Day, admin, box);
        Assert.True(ret.Success, ret.ErrorMessage);
        Assert.Equal(900_000, await cash.GetBalanceAsync(box));
        Assert.Equal(0, await svc.BalanceAsync(id.Value));
        var row = (await svc.ListAsync()).Single(c => c.Id == id);
        Assert.Equal(CustodyStatus.Settled, row.Status);
        Assert.Equal((300_000m, 100_000m, 200_000m), (row.Issued, row.Spent, row.Returned));
        Assert.False((await svc.AddExpenseAsync(new CustodyExpenseInput(id.Value, 1_000, cat, null, null, Day), admin)).result.Success);

        // كل قيود العهدة متوازنة
        var entries = await db.JournalEntries.Include(j => j.Lines).Where(j => j.SourceTable == "CustodyTransactions" || j.SourceTable == "FinanceEntries").ToListAsync();
        Assert.All(entries, j => Assert.Equal(j.Lines.Sum(l => l.Debit), j.Lines.Sum(l => l.Credit)));
    }

    [Fact]
    public async Task Renew_tops_up_to_the_usual_amount_and_reopens_a_settled_custody()
    {
        var (db, svc, emp, box, cat) = await ArrangeAsync("سائق — اختبار التجديد");
        await using var _ = db;
        var admin = _f.AdminLocalId;
        var (r, id) = await svc.IssueAsync(emp, 100_000, "وقود", Day, admin, box);
        Assert.True(r.Success, r.ErrorMessage);
        Assert.False((await svc.RenewToTargetAsync(id!.Value, Day, admin, box)).Success);   // لم ينقص بعد
        Assert.True((await svc.AddExpenseAsync(new CustodyExpenseInput(id.Value, 60_000, cat, "وقود", null, Day), admin)).result.Success);
        Assert.True((await svc.RenewToTargetAsync(id.Value, Day, admin, box)).Success);
        Assert.Equal(100_000, await svc.BalanceAsync(id.Value));
        Assert.Equal(840_000, await new CashBoxService(db).GetBalanceAsync(box));

        Assert.True((await svc.ReturnAsync(id.Value, null, settle: true, Day, admin, box)).Success);
        Assert.Equal(CustodyStatus.Settled, (await svc.ListAsync()).Single(c => c.Id == id).Status);
        Assert.True((await svc.TopUpAsync(id.Value, 50_000, Day, admin, box, "عهدة جديدة")).Success);
        Assert.Equal(CustodyStatus.Active, (await svc.ListAsync()).Single(c => c.Id == id).Status);
        Assert.Equal(50_000, await svc.BalanceAsync(id.Value));
    }

    [Fact]
    public async Task Edit_and_void_keep_the_ledger_and_the_cash_box_consistent()
    {
        var (db, svc, emp, box, cat) = await ArrangeAsync("مشتريات — اختبار التعديل والإلغاء");
        await using var _ = db;
        var admin = _f.AdminLocalId;
        var cash = new CashBoxService(db);
        var (r, id) = await svc.IssueAsync(emp, 300_000, "مشتريات", Day, admin, box);
        Assert.True(r.Success, r.ErrorMessage);
        Assert.True((await svc.AddExpenseAsync(new CustodyExpenseInput(id!.Value, 45_000, cat, "نقل", null, Day), admin)).result.Success);
        var issue = await db.CustodyTransactions.SingleAsync(t => t.CustodyId == id && t.Kind == CustodyTxKind.Issue);
        var expense = await db.CustodyTransactions.SingleAsync(t => t.CustodyId == id && t.Kind == CustodyTxKind.Expense);

        // تعديل مصروف: المبلغ والقيد والمصروف المسجل
        Assert.False((await svc.EditAsync(expense.Id, 50_000, cat, "نقل", null, Day, "", admin)).Success);       // بلا سبب
        Assert.False((await svc.EditAsync(expense.Id, 900_000, cat, "نقل", null, Day, "خطأ", admin)).Success);   // يتجاوز الرصيد
        Assert.True((await svc.EditAsync(expense.Id, 50_000, cat, "أجرة نقل", "R-9", Day, "خطأ في كتابة المبلغ", admin)).Success);
        Assert.Equal(250_000, await svc.BalanceAsync(id.Value));
        var fe = await db.FinanceEntries.AsNoTracking().SingleAsync(x => x.CategoryId == cat);
        Assert.Equal(50_000, fe.Amount);
        Assert.Equal("R-9", fe.ReceiptNumber);
        var lines = await db.JournalEntryLines.AsNoTracking().Where(l => l.JournalEntryId == fe.JournalEntryId).ToListAsync();
        Assert.All(lines, l => Assert.Equal(50_000, Math.Max(l.Debit, l.Credit)));
        Assert.Equal(45_000, (await db.CustodyTransactions.AsNoTracking().SingleAsync(t => t.Id == expense.Id)).OriginalAmount);

        // تعديل الصرف: لا ينزل دون المصروف، وزيادته من الصندوق
        Assert.False((await svc.EditAsync(issue.Id, 40_000, null, "مشتريات", null, Day, "تخفيض", admin)).Success);
        Assert.True((await svc.EditAsync(issue.Id, 400_000, null, "مشتريات", null, Day, "زيادة", admin)).Success);
        Assert.Equal(600_000, await cash.GetBalanceAsync(box));
        Assert.Equal(350_000, await svc.BalanceAsync(id.Value));

        // إلغاء الصرف ممنوع وقد صُرف منه، وإلغاء العهدة كذلك
        Assert.False((await svc.VoidTxAsync(issue.Id, "تجربة", admin)).Success);
        Assert.False((await svc.VoidCustodyAsync(id.Value, "تجربة", admin)).Success);
        // إلغاء المصروف يعيد الرصيد ويشطب المصروف المسجل بقيد عكسي
        Assert.False((await svc.VoidTxAsync(expense.Id, " ", admin)).Success);
        Assert.True((await svc.VoidTxAsync(expense.Id, "سُجّل بالخطأ", admin)).Success);
        Assert.Equal(400_000, await svc.BalanceAsync(id.Value));
        Assert.True((await db.FinanceEntries.AsNoTracking().SingleAsync(x => x.CategoryId == cat)).IsVoided);
        var all = await db.JournalEntryLines.AsNoTracking().Where(l => l.Account.AccountCode == "5101" && l.JournalEntry.SourceTable == "FinanceEntries"
                                                                       && l.JournalEntry.SourceId == fe.Id).SumAsync(l => l.Debit - l.Credit);
        Assert.Equal(0, all);
        Assert.False((await svc.VoidTxAsync(expense.Id, "مرة ثانية", admin)).Success);

        // الآن تُلغى العهدة كاملة: يعود الصندوق كما بدأ وتبقى ظاهرة
        Assert.True((await svc.VoidCustodyAsync(id.Value, "صُرفت لشخص خطأ", admin)).Success);
        Assert.Equal(1_000_000, await cash.GetBalanceAsync(box));
        Assert.Equal(CustodyStatus.Voided, (await svc.ListAsync()).Single(c => c.Id == id).Status);
        Assert.False((await svc.TopUpAsync(id.Value, 1_000, Day, admin, box)).Success);
        var detail = await svc.DetailAsync(id.Value);
        Assert.Contains(detail!.Rows, r2 => r2.IsVoided && r2.VoidReason == "صُرفت لشخص خطأ");
    }

    [Fact]
    public async Task List_flags_old_and_low_custodies_and_detail_summarizes_by_category()
    {
        var (db, svc, emp, box, cat) = await ArrangeAsync("سائق — اختبار التنبيه");
        await using var _ = db;
        var admin = _f.AdminLocalId;
        var old = DateTime.Today.AddDays(-9);
        var (r, id) = await svc.IssueAsync(emp, 100_000, "وقود", old, admin, box);
        Assert.True(r.Success, r.ErrorMessage);
        var row = (await svc.ListAsync(CustodyStatus.Active, emp)).Single();
        Assert.True(row.IsLate);          // 9 أيام ≥ 7
        Assert.False(row.IsLow);
        Assert.True((await svc.AddExpenseAsync(new CustodyExpenseInput(id!.Value, 90_000, cat, "وقود", null, DateTime.Today), admin)).result.Success);
        row = (await svc.ListAsync(CustodyStatus.Active, emp)).Single();
        Assert.True(row.IsLow);           // بقي 10% < 20%
        var detail = await svc.DetailAsync(id.Value);
        Assert.Equal(("عهدة اختبار — " + "سائق — اختبار التنبيه", 90_000m), detail!.ByCategory.Single());
        Assert.Equal(10_000, detail.Rows.Last().Balance);
        Assert.Equal(9, row.AgeDays);

        // الجرس: عهدة متأخرة وعهدة بلا وصل، لمن يملك صلاحية العهد فقط
        var custodyPerms = new UserPermissions(new[] { new RolePermission { ModuleCode = SpecialPermission.Custody, CanView = true } });
        var alerts = await new AlertsService(db).ForAsync(custodyPerms);
        Assert.Contains(alerts, a => a.Key == "custody-late" && a.Detail.Contains("سائق — اختبار التنبيه") && a.Section == "CustodySectionViewModel");
        Assert.Contains(alerts, a => a.Key == "custody-receipt" && a.Detail.Contains("سائق — اختبار التنبيه"));
        Assert.DoesNotContain(await new AlertsService(db).ForAsync(new UserPermissions(Array.Empty<RolePermission>())), a => a.Key.StartsWith("custody-"));
    }

    [Fact]
    public async Task Buyer_phone_gets_a_custody_only_snapshot_and_expenses_post_once_within_his_own_balance()
    {
        var (db, svc, emp, box, cat) = await ArrangeAsync("مسؤول مشتريات — اختبار الهاتف");
        await using var _ = db;
        var admin = _f.AdminLocalId;
        var other = new Employee { FullName = "موظف آخر — اختبار الهاتف", BaseSalary = 400_000, HireDate = new DateTime(2030, 1, 1) };
        db.Employees.Add(other);
        await db.SaveChangesAsync();

        // موظف غير مندوب يسجَّل له جهاز ودخول
        var app = new RepAppService(db);
        var (reg, key) = await app.RegisterDeviceAsync(emp, "هاتف المشتريات", admin);
        Assert.True(reg.Success, reg.ErrorMessage);
        Assert.True((await app.SetLoginAsync(emp, "m1", "7", admin)).Success);

        // قبل العهدة: نسخة بعهدة فارغة، وبلا منتجات ولا زبائن
        var snap0 = await new CloudSyncService(db).BuildSnapshotAsync(emp);
        Assert.True(snap0!.CustodyOnly);
        Assert.Empty(snap0.Products);
        Assert.NotNull(snap0.Login);
        Assert.Empty(snap0.Custody!.Custodies);

        var (issued, id) = await svc.IssueAsync(emp, 200_000, "مشتريات", Day, admin, box);
        Assert.True(issued.Success, issued.ErrorMessage);
        var snap = await new CloudSyncService(db).BuildSnapshotAsync(emp);
        var c = Assert.Single(snap!.Custody!.Custodies);
        Assert.Equal((200_000m, "مشتريات"), (c.Balance, c.Purpose));
        Assert.Contains(snap.Custody.Categories, x => x.Id == cat);
        Assert.Contains(snap.Notices!, n => n.Kind == "custody" && n.Title.Contains("200,000"));

        RepRequestEnvelope Env(object payload, Guid? cid = null) => new(cid ?? Guid.NewGuid(), RepRequestKind.CustodyExpense, DateTime.Now, JsonSerializer.Serialize(payload), null);
        var rid = Guid.NewGuid();
        var env = Env(new CustodyExpensePayload(id!.Value, cat, 45_000, "أجرة نقل", "R-7"), rid);
        var r1 = await app.ReceiveAsync(key!, env);
        Assert.True(r1.Accepted, r1.Message);
        Assert.Equal(RepRequestStatus.Posted, r1.Status);
        var again = await app.ReceiveAsync(key!, env);          // إعادة الإرسال لا تكرر المصروف
        Assert.Equal(r1.RequestId, again.RequestId);
        Assert.Equal(155_000, await svc.BalanceAsync(id.Value));
        Assert.Equal(1, await db.FinanceEntries.CountAsync(x => x.CategoryId == cat));

        // أكبر من الرصيد، وعهدة غيره: يُرفضان بسبب مفهوم
        var over = await app.ReceiveAsync(key!, Env(new CustodyExpensePayload(id.Value, cat, 999_000)));
        Assert.Equal(RepRequestStatus.Failed, over.Status);
        Assert.Contains("رصيد العهدة", over.Message);
        var (_, otherKey) = await app.RegisterDeviceAsync(other.Id, "هاتف آخر", admin);
        var stolen = await app.ReceiveAsync(otherKey!, Env(new CustodyExpensePayload(id.Value, cat, 1_000)));
        Assert.Equal(RepRequestStatus.Failed, stolen.Status);
        Assert.Equal(155_000, await svc.BalanceAsync(id.Value));

        // الهاتف يرى الرصيد الجديد وحركته
        var after = await new CloudSyncService(db).BuildSnapshotAsync(emp);
        Assert.Equal(155_000, after!.Custody!.Custodies.Single().Balance);
        Assert.Contains(after.Custody.Recent, t => t.Kind == "Expense" && t.Amount == 45_000);
        Assert.Contains(after.PostedClientIds!, g => g == rid);
    }
}
