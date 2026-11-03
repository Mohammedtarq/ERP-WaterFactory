using ERP.Data.ProjectDb.Entities;
using ERP.Data.Security;
using ERP.Data.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>
/// صندوق المنزل: صرف خارج العمل (الخمس، مساعدات، أصول) لا يدخل كلفة القنينة ولا صافي الربح ولا فائض رأس المال،
/// منع الصرف التشغيلي منه، سحب الشريك من صندوق نقدي فقط، القروض وسدادها، التسوية بالجرد، وضوابط تصحيح الحركات.
/// </summary>
[Collection("provisioned")]
public class HomeBoxTests
{
    private readonly ProvisionedFixture _f;
    public HomeBoxTests(ProvisionedFixture f) => _f = f;

    private static readonly DateTime Day = new(2033, 5, 10);
    private static readonly DateTime Day2 = new(2033, 6, 10);
    private static readonly DateTime Day3 = new(2033, 7, 10);

    [Fact]
    public async Task Khums_aid_and_asset_purchase_stay_out_of_cost_net_profit_and_capital_surplus()
    {
        Assert.True(_f.Install.Success, _f.Install.ErrorMessage);
        await using var db = _f.NewDb();
        var admin = _f.AdminLocalId;
        var cash = new CashBoxService(db);
        var fa = new FinalAccountsService(db);
        var svc = new HomeBoxService(db);
        var home = await fa.HomeBoxAsync();
        var (dep, funding) = await cash.DepositAsync(home.Id, 500_000, Day, "تمويل", "اختبار صندوق المنزل", admin);
        Assert.True(dep.Success, dep.ErrorMessage);
        var created = new List<int>();
        // مطابقة سابقة مؤقتة: ما يُصرف من المنزل بعدها يُعاد إلى فائض المطابقة التالية (توزيع ربح لا خسارة)
        var prev = new AssetReconciliation { ReconNumber = "TMP-HOMEBOX", ReconDate = Day, CreatedByUserId = admin, CreatedAt = DateTime.UtcNow.AddMinutes(-5), NetAssets = 0, IsBaseline = true };
        db.AssetReconciliations.Add(prev);
        await db.SaveChangesAsync();
        try
        {
            var homeBefore = await cash.GetBalanceAsync(home.Id);
            var monthBefore = await fa.MonthAsync(Day.Year, Day.Month);
            var wcBefore = await fa.WorkingCapitalAsync(Day);

            // التحقق من المدخلات
            Assert.False((await svc.SpendAsync(new(HomeBoxEntryKind.Khums, 0, Day, null, null, null), admin)).result.Success);
            Assert.False((await svc.SpendAsync(new(HomeBoxEntryKind.Asset, 50_000, Day, null, null, null), admin)).result.Success);   // أصل بلا بيان
            Assert.False((await svc.SpendAsync(new(HomeBoxEntryKind.Aid, 9_000_000, Day, "مساعدة", "فقير", null), admin)).result.Success);   // أكبر من الرصيد

            var (k, khums) = await svc.SpendAsync(new(HomeBoxEntryKind.Khums, 100_000, Day, null, null, "خمس السنة"), admin);
            var (a, aid) = await svc.SpendAsync(new(HomeBoxEntryKind.Aid, 30_000, Day, "مساعدة عائلة", "عائلة محتاجة", null), admin);
            var (m, asset) = await svc.SpendAsync(new(HomeBoxEntryKind.Asset, 150_000, Day, "ماكنة تعبئة جديدة", "مورد المكائن", null), admin);
            Assert.True(k.Success && a.Success && m.Success, k.ErrorMessage + a.ErrorMessage + m.ErrorMessage);
            created.AddRange(new[] { khums!.Id, aid!.Id, asset!.Id });
            Assert.StartsWith("HB-2033-", khums.EntryNumber);

            // الرصيد نزل بمجموع الصرف
            Assert.Equal(homeBefore - 280_000, await cash.GetBalanceAsync(home.Id));

            // القيود: الخمس والمساعدات إلى توزيعات المالك 3105، والأصل إلى أصول ثابتة 1401، والصندوق دائن
            async Task<(string debit, string credit)> AccountsOf(int jeId)
            {
                var lines = await db.JournalEntryLines.Include(l => l.Account).Where(l => l.JournalEntryId == jeId).ToListAsync();
                return (lines.Single(l => l.Debit > 0).Account.AccountCode, lines.Single(l => l.Credit > 0).Account.AccountCode);
            }
            Assert.Equal(("3105", "1101"), await AccountsOf(khums.JournalEntryId!.Value));
            Assert.Equal(("3105", "1101"), await AccountsOf(aid.JournalEntryId!.Value));
            Assert.Equal(("1401", "1101"), await AccountsOf(asset.JournalEntryId!.Value));

            // الحسابات الختامية للشهر: لا تغيّر في المصاريف ولا الكلفة ولا صافي الربح، والصرف يظهر للعلم فقط
            var monthAfter = await fa.MonthAsync(Day.Year, Day.Month);
            Assert.Equal(monthBefore.OperatingExpenses, monthAfter.OperatingExpenses);
            Assert.Equal(monthBefore.NonOperatingExpenses, monthAfter.NonOperatingExpenses);
            Assert.Equal(monthBefore.NetProfit, monthAfter.NetProfit);
            Assert.Equal(monthBefore.CostPerPiece, monthAfter.CostPerPiece);
            Assert.Equal(monthBefore.OverheadPerPiece, monthAfter.OverheadPerPiece);
            Assert.Equal(monthBefore.HomeDistributions + 280_000, monthAfter.HomeDistributions);
            Assert.Contains(monthAfter.Lines, l => l.Label == "الخمس" && l.Amount == 100_000);

            var snap = await new ReconciliationService(db).ComputeAsync(Day, FinishedGoodsValuation.Cost);
            Assert.Equal(280_000, snap.HomeDistributions);
            Assert.Equal(snap.NetAssets - prev.NetAssets + snap.PartnerWithdrawals + 280_000 - snap.OwnerDeposits, snap.Surplus);

            // الفائض عن رأس المال التشغيلي لم يتغير (النقد خرج من المنزل ومن الصافي معًا)
            var wcAfter = await fa.WorkingCapitalAsync(Day);
            Assert.Equal(wcBefore.Surplus, wcAfter.Surplus);

            // السجل: بحث وفلترة وإجماليات
            var log = await svc.LogAsync(Day.AddDays(-1), Day.AddDays(1));
            Assert.Contains(log.Rows, r => r.Category == "شراء ماكنات وأصول" && r.Out == 150_000);
            Assert.Equal(1, (await svc.LogAsync(Day, Day, "الخمس", null)).Rows.Count(r => r.Out == 100_000));
            Assert.Single((await svc.LogAsync(Day, Day, null, "ماكنة تعبئة")).Rows);
            Assert.Empty((await svc.LogAsync(Day, Day, null, null, 1_000_000, null)).Rows);
            Assert.Equal(280_000, (await svc.LogAsync(Day, Day, null, null, 30_000, 150_000)).Rows.Sum(r => r.Out));

            // الإلغاء بسبب: يعيد الرصيد ويبقى السطر مشطوبًا
            Assert.False((await svc.VoidEntryAsync(khums.Id, " ", admin)).Success);
            Assert.True((await svc.VoidEntryAsync(khums.Id, "خطأ في المبلغ", admin)).Success);
            Assert.Equal(homeBefore - 180_000, await cash.GetBalanceAsync(home.Id));
            Assert.False((await svc.VoidEntryAsync(khums.Id, "مرة ثانية", admin)).Success);
            Assert.Equal(monthBefore.HomeDistributions + 180_000, (await fa.MonthAsync(Day.Year, Day.Month)).HomeDistributions);
            Assert.Equal(180_000, (await new ReconciliationService(db).ComputeAsync(Day, FinishedGoodsValuation.Cost)).HomeDistributions);
        }
        finally
        {
            db.AssetReconciliations.Remove(prev);
            await db.SaveChangesAsync();
            foreach (var id in created) await svc.VoidEntryAsync(id, "تنظيف الاختبار", admin);
            if (funding is not null) await cash.VoidAsync(funding.Id, "تنظيف الاختبار", admin);
        }
    }

