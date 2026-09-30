using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Security;
using ERP.Data.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>
/// سيناريو رواتب آب 2026 بأرقام محسوبة يدويًا مسبقًا (راجع التعليقات عند كل Assert):
/// موظف دينار بزيادة دائمة ومكافأة وغياب وتأخير وحافز شهري، موظف دولار، مندوب، ومدير مبيعات.
/// </summary>
public class HrFixture
{
    public const int Month = 8, Year = 2026;
    public int UserId, AhmedId, UsdId, RepId, ManagerId, ItemId;

    public ProjectDbContext NewDb() => new SalesFixtureConnection().NewDb();

    public HrFixture()
    {
        using var db = NewDb();
        var role = new Role { Name = "HR-Test" };
        db.Roles.Add(role);
        db.SaveChanges();
        var user = new User { Username = "hr-admin", PasswordHash = PasswordHasher.Hash("x"), RoleId = role.Id };
        db.Users.Add(user);

        var shift = new Shift { Name = "الصباحي", CheckInTime = new TimeSpan(8, 0, 0), CheckInGraceMinutes = 10, CheckOutTime = new TimeSpan(16, 0, 0) };
        db.Shifts.Add(shift);
        db.SaveChanges();

        var ahmed = new Employee { FullName = "أحمد المحاسب", BaseSalary = 900_000, ShiftId = shift.Id };
        var usd = new Employee { FullName = "خبير الدولار", BaseSalary = 1_000, SalaryCurrency = SalaryCurrency.USD };
        var rep = new Employee { FullName = "مندوب آب", BaseSalary = 600_000, IsSalesRep = true };
        var manager = new Employee { FullName = "مدير المبيعات", BaseSalary = 1_200_000, IsSalesManager = true, ShiftId = shift.Id };
        db.Employees.AddRange(ahmed, usd, rep, manager);

        var expense = new ChartOfAccount { AccountCode = "5201", AccountName = "مصروف الرواتب", AccountType = AccountType.Expense };
        var payable = new ChartOfAccount { AccountCode = "2201", AccountName = "رواتب مستحقة", AccountType = AccountType.Liability };
        db.ChartOfAccounts.AddRange(expense, payable);
        db.SaveChanges();
        db.AccountMappingRules.Add(new AccountMappingRule { TransactionType = HrRules.PayrollMappingRule, DebitAccountId = expense.Id, CreditAccountId = payable.Id });

        db.ExchangeRates.AddRange(
            new ExchangeRate { EffectiveDate = new DateTime(2026, 8, 1), RateToIQD = 1500, EnteredByUserId = user.Id },
            new ExchangeRate { EffectiveDate = new DateTime(2026, 9, 10), RateToIQD = 1600, EnteredByUserId = user.Id });   // بعد الشهر: لا يُطبَّق

        // زيادة دائمة 100,000 من تموز (تدخل في الأساسي)، مكافأة لمرة واحدة في آب (بدل)، ومكافأة تموز (لا تدخل)
        db.PromotionsAndRaises.AddRange(
            new PromotionAndRaise { EmployeeId = ahmed.Id, MovementType = PromotionMovementType.AnnualRaise, Amount = 100_000,
                                    EffectiveDate = new DateTime(2026, 7, 1), ApplicationType = PromotionApplicationType.PermanentAddition, CreatedByUserId = user.Id },
            new PromotionAndRaise { EmployeeId = ahmed.Id, MovementType = PromotionMovementType.AnnualBonus, Amount = 50_000,
                                    EffectiveDate = new DateTime(2026, 8, 15), ApplicationType = PromotionApplicationType.OneTime, CreatedByUserId = user.Id },
            new PromotionAndRaise { EmployeeId = ahmed.Id, MovementType = PromotionMovementType.AnnualBonus, Amount = 70_000,
                                    EffectiveDate = new DateTime(2026, 7, 20), ApplicationType = PromotionApplicationType.OneTime, CreatedByUserId = user.Id });

        // مقياس الحافز الشهري (الأوزان الافتراضية 40/30/30)
        if (!db.IncentiveScoreWeights.Any()) db.IncentiveScoreWeights.Add(new IncentiveScoreWeights());
        db.IncentiveScoreToAmountScale.AddRange(
            new IncentiveScoreToAmountScale { MinScore = 0, MaxScore = 59.99m, Amount = 0 },
            new IncentiveScoreToAmountScale { MinScore = 60, MaxScore = 79.99m, Amount = 50_000 },
            new IncentiveScoreToAmountScale { MinScore = 80, MaxScore = 100, Amount = 100_000 });

        // مبيعات آب لحساب حوافز المبيعات (تُكتب مرحّلة مباشرة — الحوافز تقرأ الفواتير فقط)
        var item = new Item { ItemCode = "HR-W", ItemName = "ماء حوافز", SalePrice = 250 };
        var branch = new Branch { Name = "فرع الحوافز" };
        db.Items.Add(item);
        db.Branches.Add(branch);
        db.SaveChanges();
        db.RepItemIncentiveRates.Add(new RepItemIncentiveRate { ItemId = item.Id, IncentiveRatePerUnit = 25 });
        db.SalesManagerIncentiveTiers.AddRange(
            new SalesManagerIncentiveTier { EmployeeId = manager.Id, FromQuantity = 0, ToQuantity = 200, RatePerUnit = 2 },
            new SalesManagerIncentiveTier { EmployeeId = manager.Id, FromQuantity = 200, ToQuantity = null, RatePerUnit = 3 });
        var level = new ItemPackagingLevel { ItemId = item.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 };
        var wh = new Warehouse { BranchId = branch.Id, Name = "مخزن الحوافز", WarehouseType = WarehouseType.Main };
        var customer = new Customer { Name = "عميل الحوافز" };
        db.AddRange(level, wh, customer);
        db.SaveChanges();

        int n = 0;
        void Invoice(DateTime date, int? repId, decimal qty, bool free = false, DocumentStatus status = DocumentStatus.Posted)
        {
            var inv = new SalesInvoice
            {
                InvoiceNumber = $"HR-{++n}", CustomerId = customer.Id, WarehouseId = wh.Id, InvoiceDate = date,
                PaymentMethod = InvoicePaymentMethod.Cash, SalesRepEmployeeId = repId, IsFreeSale = free,
                FreeSaleRecipient = free ? "جهة" : null, Status = status, CreatedByUserId = user.Id
            };
            inv.Lines.Add(new SalesInvoiceLine { ItemId = item.Id, PackagingLevelId = level.Id, QuantityInLevel = qty, QuantityBaseUnits = qty, UnitPrice = 250, LineTotal = qty * 250 });
            db.SalesInvoices.Add(inv);
        }
        Invoice(new DateTime(2026, 8, 5), rep.Id, 120);                              // يُحسب للمندوب وللمدير
        Invoice(new DateTime(2026, 8, 6), rep.Id, 60, free: true);                   // مجاني: لا يُحسب
        Invoice(new DateTime(2026, 8, 7), rep.Id, 100, status: DocumentStatus.Draft); // مسودة: لا تُحسب
        Invoice(new DateTime(2026, 8, 20), null, 380);                               // للمدير فقط
        Invoice(new DateTime(2026, 9, 2), rep.Id, 999);                              // الشهر التالي: لا يُحسب
        db.SaveChanges();

        (UserId, AhmedId, UsdId, RepId, ManagerId, ItemId) = (user.Id, ahmed.Id, usd.Id, rep.Id, manager.Id, item.Id);
    }
}

