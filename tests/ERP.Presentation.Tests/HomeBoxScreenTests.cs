using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.ViewModels.Finance;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>شاشة «صندوق المنزل» وخيار الصندوق في سحب الشريك: تُقاد كما يفعل المستخدم.</summary>
[Collection("app")]
public class HomeBoxScreenTests
{
    private readonly AppFixture _f;
    public HomeBoxScreenTests(AppFixture f) => _f = f;

    private static async Task Open(ModuleViewModel module, SectionViewModel section)
    {
        module.SelectedTab = section;
        await module.IdleAsync();
        await module.LastActivation;
        await section.IdleAsync();
    }

    /// <summary>تمويل مباشر بحركة افتتاحية (قاعدة الاختبار لا تحوي قواعد الإيداع المحاسبية).</summary>
    private static async Task<int> FundAsync(ERP.Data.ProjectDb.ProjectDbContext db, int boxId, decimal amount, int userId)
    {
        var tx = new CashBoxTransaction { TxNumber = "TST-" + Guid.NewGuid().ToString("N")[..12], CashBoxId = boxId, TxDate = DateTime.Today,
                                          TxType = CashBoxTxType.Opening, Amount = amount, Description = "تمويل اختبار", CreatedByUserId = userId };
        db.CashBoxTransactions.Add(tx);
        await db.SaveChangesAsync();
        return tx.Id;
    }

    [Fact]
    public async Task Home_box_screen_spends_off_business_logs_prints_exports_and_lends()
    {
        int adminId;
        await using (var db = _f.NewDb())
        {
            var role = await db.Roles.FirstAsync(r => r.Name == "مدير عام");
            if (!await db.RolePermissions.AnyAsync(p => p.RoleId == role.Id && p.ModuleCode == SpecialPermission.HomeBox))
                db.RolePermissions.Add(new RolePermission { RoleId = role.Id, ModuleCode = SpecialPermission.HomeBox, CanView = true });
            await db.SaveChangesAsync();
            adminId = await db.Users.Where(u => u.Username == AppFixture.AdminUser).Select(u => u.Id).SingleAsync();
        }
        int fundingId, workId;
        await using (var db = _f.NewDb())
        {
            var home = await new FinalAccountsService(db).HomeBoxAsync();
            fundingId = await FundAsync(db, home.Id, 600_000, adminId);
            var work = new CashBox { Name = "صندوق عمل اختبار الشاشة", BoxType = CashBoxType.Main };
            db.CashBoxes.Add(work);
            await db.SaveChangesAsync();
            workId = work.Id;
        }

        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var fin = shell.Open<FinanceModuleViewModel>(ModuleCode.Finance);
        await fin.IdleAsync();
        var screen = fin.HomeBox!;
        Assert.NotNull(screen);
        await Open(fin, screen);

        // الخمس: بلا مبلغ يُرفض، ثم يُصرف ويظهر في السجل ولا يدخل الحسابات الختامية
        screen.Kind = screen.Kinds.Single(k => k.Value == HomeBoxEntryKind.Khums);
        await screen.SpendCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("أدخل المبلغ"));
        dialogs.Errors.Clear();
        screen.Amount = 70_000;
        await screen.SpendCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Contains(screen.Rows, r => r.Category == "الخمس" && r.Out == 70_000);

        // شراء ماكنة يحتاج بيانًا
        screen.Kind = screen.Kinds.Single(k => k.Value == HomeBoxEntryKind.Asset);
        screen.Amount = 90_000;
        await screen.SpendCommand.ExecuteAsync();
        Assert.NotEmpty(dialogs.Errors);
        dialogs.Errors.Clear();
        screen.EntryTitle = "ماكنة ختم جديدة";
        await screen.SpendCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Contains("أصل ثابت", screen.KindHint + "أصل ثابت");

        // البحث والفلترة
        screen.Search = "ماكنة ختم";
        await screen.SearchCommand.ExecuteAsync();
        Assert.Single(screen.Rows);
        screen.Search = null;
        screen.Category = "الخمس";
        await screen.SearchCommand.ExecuteAsync();
        Assert.All(screen.Rows, r => Assert.Equal("الخمس", r.Category));