    [Fact]
    public async Task Home_box_refuses_operating_spending_and_partner_withdrawal_takes_cash_boxes_only()
    {
        Assert.True(_f.Install.Success, _f.Install.ErrorMessage);
        await using var db = _f.NewDb();
        var admin = _f.AdminLocalId;
        var cash = new CashBoxService(db);
        var home = await new FinalAccountsService(db).HomeBoxAsync();
        var (dep, funding) = await cash.DepositAsync(home.Id, 400_000, Day2, "تمويل", "اختبار", admin);
        Assert.True(dep.Success, dep.ErrorMessage);
        decimal withdrawn = 0;
        try
        {
            // سحب يدوي (يدخل المصروفات العمومية) مرفوض
            var manual = await cash.WithdrawAsync(home.Id, 10_000, Day2, "مورد", "مواد", admin);
            Assert.False(manual.result.Success);
            Assert.Equal(CashBoxService.HomeSpendError, manual.result.ErrorMessage);

            // مصروف تشغيلي بباب من صندوق المنزل مرفوض
            var category = new FinanceCategory { Name = "باب اختبار المنزل", Kind = FinanceCategoryKind.Operating };
            db.FinanceCategories.Add(category);
            await db.SaveChangesAsync();
            var expense = await new FinanceEntryService(db).CreateAsync(new(category.Id, 10_000, Day2, null, null, null, null, null, home.Id), admin);
            Assert.False(expense.result.Success);
            Assert.Contains("صندوق المنزل", expense.result.ErrorMessage);

            // الحماية الأخيرة في التسجيل التلقائي (رواتب، عهد...) ترفض ولو لم تفحصه الشاشة
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                cash.RecordAutoAsync(admin, CashBoxTxType.Expense, -5_000, Day2, "FinanceEntries", 1, null, "اختبار", null, home.Id));

            // الإيراد إلى صندوق المنزل غير ممنوع
            var bank = new CashBox { Name = "صندوق بنك اختبار المنزل", BoxType = CashBoxType.Bank };
            var cards = new CashBox { Name = "صندوق بطاقات اختبار المنزل", BoxType = CashBoxType.Cards };
            db.CashBoxes.AddRange(bank, cards);
            await db.SaveChangesAsync();
            await cash.DepositAsync(bank.Id, 1_000_000, Day2, null, "تمويل", admin);

            // سحب الشريك: لا من البنك ولا البطاقات؛ ويصح من صندوق المنزل
            var partner = new Partner { Name = "شريك اختبار المنزل", SharePercent = 50, IsActive = true };
            db.Partners.Add(partner);
            await db.SaveChangesAsync();
            var recon = new ReconciliationService(db);
            Assert.True((await recon.OpeningAsync(partner.Id, 300_000, Day2, "رصيد افتتاحي", admin)).result.Success);
            var approvals = new PaymentApprovalService(db);
            Assert.False((await approvals.RequestPartnerWithdrawalAsync(partner.Id, 50_000, Day2, null, bank.Id, admin)).result.Success);
            Assert.False((await approvals.RequestPartnerWithdrawalAsync(partner.Id, 50_000, Day2, null, cards.Id, admin)).result.Success);
            Assert.False((await approvals.RequestPartnerWithdrawalAsync(partner.Id, 50_000, Day2, null, home.Id + 999_999, admin)).result.Success);
            Assert.False((await approvals.RequestPartnerWithdrawalAsync(partner.Id, 5_000_000, Day2, null, home.Id, admin)).result.Success);   // أكبر من رصيد الصندوق
            var homeBefore = await cash.GetBalanceAsync(home.Id);
            var (ok, request) = await approvals.RequestPartnerWithdrawalAsync(partner.Id, 120_000, Day2, "سحب أرباح", home.Id, admin);
            Assert.True(ok.Success, ok.ErrorMessage);
            Assert.Equal(home.Id, request!.CashBoxId);
            var approved = await approvals.ApproveAsync(request.Id, admin);
            Assert.True(approved.Success, approved.ErrorMessage);
            withdrawn = 120_000;
            Assert.Equal(homeBefore - 120_000, await cash.GetBalanceAsync(home.Id));
            Assert.Contains((await new HomeBoxService(db).LogAsync(Day2, Day2)).Rows, r => r.Category == "سحب أرباح شريك" && r.Out == 120_000);
        }
        finally
        {
            // يُعاد الصندوق كما كان: يُعاد السحب إيداعًا (صافي صفر على الفائض) ثم يُلغى التمويل، ولا يبقى شريك اختبار
            if (withdrawn > 0) await cash.DepositAsync(home.Id, withdrawn, Day2, "تنظيف", "إعادة سحب الاختبار", admin);
            await db.PartnerTransactions.Where(t => t.Partner.Name == "شريك اختبار المنزل").ExecuteDeleteAsync();
            await db.Partners.Where(p => p.Name == "شريك اختبار المنزل").ExecuteDeleteAsync();
            if (funding is not null) await cash.VoidAsync(funding.Id, "تنظيف الاختبار", admin);
        }
    }

    [Fact]
    public async Task Home_loans_move_cash_without_touching_profit_and_repay_in_parts()
    {
        Assert.True(_f.Install.Success, _f.Install.ErrorMessage);
        await using var db = _f.NewDb();
        var admin = _f.AdminLocalId;
        var cash = new CashBoxService(db);
        var fa = new FinalAccountsService(db);
        var svc = new HomeBoxService(db);
        var home = await fa.HomeBoxAsync();
        var work = new CashBox { Name = "صندوق عمل اختبار القرض", BoxType = CashBoxType.Main };
        db.CashBoxes.Add(work);
        await db.SaveChangesAsync();
        var (dep, funding) = await cash.DepositAsync(home.Id, 300_000, Day3, "تمويل", "اختبار", admin);
        Assert.True(dep.Success, dep.ErrorMessage);
        try
        {
            var wcBefore = await fa.WorkingCapitalAsync(Day3);
            var monthBefore = await fa.MonthAsync(Day3.Year, Day3.Month);
            Assert.False((await svc.LendAsync(work.Id, 0, Day3, "سيولة", admin)).result.Success);
            Assert.False((await svc.LendAsync(work.Id, 50_000, Day3, " ", admin)).result.Success);
            Assert.False((await svc.LendAsync(home.Id, 50_000, Day3, "سيولة", admin)).result.Success);        // لا يُقرض المنزلُ نفسه
            Assert.False((await svc.LendAsync(work.Id, 90_000_000, Day3, "سيولة", admin)).result.Success);   // أكبر من رصيد المنزل

            var homeBefore = await cash.GetBalanceAsync(home.Id);
            var (lr, loan) = await svc.LendAsync(work.Id, 100_000, Day3, "نفاد سيولة صندوق العمل", admin);
            Assert.True(lr.Success, lr.ErrorMessage);
            Assert.StartsWith("HL-2033-", loan!.LoanNumber);
            Assert.Equal(homeBefore - 100_000, await cash.GetBalanceAsync(home.Id));
            Assert.Equal(100_000, await cash.GetBalanceAsync(work.Id));
            Assert.Equal(100_000, await svc.OutstandingTotalAsync() - wcBefore.HomeLoansOutstanding);

            // لا أثر على الأرباح ولا على فائض رأس المال (ما اقترضه العمل يبقى ملكًا للمنزل)
            var wc = await fa.WorkingCapitalAsync(Day3);
            Assert.Equal(wcBefore.Surplus, wc.Surplus);
            var month = await fa.MonthAsync(Day3.Year, Day3.Month);
            Assert.Equal(monthBefore.NetProfit, month.NetProfit);
            Assert.Equal(monthBefore.OperatingExpenses, month.OperatingExpenses);
            var log = await svc.LogAsync(Day3, Day3);
            Assert.Contains(log.Rows, r => r.Category == "قرض لصندوق عمل" && r.Out == 100_000);

            // السداد الجزئي ثم الكامل، ولا يتجاوز المتبقي
            Assert.False((await svc.RepayAsync(loan.Id, 0, work.Id, Day3, null, admin)).Success);
            Assert.False((await svc.RepayAsync(loan.Id, 150_000, work.Id, Day3, null, admin)).Success);
            Assert.False((await svc.RepayAsync(loan.Id, 10_000, home.Id, Day3, null, admin)).Success);
            Assert.True((await svc.RepayAsync(loan.Id, 40_000, work.Id, Day3, "دفعة أولى", admin)).Success);
            var open = (await svc.LoansAsync(openOnly: true)).Single(l => l.Id == loan.Id);
            Assert.Equal(60_000, open.Remaining);
            Assert.Equal("مسدَّد جزئيًا", open.StatusText);
            Assert.False((await svc.RepayAsync(loan.Id, 70_000, work.Id, Day3, null, admin)).Success);
            Assert.True((await svc.RepayAsync(loan.Id, 60_000, work.Id, Day3, null, admin)).Success);
            Assert.DoesNotContain(await svc.LoansAsync(openOnly: true), l => l.Id == loan.Id);
            Assert.Equal(HomeLoanStatus.Settled, (await db.HomeLoans.AsNoTracking().SingleAsync(l => l.Id == loan.Id)).Status);
            Assert.False((await svc.RepayAsync(loan.Id, 1, work.Id, Day3, null, admin)).Success);   // مسدَّد
            Assert.Equal(homeBefore, await cash.GetBalanceAsync(home.Id));
            Assert.Equal(0, await cash.GetBalanceAsync(work.Id));
            Assert.Contains((await svc.LogAsync(Day3, Day3)).Rows, r => r.Category == "سداد قرض" && r.In == 60_000);
        }
        finally
        {
            if (funding is not null) await cash.VoidAsync(funding.Id, "تنظيف الاختبار", admin);
            work.IsActive = false;
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Count_adjustment_posts_a_separate_entry_outside_cost_and_large_gaps_wait_for_approval()
    {
        Assert.True(_f.Install.Success, _f.Install.ErrorMessage);
        await using var db = _f.NewDb();
        var admin = _f.AdminLocalId;
        var cash = new CashBoxService(db);
        var fa = new FinalAccountsService(db);
        var box = new CashBox { Name = "صندوق اختبار الجرد", BoxType = CashBoxType.Bank };
        db.CashBoxes.Add(box);
        await db.SaveChangesAsync();
        Assert.True((await cash.DepositAsync(box.Id, 1_000_000, Day, null, "تمويل", admin)).result.Success);
        var monthBefore = await fa.MonthAsync(Day.Year, Day.Month);

        Assert.False((await cash.AdjustToCountAsync(box.Id, 950_000, Day, " ", admin)).result.Success);          // السبب إلزامي
        Assert.False((await cash.AdjustToCountAsync(box.Id, 1_000_000, Day, "جرد", admin)).result.Success);      // لا فرق
        Assert.False((await cash.AdjustToCountAsync(box.Id, -1, Day, "جرد", admin)).result.Success);

        var (r, tx, req) = await cash.AdjustToCountAsync(box.Id, 950_000, Day, "جرد فعلي: نقص", admin);
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Null(req);
        Assert.Equal(CashBoxTxType.Adjustment, tx!.TxType);
        Assert.Equal(-50_000, tx.Amount);
        Assert.Equal(950_000, await cash.GetBalanceAsync(box.Id));
        var lines = await db.JournalEntryLines.Include(l => l.Account).Where(l => l.JournalEntryId == tx.JournalEntryId).ToListAsync();
        Assert.Equal(50_000, lines.Single(l => l.Account.AccountCode == "5105").Debit);
        Assert.Equal(50_000, lines.Single(l => l.Account.AccountCode == "1101").Credit);

        // عجز الجرد خسارة فعلية تخفض صافي الربح لكنها لا تدخل كلفة القنينة
        var month = await fa.MonthAsync(Day.Year, Day.Month);
        Assert.Equal(monthBefore.CashDifferences + 50_000, month.CashDifferences);
        Assert.Equal(monthBefore.NetProfit - 50_000, month.NetProfit);
        Assert.Equal(monthBefore.OperatingExpenses, month.OperatingExpenses);
        Assert.Equal(monthBefore.CostPerPiece, month.CostPerPiece);
        Assert.Equal(monthBefore.OverheadPerPiece, month.OverheadPerPiece);

        // زيادة جرد
        var (r2, tx2, _) = await cash.AdjustToCountAsync(box.Id, 960_000, Day, "جرد فعلي: وُجدت زيادة", admin);
        Assert.True(r2.Success, r2.ErrorMessage);
        Assert.Equal(10_000, tx2!.Amount);
        Assert.Equal(monthBefore.CashDifferences + 40_000, (await fa.MonthAsync(Day.Year, Day.Month)).CashDifferences);

        // التسوية لا تُعدَّل ولا تُلغى (تصحَّح بتسوية جديدة)، ويظهر الأثر في تقرير التعديلات
        Assert.False((await cash.VoidAsync(tx.Id, "تجربة", admin)).Success);
        var changes = await cash.ChangesAsync(Day, Day, box.Id);
        Assert.Equal(2, changes.Count(c => c.Kind == "تسوية جرد"));

        // مستخدم بصلاحية التسوية: دون السقف مباشرة، وفوقه طلب لاعتماد المالية
        var role = new Role { Name = "مسوّي صناديق اختبار", Permissions = { new RolePermission { ModuleCode = SpecialPermission.CashAdjust, CanView = true } } };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        var clerk = new User { Username = "adj_clerk", PasswordHash = PasswordHasher.Hash("x"), RoleId = role.Id };
        db.Users.Add(clerk);
        await db.SaveChangesAsync();
        Assert.True(await cash.CanCorrectAsync(clerk.Id));
        var small = await cash.AdjustToCountAsync(box.Id, 955_000, Day, "فرق صغير", clerk.Id);
        Assert.True(small.result.Success, small.result.ErrorMessage);
        Assert.Null(small.request);
        var balance = await cash.GetBalanceAsync(box.Id);
        var big = await cash.AdjustToCountAsync(box.Id, balance - 300_000, Day, "عجز كبير", clerk.Id);
        Assert.True(big.result.Success, big.result.ErrorMessage);
        Assert.Null(big.tx);
        Assert.NotNull(big.request);
        Assert.Equal(PaymentRequestKind.CashAdjustment, big.request!.Kind);
        Assert.Equal(balance, await cash.GetBalanceAsync(box.Id));   // لم يُقيَّد شيء قبل الاعتماد
        var approved = await new PaymentApprovalService(db).ApproveAsync(big.request.Id, admin);
        Assert.True(approved.Success, approved.ErrorMessage);
        Assert.Equal(balance - 300_000, await cash.GetBalanceAsync(box.Id));

        // من لا صلاحية له لا يسوّي
        var plain = new User { Username = "adj_plain", PasswordHash = PasswordHasher.Hash("x"), RoleId = (await db.Roles.FirstAsync(r => r.Name == "موظف مبيعات")).Id };
        db.Users.Add(plain);
        await db.SaveChangesAsync();
        Assert.False((await cash.AdjustToCountAsync(box.Id, 1, Day, "ممنوع", plain.Id)).result.Success);
        box.IsActive = false;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Correcting_a_manual_move_reverses_the_old_entry_and_needs_a_reason_and_an_open_period()
    {
        Assert.True(_f.Install.Success, _f.Install.ErrorMessage);
        await using var db = _f.NewDb();
        var admin = _f.AdminLocalId;
        var cash = new CashBoxService(db);
        var Day = new DateTime(2020, 3, 10);   // فترة ماضية يمكن إقفالها (الإقفال لا يكون لشهر لم ينتهِ)
        var box = new CashBox { Name = "صندوق اختبار التصحيح", BoxType = CashBoxType.Bank };
        db.CashBoxes.Add(box);
        await db.SaveChangesAsync();
        var (d, tx) = await cash.DepositAsync(box.Id, 200_000, Day, "مالك", "إيداع", admin);
        Assert.True(d.Success, d.ErrorMessage);
        var oldEntryId = tx!.JournalEntryId!.Value;

        Assert.False((await cash.UpdateAsync(tx.Id, 250_000, Day, "إيداع", admin)).Success);                       // بلا سبب
        Assert.False((await cash.UpdateAsync(tx.Id, 250_000, Day, "إيداع", admin, " ")).Success);
        var ok = await cash.UpdateAsync(tx.Id, 250_000, Day, "إيداع مصحَّح", admin, "المبلغ الصحيح بعد المراجعة");
        Assert.True(ok.Success, ok.ErrorMessage);
        Assert.Equal(250_000, await cash.GetBalanceAsync(box.Id));

        var fresh = await db.CashBoxTransactions.AsNoTracking().SingleAsync(t => t.Id == tx.Id);
        Assert.Equal(200_000, fresh.OriginalAmount);
        Assert.NotEqual(oldEntryId, fresh.JournalEntryId);   // قيد جديد بدل تغيير القديم
        var oldLines = await db.JournalEntryLines.AsNoTracking().Where(l => l.JournalEntryId == oldEntryId).ToListAsync();
        Assert.Equal(200_000, oldLines.Sum(l => l.Debit));   // القيد القديم باقٍ كما كان
        var newLines = await db.JournalEntryLines.AsNoTracking().Where(l => l.JournalEntryId == fresh.JournalEntryId).ToListAsync();
        Assert.Equal(250_000, newLines.Sum(l => l.Debit));
        Assert.True(await db.JournalEntries.AnyAsync(j => j.SourceTable == "CashBoxTransactions" && j.SourceId == tx.Id && j.Description!.StartsWith("عكس قيد حركة صندوق مُصحَّحة")));
        // صافي القيود على حساب الصندوق = المبلغ الجديد فقط
        var net = await db.JournalEntryLines.Where(l => l.Account.AccountCode == "1101" && l.JournalEntry.SourceTable == "CashBoxTransactions" && l.JournalEntry.SourceId == tx.Id)
                          .SumAsync(l => l.Debit - l.Credit);
        Assert.Equal(250_000, net);
        Assert.True(await cash.ChangesAsync(DateTime.Today.AddDays(-1), DateTime.Today, box.Id) is { Count: 1 } changes && changes[0].Kind == "تصحيح" && changes[0].OldAmount == 200_000 && changes[0].NewAmount == 250_000);

        // التصحيح ممنوع في فترة مقفلة (حتى للأدمن)
        var locks = new PeriodLockService(db);
        await locks.CloseMonthAsync(2020, 3, admin);
        Assert.True(await locks.GetLockedThroughAsync() is DateTime lockedThrough && lockedThrough >= Day);
        try
        {
            var blocked = await cash.UpdateAsync(tx.Id, 260_000, Day, "ممنوع", admin, "فترة مقفلة");
            Assert.False(blocked.Success);
            Assert.Contains("مقفلة", blocked.ErrorMessage);
            Assert.Contains("مقفلة", (await cash.VoidAsync(tx.Id, "ممنوع", admin)).ErrorMessage);
        }
        finally
        {
            Assert.True((await locks.ReopenFromMonthAsync(2020, 1, "تنظيف الاختبار: إعادة القفل إلى ما قبل كل بيانات الاختبارات", admin)).Success);
        }
        Assert.True((await cash.VoidAsync(tx.Id, "إلغاء بعد الفتح", admin)).Success);
        box.IsActive = false;
        await db.SaveChangesAsync();
    }
}