/// <summary>نفس سلسلة اتصال قاعدة اختبارات التكامل.</summary>
internal class SalesFixtureConnection
{
    public ProjectDbContext NewDb() => new(new DbContextOptionsBuilder<ProjectDbContext>()
        .UseSqlServer(Environment.GetEnvironmentVariable("ERP_TEST_CONNECTION")!).Options);
}

[CollectionDefinition("hr")] public class HrCollection : ICollectionFixture<HrFixture> { }

[Collection("hr")]
public class HrServiceTests
{
    private readonly HrFixture _f;
    public HrServiceTests(HrFixture f) => _f = f;

    [Theory]
    [InlineData("08:05", AttendanceStatus.Present, 0)]
    [InlineData("08:10", AttendanceStatus.Present, 0)]     // ضمن السماح (10 دقائق)
    [InlineData("08:11", AttendanceStatus.Late, 11)]       // تجاوز السماح: التأخير من بداية الشفت
    [InlineData("09:30", AttendanceStatus.Late, 90)]
    public void Classify_uses_shift_start_and_grace(string checkIn, AttendanceStatus expected, int lateMinutes)
    {
        var shift = new Shift { CheckInTime = new TimeSpan(8, 0, 0), CheckInGraceMinutes = 10 };
        var (status, late) = HrService.Classify(shift, null, TimeSpan.Parse(checkIn));
        Assert.Equal(expected, status);
        Assert.Equal(lateMinutes, late);
        Assert.Equal((AttendanceStatus.Absent, 0), HrService.Classify(shift, null, null));
        Assert.Equal((AttendanceStatus.ApprovedLeave, 0), HrService.Classify(shift, AttendanceStatus.ApprovedLeave, TimeSpan.Parse(checkIn)));
    }

