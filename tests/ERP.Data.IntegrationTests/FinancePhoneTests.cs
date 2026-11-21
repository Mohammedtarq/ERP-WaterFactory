using System.Text.Json;
using ERP.Cloud.Contracts;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>هاتف المدير المالي: يرى كل الصناديق وأرصدتها، ويصرف من صندوقه وحده بلا حد، ويُرفض أي صرف من غيره أو من هاتف غيره أو فوق الرصيد.</summary>
[Collection("provisioned")]
public class FinancePhoneTests
{
    private readonly ProvisionedFixture _f;
    public FinancePhoneTests(ProvisionedFixture f) => _f = f;

    private static readonly DateTime Day = new(2032, 4, 10);

    [Fact]
    public async Task Finance_manager_phone_sees_all_boxes_and_spends_from_his_own_box_only_without_limits()
    {
        Assert.True(_f.Install.Success, _f.Install.ErrorMessage);
        await using var db = _f.NewDb();
        var admin = _f.AdminLocalId;
        var manager = new Employee { FullName = "مدير مالي — اختبار الهاتف", BaseSalary = 900_000, HireDate = new DateTime(2030, 1, 1) };
        var other = new Employee { FullName = "موظف آخر — اختبار صندوق الهاتف", BaseSalary = 400_000, HireDate = new DateTime(2030, 1, 1) };
        var category = new FinanceCategory { Name = "مصروف صندوق الهاتف — اختبار", Kind = FinanceCategoryKind.Operating };
        var income = new FinanceCategory { Name = "إيراد صندوق الهاتف — اختبار", Kind = FinanceCategoryKind.OtherIncome };
        // صناديق بلا مالك: لا يلتقطها التسجيل التلقائي لحركات الاختبارات الأخرى
        var own = new CashBox { Name = "صندوق المدير المالي — اختبار الهاتف", BoxType = CashBoxType.Bank };
        var theirs = new CashBox { Name = "صندوق آخر — اختبار الهاتف", BoxType = CashBoxType.Bank };
        db.AddRange(manager, other, category, income, own, theirs);
        await db.SaveChangesAsync();
        var boxes = new CashBoxService(db);
        Assert.True((await boxes.DepositAsync(own.Id, 500_000, Day, "تمويل", null, admin)).result.Success);
        Assert.True((await boxes.DepositAsync(theirs.Id, 300_000, Day, "تمويل", null, admin)).result.Success);

        var app = new RepAppService(db);
        var (reg, key) = await app.RegisterDeviceAsync(manager.Id, "هاتف المدير المالي", admin);
        Assert.True(reg.Success, reg.ErrorMessage);
        var (_, otherKey) = await app.RegisterDeviceAsync(other.Id, "هاتف موظف آخر", admin);

        // قبل الضبط: لا واجهة صندوق (موظف غير مندوب بلا عهدة)، والضبط الناقص مرفوض
        Assert.Null((await new CloudSyncService(db).BuildSnapshotAsync(manager.Id))!.Finance);
        Assert.False((await app.SaveFinancePhoneAsync(manager.Id, null, admin)).Success);
        Assert.False((await app.SaveFinancePhoneAsync(null, own.Id, admin)).Success);
        Assert.True((await app.SaveFinancePhoneAsync(manager.Id, own.Id, admin)).Success);
        try
        {
            var snap = (await new CloudSyncService(db).BuildSnapshotAsync(manager.Id))!;
            var finance = Assert.IsType<SnapshotFinance>(snap.Finance);
            Assert.False(snap.CustodyOnly);
            Assert.Empty(snap.Products);                                              // واجهة الصندوق فقط: لا منتجات ولا زبائن
            Assert.Equal(own.Id, finance.OwnBoxId);
            Assert.Equal(500_000, Assert.Single(finance.Boxes, b => b.Own).Balance);
            Assert.Contains(finance.Boxes, b => b.Id == theirs.Id && b.Balance == 300_000 && !b.Own);   // يرى صندوق غيره للاطلاع
            Assert.Equal(own.Id, finance.Boxes[0].Id);                                // صندوقه أولًا
            Assert.Contains(finance.Categories, c => c.Id == category.Id);
            Assert.DoesNotContain(finance.Categories, c => c.Id == income.Id);       // لا باب إيراد في المصروف
            Assert.Contains(finance.Recent, t => t.BoxId == own.Id && t.Amount == 500_000);

            RepRequestEnvelope Env(object payload, Guid? cid = null) =>
                new(cid ?? Guid.NewGuid(), RepRequestKind.BoxExpense, DateTime.Now, JsonSerializer.Serialize(payload), null);
            var rid = Guid.NewGuid();
            var env = Env(new BoxExpensePayload(own.Id, category.Id, 120_000, "مكافأة عمال", "R-9"), rid);
            var first = await app.ReceiveAsync(key!, env);
            Assert.True(first.Accepted, first.Message);
            Assert.Equal(RepRequestStatus.Posted, first.Status);
            var again = await app.ReceiveAsync(key!, env);                           // إعادة الإرسال لا تكرر المصروف
            Assert.Equal(first.RequestId, again.RequestId);
            Assert.Equal(380_000, await boxes.GetBalanceAsync(own.Id));
            var entry = await db.FinanceEntries.SingleAsync(x => x.CategoryId == category.Id);
            Assert.Equal((120_000m, "R-9"), (entry.Amount, entry.ReceiptNumber));
            var cashTx = await db.CashBoxTransactions.SingleAsync(t => t.ReferenceTable == "FinanceEntries" && t.ReferenceId == entry.Id);
            Assert.Equal((own.Id, -120_000m), (cashTx.CashBoxId, cashTx.Amount));    // خرج من صندوقه هو لا من صندوق آخر

            // أكبر من الرصيد، وصندوق غيره، وهاتف غيره، وباب إيراد: مرفوضة بسبب مفهوم ولا تمس الأرصدة
            var over = await app.ReceiveAsync(key!, Env(new BoxExpensePayload(own.Id, category.Id, 380_001)));
            Assert.Equal(RepRequestStatus.Failed, over.Status);
            Assert.Contains("رصيد الصندوق", over.Message);
            var wrongBox = await app.ReceiveAsync(key!, Env(new BoxExpensePayload(theirs.Id, category.Id, 1_000)));
            Assert.Equal(RepRequestStatus.Failed, wrongBox.Status);
            Assert.Contains("صندوق المدير المالي فقط", wrongBox.Message);
            var stolen = await app.ReceiveAsync(otherKey!, Env(new BoxExpensePayload(own.Id, category.Id, 1_000)));
            Assert.Equal(RepRequestStatus.Failed, stolen.Status);
            Assert.Contains("ليس هاتف المدير المالي", stolen.Message);
            var asIncome = await app.ReceiveAsync(key!, Env(new BoxExpensePayload(own.Id, income.Id, 1_000)));
            Assert.Equal(RepRequestStatus.Failed, asIncome.Status);
            Assert.Equal((380_000m, 300_000m), (await boxes.GetBalanceAsync(own.Id), await boxes.GetBalanceAsync(theirs.Id)));

            // بلا حد موافقة: مصروف كبير بعد وضع حد صغير على من سجّل الجهاز يمرّ
            var big = await app.ReceiveAsync(key!, Env(new BoxExpensePayload(own.Id, category.Id, 300_000)));
            Assert.Equal(RepRequestStatus.Posted, big.Status);
            Assert.Equal(80_000, await boxes.GetBalanceAsync(own.Id));

            // الهاتف يرى الرصيد الجديد وحركته، والمرفوض يظهر تبليغًا
            var after = (await new CloudSyncService(db).BuildSnapshotAsync(manager.Id))!;
            Assert.Equal(80_000, after.Finance!.Boxes.Single(b => b.Own).Balance);
            Assert.Contains(after.Finance.Recent, t => t.BoxId == own.Id && t.Amount == -120_000);
            Assert.Contains(after.PostedClientIds!, g => g == rid);
            Assert.Contains(after.Notices!, n => n.Kind == "reject" && n.Title.Contains("مصروف من الصندوق"));

            // إلغاء الواجهة يعيد النسخة العادية
            Assert.True((await app.SaveFinancePhoneAsync(null, null, admin)).Success);
            Assert.Null((await new CloudSyncService(db).BuildSnapshotAsync(manager.Id))!.Finance);
        }
        finally { await app.SaveFinancePhoneAsync(null, null, admin); }                // لا يترك الإعداد لاختبارات أخرى
    }
}
