using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>
/// صلاحيات خاصة على المعلومة أو الإجراء، مستقلة عن صلاحيات الوحدة (عرض/إضافة/تعديل/حذف/ترحيل).
/// تُحفظ في نفس جدول الصلاحيات بكود يبدأ بـ "Special:"، والمنح = CanView.
/// </summary>
public static class SpecialPermission
{
    public const string Prefix = "Special:";
    public const string CostAndProfit = "Special:CostProfit";
    public const string FinalAccounts = "Special:FinalAccounts";
    public const string PeriodClose = "Special:PeriodClose";
    public const string VoidPosted = "Special:VoidPosted";
    public const string ApproveAboveLimit = "Special:ApproveAboveLimit";
    public const string AllCashBoxes = "Special:AllCashBoxes";
    public const string AuditLog = "Special:AuditLog";
    public const string SellPendingProduction = "Special:SellPending";
    public const string SellRawMaterials = "Special:SellRaw";
    public const string CreditLimitOverride = "Special:CreditOverride";

    public static readonly (string Code, string Name, string Hint)[] All =
    {
        (CostAndProfit, "رؤية الكلفة والهامش والأرباح", "أسعار الكلفة، وهامش كل منتج، ومحاكاة الكلفة"),
        (FinalAccounts, "الحسابات الختامية والمطابقة والشركاء", "تقرير الحسابات الختامية، والمطابقة الحسابية، وحصص الشركاء ومسحوباتهم"),
        (PeriodClose, "إغلاق الشهر وفتحه", "قفل الفترة المحاسبية، وفتحها للتعديل مع كتابة السبب"),
        (VoidPosted, "إلغاء المستندات المرحّلة", "إلغاء فاتورة أو سند مرحّل بقيد عكسي"),
        (ApproveAboveLimit, "الصرف فوق سقف الدور", "تنفيذ سند صرف أو سحب من صندوق يتجاوز سقف الدور"),
        (AllCashBoxes, "رؤية كل الصناديق", "بدونها يرى المستخدم صندوقه فقط"),
        (AuditLog, "سجل الحركات", "عرض من أضاف أو عدّل أو ألغى ماذا ومتى"),
        (SellPendingProduction, "البيع بانتظار الإنتاج", "بيع كمية أكبر من الرصيد المسجّل، تُسوّى عند تسجيل الإنتاج"),
        (SellRawMaterials, "بيع المواد الأولية", "اختيار مخزن المواد الأولية في فاتورة البيع"),
        (CreditLimitOverride, "تجاوز حد دين العميل", "الموافقة على بيع آجل لعميل تجاوز حد دينه"),
    };

    public static string NameOf(string code) => All.FirstOrDefault(p => p.Code == code).Name ?? code;

    public static Task<bool> HasAsync(ProjectDbContext db, int userId, string code) =>
        db.Users.AnyAsync(u => u.Id == userId && u.IsActive &&
                               u.Role.Permissions.Any(p => p.ModuleCode == code && p.CanView));
}

/// <summary>تسجيل صريح في سجل الحركات للعمليات التي لا تمر عبر تتبّع EF (إجراءات مخزّنة، دخول...).</summary>
public class AuditService
{
    private readonly ProjectDbContext _db;
    public AuditService(ProjectDbContext db) => _db = db;

    public async Task LogAsync(int? userId, string action, string table, object? recordId, string? summary, string? changes = null)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            UserId = userId, Action = action, TableName = table, RecordId = recordId?.ToString(),
            Summary = summary is { Length: > 400 } ? summary[..400] : summary, Changes = changes,
        });
        await _db.SaveChangesAsync();
    }

    public record AuditRow(long Id, DateTime AtLocal, string User, string Action, string ActionName, string TableName, string TableTitle,
                           string? RecordId, string? Summary, string? Changes);

    public async Task<List<AuditRow>> QueryAsync(DateTime from, DateTime to, int? userId = null, string? table = null, string? text = null, int take = 2000)
    {
        var fromUtc = from.Date.ToUniversalTime();
        var toUtc = to.Date.AddDays(1).ToUniversalTime();
        var q = _db.AuditLogs.AsNoTracking().Where(a => a.AtUtc >= fromUtc && a.AtUtc < toUtc);
        if (userId is int u) q = q.Where(a => a.UserId == u);
        if (!string.IsNullOrWhiteSpace(table)) q = q.Where(a => a.TableName == table);
        if (!string.IsNullOrWhiteSpace(text))
        {
            var t = text.Trim();
            q = q.Where(a => (a.Summary != null && a.Summary.Contains(t)) || (a.RecordId != null && a.RecordId == t) || (a.Changes != null && a.Changes.Contains(t)));
        }
        var rows = await q.OrderByDescending(a => a.Id).Take(take).ToListAsync();
        var names = await _db.Users.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Username);
        return rows.Select(a => new AuditRow(a.Id, DateTime.SpecifyKind(a.AtUtc, DateTimeKind.Utc).ToLocalTime(),
                                             a.UserId is int id && names.TryGetValue(id, out var n) ? n : "النظام",
                                             a.Action, ActionName(a.Action), a.TableName, TableTitle(a.TableName), a.RecordId, a.Summary, a.Changes))
                   .ToList();
    }

    public static string ActionName(string action) => action switch
    {
        "Insert" => "إضافة", "Update" => "تعديل", "Delete" => "حذف", "Void" => "إلغاء", "Post" => "ترحيل",
        "Close" => "إغلاق فترة", "Reopen" => "فتح فترة", "Login" => "دخول", "LoginFailed" => "دخول فاشل",
        _ => action,
    };

    public static string TableTitle(string table) => table switch
    {
        "SalesInvoices" => "فواتير المبيعات", "Vouchers" => "السندات", "JournalEntries" => "القيود",
        "CashBoxTransactions" => "حركات الصناديق", "CashBoxes" => "الصناديق", "Customers" => "العملاء",
        "Suppliers" => "الموردون", "Items" => "الأصناف", "Employees" => "الموظفون", "Users" => "المستخدمون",
        "Roles" => "الأدوار", "RolePermissions" => "الصلاحيات", "PeriodLocks" => "إغلاق الفترات",
        "StockDocuments" => "المستندات المخزنية", "GoodsReceipts" => "الاستلام", "PurchaseOrders" => "أوامر الشراء",
        "ProductionOrders" => "أوامر الإنتاج", "PayrollRuns" => "مسيرات الرواتب", "AccountMappingRules" => "قواعد العقل المالي",
        "ChartOfAccounts" => "دليل الحسابات", "Warehouses" => "المخازن", "CustomerDeposits" => "التأمينات",
        "EmployeeDeductions" => "السلف والاستقطاعات", "Partners" => "الشركاء", "Reconciliations" => "المطابقات",
        _ => table,
    };
}