    [Fact]
    public async Task Full_month_attendance_incentives_payroll_and_approval()
    {
        await using var db = _f.NewDb();
        var hr = new HrService(db);
        var start = new DateTime(HrFixture.Year, HrFixture.Month, 1);

        // أحمد: 16 حاضر + 2 متأخر + 2 غائب + 1 إجازة معتمدة. المدير: 22 يوم حضور
        for (int d = 0; d < 21; d++)
        {
            var (forced, time) = d switch
            {
                < 16 => ((AttendanceStatus?)null, (TimeSpan?)new TimeSpan(8, 5, 0)),
                < 18 => (null, new TimeSpan(8, 30, 0)),
                < 20 => (null, null),
                _ => (AttendanceStatus.ApprovedLeave, null)
            };
            var r = await hr.SaveAttendanceAsync(start.AddDays(d), new[] { new AttendanceInput(_f.AhmedId, forced, time, new TimeSpan(16, 0, 0)) });
            Assert.True(r.Success, r.ErrorMessage);
        }
        for (int d = 0; d < 22; d++)
            await hr.SaveAttendanceAsync(start.AddDays(d), new[] { new AttendanceInput(_f.ManagerId, null, new TimeSpan(7, 55, 0), null) });

        // إعادة حفظ نفس اليوم تحدّث السجل ولا تكرره
        await hr.SaveAttendanceAsync(start, new[] { new AttendanceInput(_f.AhmedId, null, new TimeSpan(8, 5, 0), null) });
        Assert.Equal(21, await db.AttendanceRecords.CountAsync(a => a.EmployeeId == _f.AhmedId));
        Assert.Equal(30, (await db.AttendanceRecords.SingleAsync(a => a.EmployeeId == _f.AhmedId && a.AttendanceDate == start.AddDays(16))).LateMinutes);

        // الانضباط = (16×1 + 2×0.5) ÷ 20 × 100 = 85
        Assert.Equal(85m, await hr.ComputeAttendanceScoreAsync(_f.AhmedId, HrFixture.Month, HrFixture.Year));

        // الحافز الشهري: (85×40 + 90×30 + 70×30) ÷ 100 = 82 ← شريحة 80–100 = 100,000
        var (er, b) = await hr.SaveEvaluationAsync(_f.AhmedId, HrFixture.Month, HrFixture.Year, 90, 70);
        Assert.True(er.Success, er.ErrorMessage);
        Assert.Equal(82m, b!.TotalScore);
        Assert.Equal(100_000m, b.Amount);
        Assert.False((await hr.SaveEvaluationAsync(_f.AhmedId, HrFixture.Month, HrFixture.Year, 120, 70)).result.Success);

        // حافز المندوب: 120 قطعة مرحّلة غير مجانية × 25 = 3,000
        Assert.Equal(3_000m, await hr.ComputeRepIncentiveAsync(_f.RepId, HrFixture.Month, HrFixture.Year));

        // حافز المدير: مبيعات آب = 120 + 380 = 500 ← 200×2 + 300×3 = 1,300 × 22 يوم = 28,600
        var mgr = await hr.ComputeSalesManagerIncentiveAsync(_f.ManagerId, HrFixture.Month, HrFixture.Year);
        Assert.Equal(500m, mgr.soldQuantity);
        Assert.Equal(1_300m, mgr.tierSum);
        Assert.Equal(22, mgr.workedDays);
        Assert.Equal(28_600m, mgr.amount);

        // ------------------ الرواتب ------------------
        var (gr, runId) = await hr.GenerateAsync(HrFixture.Month, HrFixture.Year);
        Assert.True(gr.Success, gr.ErrorMessage);
        var lines = await db.PayrollLines.AsNoTracking().Where(l => l.PayrollRunId == runId).ToDictionaryAsync(l => l.EmployeeId);

        var a = lines[_f.AhmedId];
        Assert.Equal(1_000_000m, a.BaseSalary);                 // 900,000 + زيادة دائمة 100,000
        Assert.Equal(50_000m, a.Allowances);                    // مكافأة آب فقط (تموز لا)
        Assert.Equal(66_666.67m, a.AbsenceDeduction);           // 1,000,000 ÷ 30 × 2
        Assert.Equal(100_000m, a.MonthlyIncentiveAmount);
        Assert.Equal(1_083_333.33m, a.NetSalary);

        Assert.Equal(603_000m, lines[_f.RepId].NetSalary);      // 600,000 + 3,000
        Assert.Equal(1_228_600m, lines[_f.ManagerId].NetSalary);// 1,200,000 + 28,600
        var u = lines[_f.UsdId];
        Assert.Equal("USD", u.Currency);
        Assert.Equal(1_000m, u.NetSalary);

        // إعادة التوليد تستبدل السطور ولا تكررها
        await hr.GenerateAsync(HrFixture.Month, HrFixture.Year);
        Assert.Equal(lines.Count, await db.PayrollLines.CountAsync(l => l.PayrollRunId == runId));

        // ------------------ الاعتماد ------------------
        var (ar, summary) = await hr.ApproveAsync(runId!.Value, _f.UserId);
        Assert.True(ar.Success, ar.ErrorMessage);
        Assert.Equal(1_000m, summary!.TotalNetUsd);
        Assert.Equal(1500m, summary.UsdRate);                    // سعر آب، لا سعر أيلول
        Assert.Equal(summary.TotalNetIqd + 1_500_000m, summary.TotalInIqd);

        var run = await db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == runId);
        Assert.Equal(PayrollRunStatus.Approved, run.Status);
        var je = await db.JournalEntries.AsNoTracking().Include(j => j.Lines).SingleAsync(j => j.Id == run.JournalEntryId);
        Assert.True(je.IsPosted && je.IsBalanced);
        Assert.Equal(JournalEntryType.AutoPayroll, je.EntryType);
        Assert.Equal(summary.TotalInIqd, je.Lines.Sum(l => l.Debit));

        // الشهر مقفل بعد الاعتماد
        Assert.False((await hr.GenerateAsync(HrFixture.Month, HrFixture.Year)).result.Success);
        Assert.False((await hr.ApproveAsync(runId.Value, _f.UserId)).result.Success);
        Assert.Contains("معتمدة", (await hr.SaveAttendanceAsync(start, new[] { new AttendanceInput(_f.AhmedId, null, null, null) })).ErrorMessage);
        Assert.False((await hr.SaveEvaluationAsync(_f.AhmedId, HrFixture.Month, HrFixture.Year, 50, 50)).result.Success);
    }

    [Fact]
    public async Task Usd_payroll_without_exchange_rate_is_refused()
    {
        await using var db = _f.NewDb();
        // شهر قبل أي سعر صرف مسجّل
        var (r, _) = await new HrService(db).GenerateAsync(1, 2020);
        Assert.False(r.Success);
        Assert.Contains("سعر صرف", r.ErrorMessage);
    }
}
