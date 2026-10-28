using ERP.Cloud.Contracts;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>
/// ما يُضاف لنسخة عمل المندوب لصفحاته الجديدة: «شهري» (الحافز بالمبلغ لكل صنف وأسبوع، والحضور والغياب والتأخر والإجازة بأيام الشهر)،
/// وأعمار ديون الزبائن، والتبليغات. لا راتب ولا خصم ولا رصيد إجازات، وبيانات المندوب نفسه فقط.
/// </summary>
public class RepSnapshotExtrasService
{
    private readonly ProjectDbContext _db;
    public RepSnapshotExtrasService(ProjectDbContext db) => _db = db;

    /// <summary>مدة حفظ كتلة «شهري» في الذاكرة بالثواني (الحافز يتطلب عدة استعلامات، ولا حاجة لإعادتها كل دورة مزامنة). صفر = بلا حفظ.</summary>
    public static int MonthCacheSeconds { get; set; } = 300;
    private static readonly Dictionary<(string Db, int Rep, DateTime Day), (DateTime At, SnapshotMonth Month)> Cache = new();
    private static readonly object CacheLock = new();

    public async Task<SnapshotMonth> MonthAsync(int repEmployeeId, DateTime? today = null)
    {
        var day = (today ?? DateTime.Today).Date;
        var key = (_db.Database.GetConnectionString() ?? "", repEmployeeId, day);
        if (MonthCacheSeconds > 0)
            lock (CacheLock)
                if (Cache.TryGetValue(key, out var hit) && (DateTime.UtcNow - hit.At).TotalSeconds < MonthCacheSeconds) return hit.Month;

        var first = new DateTime(day.Year, day.Month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        var incentive = new RepIncentiveService(_db);
        var rows = await incentive.RowsAsync(first, day, repEmployeeId);
        var names = await ProductNames.LoadAsync(_db, rows.Select(r => r.ItemId).Distinct().ToList());
        var lines = rows.Where(r => r.Net != 0 || r.Amount != 0)
            .Select(r => new SnapshotIncentiveLine(names.Name(r.ItemId, null, r.PackagingLevelId), r.Net, r.Rate, r.Amount)).ToList();

        // أسابيع الشهر (تبدأ السبت): مبلغ حافز كل أسبوع داخل حدود الشهر
        var weeks = new List<SnapshotWeek>();
        for (var ws = ReportsService.WeekStart(first); ws <= day; ws = ws.AddDays(7))
        {
            var from = ws < first ? first : ws;
            var to = ws.AddDays(6) > day ? day : ws.AddDays(6);
            var amount = (await incentive.RowsAsync(from, to, repEmployeeId)).Sum(r => r.Amount);
            weeks.Add(new SnapshotWeek(from, to, amount));
        }

        var attendance = await _db.AttendanceRecords.AsNoTracking()
            .Where(a => a.EmployeeId == repEmployeeId && a.AttendanceDate >= first && a.AttendanceDate <= last).ToListAsync();
        var leaves = await _db.EmployeeLeaves.AsNoTracking()
            .Where(l => l.EmployeeId == repEmployeeId && !l.IsVoided && l.Kind == LeaveKind.Day && l.FromDate <= last && l.ToDate >= first)
            .Select(l => new { l.FromDate, l.ToDate }).ToListAsync();
        var days = new List<SnapshotDay>();
        for (var d = first; d <= last && d <= day; d = d.AddDays(1))
        {
            var a = attendance.FirstOrDefault(x => x.AttendanceDate.Date == d);
            var onLeave = a?.Status == AttendanceStatus.ApprovedLeave || leaves.Any(l => d >= l.FromDate.Date && d <= l.ToDate.Date);
            var status = onLeave ? "V" : a?.Status switch
            {
                AttendanceStatus.Present => "P", AttendanceStatus.Late => "L", AttendanceStatus.Absent => "A", _ => null
            };
            if (status is null) continue;
            days.Add(new SnapshotDay(d, status, status == "L" ? a!.LateMinutes : 0, a?.CheckInTime is { } t ? $"{(int)t.TotalHours:00}:{t.Minutes:00}" : null));
        }

        var month = new SnapshotMonth(first, lines, weeks, lines.Sum(l => l.Amount), days);
        if (MonthCacheSeconds > 0) lock (CacheLock) Cache[key] = (DateTime.UtcNow, month);
        return month;
    }

    /// <summary>تاريخ آخر دفعة وأقدم فاتورة غير مسددة لكل زبون مدين: لعمر الدين في تطبيق المندوب.</summary>
    public async Task<Dictionary<int, (DateTime? LastPayment, DateTime? OldestDebt)>> DebtDatesAsync(IReadOnlyCollection<int> customerIds)
    {
        var result = new Dictionary<int, (DateTime?, DateTime?)>();
        if (customerIds.Count == 0) return result;
        var oldest = await _db.SalesInvoices.AsNoTracking()
            .Where(i => customerIds.Contains(i.CustomerId) && i.Status == DocumentStatus.Posted && !i.IsFreeSale && !i.IsOpeningBalance && i.AmountRemaining > 0)
            .GroupBy(i => i.CustomerId).Select(g => new { Id = g.Key, Date = g.Min(i => i.InvoiceDate) }).ToListAsync();
        var paid = await _db.Vouchers.AsNoTracking()
            .Where(v => v.PartyType == VoucherPartyType.Customer && v.PartyId != null && customerIds.Contains(v.PartyId.Value)
                        && v.VoucherType == VoucherType.Receipt && !v.IsVoided)
            .GroupBy(v => v.PartyId!.Value).Select(g => new { Id = g.Key, Date = g.Max(v => v.VoucherDate) }).ToListAsync();
        foreach (var id in customerIds)
            result[id] = (paid.FirstOrDefault(p => p.Id == id)?.Date, oldest.FirstOrDefault(o => o.Id == id)?.Date);
        return result;
    }

    /// <summary>تبليغات آخر 14 يومًا للمندوب: تجهيز الحمولة، رفض الحركات، اعتماد ما يحتاج مراجعة، تأكيد التسوية، وتنبيه النقد المتأخر.</summary>
    public async Task<List<SnapshotNotice>> NoticesAsync(int repEmployeeId, decimal walletBalance, int cashAlertDays, DateTime? now = null)
    {
        var at = now ?? DateTime.UtcNow;
        var since = at.AddDays(-14);
        var notices = new List<SnapshotNotice>();

        foreach (var o in await _db.RepLoadOrders.AsNoTracking()
                     .Where(o => o.RepEmployeeId == repEmployeeId && o.Status == RepLoadOrderStatus.Prepared && o.PreparedAt >= since).ToListAsync())
            notices.Add(new SnapshotNotice($"load:{o.Id}", "load", $"تم تجهيز طلب التحميل {o.OrderNumber}", "استلم الحمولة من المخزن وراجع رصيد سيارتك", o.PreparedAt!.Value));

        foreach (var r in await _db.RepRequests.AsNoTracking()
                     .Where(r => r.RepEmployeeId == repEmployeeId && r.ReceivedAt >= since
                                 && (r.Status == RepRequestStatus.Rejected || (r.Status == RepRequestStatus.Posted && r.ReviewedAt != null)))
                     .ToListAsync())
        {
            var what = KindText(r.Kind);
            notices.Add(r.Status == RepRequestStatus.Rejected
                ? new SnapshotNotice($"req:{r.Id}", "reject", $"رُفض {what}", string.IsNullOrWhiteSpace(r.RejectReason) ? r.Summary : $"السبب: {r.RejectReason}. {r.Summary}", r.ReviewedAt ?? r.ReceivedAt)
                : new SnapshotNotice($"req:{r.Id}", "approve", $"اعتُمد {what}", r.Summary, r.ReviewedAt ?? r.ReceivedAt));
        }

        foreach (var s in await _db.RepSettlements.AsNoTracking()
                     .Where(s => s.RepEmployeeId == repEmployeeId && s.CreatedAt >= since && s.Status != RepSettlementStatus.PendingWarehouse).ToListAsync())
            notices.Add(new SnapshotNotice($"settle:{s.Id}", "settle", $"تأكيد تسوية {s.SettlementNumber}", $"تسوية {s.SettlementDate:yyyy/MM/dd}", s.CreatedAt));

        if (walletBalance > 0)
        {
            var lastSettle = await _db.RepSettlements.AsNoTracking().Where(s => s.RepEmployeeId == repEmployeeId).MaxAsync(s => (DateTime?)s.SettlementDate);
            var days = lastSettle is null ? (int?)null : (int)(at.ToLocalTime().Date - lastSettle.Value.Date).TotalDays;
            if (days is int d && d >= cashAlertDays)
                notices.Add(new SnapshotNotice($"cash:{at.ToLocalTime():yyyyMMdd}", "cash", $"النقد بحوزتك منذ {d} أيام", "سلّم النقد للمالية وأكمل التسوية", at));
        }
        return notices.OrderByDescending(n => n.At).Take(40).ToList();
    }

    /// <summary>عهد الموظف الفعّالة وأبواب المصاريف وآخر حركاته (30 يومًا)؛ null إن لم تكن له عهدة حديثة.</summary>
    public async Task<SnapshotCustodyInfo?> CustodyAsync(int employeeId)
    {
        var svc = new CustodyService(_db);
        var active = await svc.ActiveOfAsync(employeeId);
        var since = DateTime.Today.AddDays(-30);
        var recent = (await _db.CustodyTransactions.AsNoTracking().Where(t => t.Custody.EmployeeId == employeeId && !t.IsVoided && t.TxDate >= since)
                .OrderByDescending(t => t.Id).Take(40)
                .Select(t => new { t.Id, t.CustodyId, t.TxDate, t.Kind, t.Amount, Category = t.Category != null ? t.Category.Name : null, t.Description }).ToListAsync())
            .Select(t => new SnapshotCustodyTx(t.Id, t.CustodyId, t.TxDate, t.Kind.ToString(), t.Amount, t.Category, t.Description)).ToList();
        if (active.Count == 0 && recent.Count == 0) return null;
        var categories = active.Count == 0 ? new List<SnapshotCategory>()
            : (await new FinanceEntryService(_db).GetCategoriesAsync()).Where(c => c.Kind != FinanceCategoryKind.OtherIncome).Select(c => new SnapshotCategory(c.Id, c.Name)).ToList();
        return new SnapshotCustodyInfo(active.Select(c => new SnapshotCustody(c.Id, c.Number, c.Purpose, c.IssuedAt, c.Issued, c.Spent, c.Returned, c.TargetAmount)).ToList(),
                                       categories, recent, (await svc.SettingsAsync()).CustodyReceiptAbove);
    }

    /// <summary>تبليغات العهدة: وصلتك عهدة (آخر 14 يومًا)، ومصروف رُفض من الهاتف.</summary>
    public async Task<List<SnapshotNotice>> CustodyNoticesAsync(int employeeId, DateTime? now = null)
    {
        var since = (now ?? DateTime.UtcNow).AddDays(-14);
        var notices = new List<SnapshotNotice>();
        foreach (var t in await _db.CustodyTransactions.AsNoTracking()
                     .Where(t => t.Custody.EmployeeId == employeeId && t.Kind == CustodyTxKind.Issue && !t.IsVoided && t.CreatedAt >= since)
                     .Select(t => new { t.Id, t.Amount, t.CreatedAt, t.Custody.Purpose, t.Custody.CustodyNumber }).ToListAsync())
            notices.Add(new SnapshotNotice($"custody:{t.Id}", "custody", $"وصلتك عهدة {t.Amount:N0} د.ع", $"{t.CustodyNumber} — {t.Purpose}", t.CreatedAt));
        foreach (var r in await _db.RepRequests.AsNoTracking()
                     .Where(r => r.RepEmployeeId == employeeId && r.Kind == RepRequestKind.CustodyExpense && r.Status == RepRequestStatus.Failed && r.ReceivedAt >= since).ToListAsync())
            notices.Add(new SnapshotNotice($"custfail:{r.Id}", "reject", "تعذّر تسجيل مصروف من العهدة", r.ErrorMessage, r.ReceivedAt));
        return notices;
    }

    private static string KindText(RepRequestKind k) => k switch
    {
        RepRequestKind.Expense => "مصروف", RepRequestKind.Return => "مرتجع", RepRequestKind.Free => "مجاني", RepRequestKind.NewCustomer => "زبون جديد",
        RepRequestKind.CustomerLink => "طلب ربط زبون", RepRequestKind.Collection => "تحصيل", RepRequestKind.CreditSale => "بيع آجل", _ => "بيع"
    };
}
