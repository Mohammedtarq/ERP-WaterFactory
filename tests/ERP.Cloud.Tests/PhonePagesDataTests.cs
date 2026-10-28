using System.Text.Json;
using ERP.Cloud.Contracts;
using ERP.RepApp.Core;
using Xunit;

namespace ERP.Cloud.Tests;

/// <summary>
/// بيانات الصفحات الجديدة في الهاتف من نسخة العمل: الديون بأعمارها وحدودها (الأكبر دينًا أولًا)، وعدّ الحضور والتأخر والإجازة،
/// والتبليغات بحالة المقروء، واسم السطر على الوصل بلا تكرار العبوة.
/// </summary>
public class PhonePagesDataTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rep-pages-" + Guid.NewGuid().ToString("N"));
    public PhonePagesDataTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private RepWork Work(RepSnapshot snap)
    {
        var store = new LocalStore(Path.Combine(_dir, "rep.db"));
        store.SaveLink(new LinkCode("https://cloud.test", new string('A', 64)));
        store.SaveSnapshot(JsonSerializer.Serialize(snap, new JsonSerializerOptions(JsonSerializerDefaults.Web)), "\"x\"");
        return new RepWork(store);
    }

    [Fact]
    public void Debtors_are_sorted_by_debt_with_age_severity_and_limit_fraction()
    {
        var today = new DateTime(2026, 10, 26);
        var customers = new List<SnapshotCustomer>
        {
            new(1, "أبو صفاء", null, null, "Direct", null, 200_000, 192_000, new DateTime(2026, 9, 12), new DateTime(2026, 9, 11)),   // 45 يومًا
            new(2, "الرسام كافيه", null, null, "Direct", null, 200_000, 56_250, new DateTime(2026, 10, 14), new DateTime(2026, 10, 14)), // 12 يومًا
            new(3, "حجي اثير", null, null, "Direct", null, null, 48_000, null, new DateTime(2026, 10, 23)),                              // 3 أيام، بلا حد
            new(4, "بلا دين", null, null, "Direct", null, null, 0)
        };
        var work = Work(new RepSnapshot(9, "علي", DateTime.UtcNow, true, 2, 0, new(), customers, new(), new()));
        var debtors = work.Debtors(today);

        Assert.Equal(new[] { "أبو صفاء", "الرسام كافيه", "حجي اثير" }, debtors.Select(d => d.Customer.Name));
        Assert.Equal(new int?[] { 45, 12, 3 }, debtors.Select(d => d.AgeDays));
        Assert.Equal(new[] { 2, 1, 0 }, debtors.Select(d => d.Severity));
        Assert.Equal(0.96, debtors[0].UsedFraction!.Value, 2);
        Assert.Null(debtors[2].UsedFraction);                                     // بلا حد مُحدَّد
        Assert.Equal(new DateTime(2026, 9, 12), debtors[0].LastPaymentAt);
        Assert.Equal(296_250m, work.TotalDebt());
    }

    [Fact]
    public void Month_counts_attendance_late_and_leave_and_notices_track_read_state()
    {
        var month = new SnapshotMonth(new DateTime(2026, 10, 1), new(), new(), 0, new()
        {
            new(new DateTime(2026, 10, 1), "P"), new(new DateTime(2026, 10, 2), "P"), new(new DateTime(2026, 10, 3), "L", 25, "08:25"),
            new(new DateTime(2026, 10, 4), "A"), new(new DateTime(2026, 10, 5), "V"), new(new DateTime(2026, 10, 6), "V")
        });
        var notices = new List<SnapshotNotice>
        {
            new("load:1", "load", "تم تجهيز طلب التحميل", null, new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc)),
            new("req:2", "reject", "رُفض مصروف", "السبب: يلزم صورة", new DateTime(2026, 10, 26, 9, 0, 0, DateTimeKind.Utc))
        };
        var work = Work(new RepSnapshot(9, "علي", DateTime.UtcNow, true, 2, 0, new(), new(), new(), new(), Month: month, Notices: notices));

        Assert.Equal((3, 1, 1, 2, 25), work.AttendanceCounts());                     // حضور (يشمل المتأخر)، غياب، تأخر، إجازة، دقائق التأخر
        Assert.Equal(new[] { "req:2", "load:1" }, work.Notices().Select(n => n.Notice.Id));   // الأحدث أولًا
        Assert.Equal(2, work.UnreadCount());
        work.MarkNoticesRead();
        Assert.Equal(0, work.UnreadCount());
        Assert.All(work.Notices(), n => Assert.False(n.Unread));
    }

    [Fact]
    public void Custody_balance_drops_with_queued_expenses_and_is_checked_against_the_live_balance()
    {
        var custody = new SnapshotCustodyInfo(
            new() { new(7, "H-0007", "مشتريات", new DateTime(2026, 10, 25), 300_000, 80_000, 0, 300_000) },
            new() { new(3, "نقل"), new(4, "صيانة") },
            new() { new(1, 7, new DateTime(2026, 10, 26), "Expense", 80_000, "صيانة", "قطع غيار") }, 25_000);
        var work = Work(new RepSnapshot(11, "علي حسن", DateTime.UtcNow, false, 2, 0, new(), new(), new(), new(), CustodyOnly: true, Custody: custody));
        Assert.True(work.CustodyOnly);
        Assert.Equal(220_000, work.CustodyBalance());

        Assert.False(work.CheckCustodyExpense(7, 3, 0).Ok);                       // مبلغ صفر
        Assert.False(work.CheckCustodyExpense(7, 99, 10_000).Ok);                 // باب غير معتمد
        Assert.False(work.CheckCustodyExpense(8, 3, 10_000).Ok);                  // عهدة غير موجودة
        Assert.False(work.CheckCustodyExpense(7, 3, 220_001).Ok);                 // أكبر من الرصيد
        Assert.Contains("رقم الوصل", work.CheckCustodyExpense(7, 3, 30_000).Warning);   // فوق حد الوصل: تنبيه لا منع
        Assert.Null(work.CheckCustodyExpense(7, 3, 20_000).Warning);

        var item = work.SaveCustodyExpense(7, 3, 45_000, "أجرة نقل", "R-1");
        Assert.Equal("CustodyExpense", item.Kind);
        var payload = JsonSerializer.Deserialize<CustodyExpensePayload>(item.Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal((7, 3, 45_000m, "أجرة نقل", "R-1"), (payload.CustodyId, payload.CategoryId, payload.Amount, payload.Description, payload.ReceiptNumber));
        Assert.Equal(175_000, work.CustodyBalance());                             // يخصم فورًا قبل وصول المعمل
        Assert.False(work.CheckCustodyExpense(7, 3, 175_001).Ok);

        var rows = work.CustodyExpenses();
        Assert.Equal(new[] { true, false }, rows.Select(r => r.Pending));         // المنتظر أولًا ثم المرحَّل
        Assert.Equal(new[] { 45_000m, 80_000m }, rows.Select(r => r.Amount));
    }

    [Fact]
    public void Old_snapshots_without_the_new_fields_still_load()
    {
        var work = Work(new RepSnapshot(9, "علي", DateTime.UtcNow, true, 2, 0, new(), new() { new(1, "زبون", null, null, "Direct", null, null, 5_000) }, new(), new()));
        Assert.Null(work.Month());
        Assert.Empty(work.Notices());
        var d = Assert.Single(work.Debtors());
        Assert.Null(d.AgeDays);
        Assert.Equal(0, d.Severity);
        Assert.Equal((0, 0, 0, 0, 0), work.AttendanceCounts());
    }

    [Fact]
    public void Receipt_line_shows_the_unified_name_once()
    {
        Assert.Equal("شرنك 20×330 الرحمة", Receipt.LineName(new ReceiptLine("شرنك 20×330 الرحمة", "شرنك", 3, 2_250, 6_750)));
        Assert.Equal("ماء — كارتون", Receipt.LineName(new ReceiptLine("ماء", "كارتون", 1, 4_250, 4_250)));   // وصل أقدم: الصنف ثم العبوة
        var receipt = new Receipt("المعمل", null, "7-1", DateTime.Today, "بيع نقدي", "زبون", "علي",
                                  new() { new ReceiptLine("شرنك 20×330 الرحمة", "شرنك", 3, 2_250, 6_750) }, 6_750);
        Assert.DoesNotContain(receipt.TextLines(), l => l.Contains("— شرنك"));
    }
}