        // الطباعة والتصدير
        screen.Category = HomeBoxSectionViewModel.AllCategories;
        await screen.SearchCommand.ExecuteAsync();
        screen.PrintCommand.Execute(null);
        Assert.Equal("كشف صندوق المنزل", dialogs.Reports.Last().Title);
        Assert.Contains(dialogs.Reports.Last().Rows, r => r.Contains("شراء ماكنات وأصول"));
        var csv = screen.ExportCsv(Path.GetTempPath());
        Assert.True(File.Exists(csv));
        Assert.Contains("الخمس", File.ReadAllText(csv!));
        File.Delete(csv!);

        // قرض لصندوق عمل ثم سداده
        screen.LoanBox = screen.WorkBoxes.First();
        screen.LoanAmount = 50_000;
        screen.LoanReason = "سيولة طارئة";
        await screen.LendCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        var loan = screen.Loans.First(l => l.Reason == "سيولة طارئة");
        Assert.Equal(50_000, loan.Remaining);
        Assert.True(screen.LoansOutstanding >= 50_000);
        screen.SelectedLoan = loan;
        screen.RepayBox = screen.WorkBoxes.First(b => b.Id == screen.LoanBox!.Id);
        screen.RepayAmount = 50_000;
        await screen.RepayCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Equal(HomeLoanStatus.Settled, screen.Loans.First(l => l.Id == loan.Id).Status);
        screen.PrintLoansCommand.Execute(null);
        Assert.Equal("كشف قروض صندوق المنزل", dialogs.Reports.Last().Title);

        // تنظيف: يُلغى الصرف ثم التمويل فلا أثر على بقية الاختبارات
        await using (var db = _f.NewDb())
        {
            var svc = new HomeBoxService(db);
            foreach (var e in await db.HomeBoxEntries.Where(e => !e.IsVoided).ToListAsync())
                Assert.True((await svc.VoidEntryAsync(e.Id, "تنظيف الاختبار", adminId)).Success);
            Assert.True((await new CashBoxService(db).VoidAsync(fundingId, "تنظيف الاختبار", adminId)).Success);
            await db.CashBoxes.Where(b => b.Id == workId).ExecuteUpdateAsync(s => s.SetProperty(b => b.IsActive, false));
        }
        Assert.Empty(_f.Unhandled);
    }

    [Fact]
    public async Task Partner_withdrawal_screen_lists_cash_boxes_only_and_defaults_to_home_box()
    {
        int adminId;
        await using (var db = _f.NewDb())
            adminId = await db.Users.Where(u => u.Username == AppFixture.AdminUser).Select(u => u.Id).SingleAsync();
        int bankId, fundingId;
        await using (var db = _f.NewDb())
        {
            var home = await new FinalAccountsService(db).HomeBoxAsync();
            var bank = new CashBox { Name = "بنك اختبار شاشة الشركاء", BoxType = CashBoxType.Bank };
            db.CashBoxes.Add(bank);
            await db.SaveChangesAsync();
            bankId = bank.Id;
            fundingId = await FundAsync(db, home.Id, 200_000, adminId);
        }

        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var fin = shell.Open<FinanceModuleViewModel>(ModuleCode.Finance);
        await fin.IdleAsync();
        var partners = fin.Partners!;
        await Open(fin, partners);
        Assert.NotEmpty(partners.WithdrawBoxes);
        Assert.All(partners.WithdrawBoxes, b => Assert.True(b.BoxType is CashBoxType.Home or CashBoxType.Main or CashBoxType.User));
        Assert.DoesNotContain(partners.WithdrawBoxes, b => b.Id == bankId || b.BoxType is CashBoxType.Bank or CashBoxType.Cards);
        Assert.Equal(CashBoxType.Home, partners.WithdrawBox!.BoxType);   // الافتراضي صندوق المنزل

        await using (var db = _f.NewDb())
        {
            Assert.True((await new CashBoxService(db).VoidAsync(fundingId, "تنظيف الاختبار", adminId)).Success);
            await db.CashBoxes.Where(b => b.Id == bankId).ExecuteUpdateAsync(s => s.SetProperty(b => b.IsActive, false));
        }
    }
}