/// <summary>
/// قفل الفترات المحاسبية: إغلاق الشهر يمنع أي إضافة أو تعديل أو إلغاء بتاريخ داخله (مشغّلات 27_controls.sql)،
/// والفتح للمدير فقط مع سبب مكتوب. كل إغلاق وفتح يبقى في السجل.
/// </summary>
public class PeriodLockService
{
    private readonly ProjectDbContext _db;
    public PeriodLockService(ProjectDbContext db) => _db = db;

    public async Task<DateTime?> GetLockedThroughAsync() =>
        await _db.PeriodLocks.AsNoTracking().OrderByDescending(p => p.Id).Select(p => p.LockedThrough).FirstOrDefaultAsync();

    public record LockHistoryRow(DateTime AtLocal, string Action, DateTime? LockedThrough, string? Reason, string User);

    public async Task<List<LockHistoryRow>> HistoryAsync() =>
        (await _db.PeriodLocks.AsNoTracking().OrderByDescending(p => p.Id)
            .Select(p => new { p.AtUtc, p.Action, p.LockedThrough, p.Reason, p.User.Username }).Take(200).ToListAsync())
        .Select(p => new LockHistoryRow(DateTime.SpecifyKind(p.AtUtc, DateTimeKind.Utc).ToLocalTime(),
                                        p.Action == "Close" ? "إغلاق" : "فتح", p.LockedThrough, p.Reason, p.Username))
        .ToList();

    /// <summary>يقفل كل الأيام حتى آخر الشهر المحدد (ضمنًا).</summary>
    public async Task<FinanceOperationResult> CloseMonthAsync(int year, int month, int userId)
    {
        if (!await SpecialPermission.HasAsync(_db, userId, SpecialPermission.PeriodClose))
            return FinanceOperationResult.Fail("لا تملك صلاحية إغلاق الشهر");
        var through = new DateTime(year, month, 1).AddMonths(1).AddDays(-1);
        if (through >= DateTime.Today)
            return FinanceOperationResult.Fail("لا يُغلق الشهر قبل انتهائه");
        var current = await GetLockedThroughAsync();
        if (current is DateTime c && c >= through)
            return FinanceOperationResult.Fail($"الفترة مقفلة أصلًا حتى {c:yyyy-MM-dd}");
        _db.PeriodLocks.Add(new PeriodLock { LockedThrough = through, Action = "Close", UserId = userId,
                                             Reason = $"إغلاق شهر {month}/{year}" });
        await _db.SaveChangesAsync();
        return FinanceOperationResult.Ok();
    }

    /// <summary>يفتح الشهر المحدد وما بعده للتعديل: يصبح القفل حتى نهاية الشهر السابق له.</summary>
    public async Task<FinanceOperationResult> ReopenFromMonthAsync(int year, int month, string reason, int userId)
    {
        if (!await SpecialPermission.HasAsync(_db, userId, SpecialPermission.PeriodClose))
            return FinanceOperationResult.Fail("لا تملك صلاحية فتح الشهر المقفل");
        if (string.IsNullOrWhiteSpace(reason)) return FinanceOperationResult.Fail("اكتب سبب فتح الشهر");
        var current = await GetLockedThroughAsync();
        var start = new DateTime(year, month, 1);
        if (current is null || current < start) return FinanceOperationResult.Fail("هذا الشهر غير مقفل");
        DateTime? newLock = start.AddDays(-1);
        _db.PeriodLocks.Add(new PeriodLock { LockedThrough = newLock, Action = "Reopen", UserId = userId, Reason = reason.Trim() });
        await _db.SaveChangesAsync();
        return FinanceOperationResult.Ok();
    }
}

/// <summary>سقف الصرف للدور: أي صرف أكبر منه يحتاج صلاحية "الصرف فوق سقف الدور".</summary>
public static class ApprovalLimits
{
    public static async Task<string?> CheckPaymentAsync(ProjectDbContext db, int userId, decimal amount)
    {
        var limit = await db.Users.Where(u => u.Id == userId).Select(u => u.Role.MaxPaymentAmount).FirstOrDefaultAsync();
        if (limit is not decimal max || amount <= max) return null;
        if (await SpecialPermission.HasAsync(db, userId, SpecialPermission.ApproveAboveLimit)) return null;
        return $"المبلغ {amount:N0} يتجاوز سقف الصرف لدورك ({max:N0}). يحتاج تنفيذه من المدير أو من يملك صلاحية الاعتماد.";
    }
}
