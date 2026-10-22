using System.Text.Json;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>سطر في شاشة «طلبات الصرف».</summary>
public class PaymentRequestRow
{
    public int Id { get; init; }
    public string RequestNumber { get; init; } = "";
    public PaymentRequestKind Kind { get; init; }
    public PaymentRequestStatus Status { get; init; }
    public decimal Amount { get; init; }
    public string Summary { get; init; } = "";
    public string RequestedBy { get; init; } = "";
    public DateTime RequestedAt { get; init; }
    public string? DecidedBy { get; init; }
    public DateTime? DecidedAt { get; init; }
    public string? RejectReason { get; init; }
    public string? ResultTable { get; init; }
    public int? ResultId { get; init; }
    public string KindText => PaymentApprovalService.KindText(Kind);
    public string StatusText => Status switch { PaymentRequestStatus.Pending => "بانتظار اعتماد المالية", PaymentRequestStatus.Approved => "معتمد ومصروف", _ => "مرفوض" };
}

/// <summary>
/// موافقة القسم المالي على الصرف: سلفة أو مسحوب موظف، وصرف رواتب شهر معتمد، وسحب أرباح شريك. كل المبالغ تحتاج موافقة.
/// الطلب يُسجَّل بلا أي أثر مالي، ويصل إشعارًا لمن يملك «اعتماد الصرف»؛ وعند الاعتماد يُنفَّذ الصرف نفسه (قيد وحركة صندوق)
/// بالبيانات المحفوظة. مقدّم الطلب لا يعتمد طلبه إلا إن كان أدمن أو لم يوجد مسؤول مالي آخر (منشأة بمسؤول واحد).
/// </summary>
public class PaymentApprovalService
{
    public const string PayrollPayoutRule = "PayrollPayout";   // مدين رواتب مستحقة الدفع / دائن الصندوق
    private static readonly JsonSerializerOptions Json = new();
    private readonly ProjectDbContext _db;
    public PaymentApprovalService(ProjectDbContext db) => _db = db;

    public static string KindText(PaymentRequestKind k) => k switch
    {
        PaymentRequestKind.EmployeeAdvance => "سلفة موظف",
        PaymentRequestKind.EmployeeWithdrawal => "مسحوب موظف",
        PaymentRequestKind.PayrollPayout => "صرف رواتب",
        _ => "سحب أرباح شريك"
    };

    private record PartnerPayload(int PartnerId, decimal Amount, DateTime Date, string? Notes);
    private record PayrollPayload(int RunId);

    // ============================ تقديم الطلب ============================

    /// <summary>
    /// سلفة أو مسحوب لموظف: يُتحقق منه كاملًا الآن بتجربة الصرف داخل معاملة تُلغى (فلا يبقى أثر)، ثم يُحفظ طلبًا.
    /// </summary>
    public async Task<(FinanceOperationResult result, PaymentRequest? request)> RequestEmployeePayoutAsync(EmployeeDeductionService.CreateRequest r, int userId)
    {
        if (r.Kind == EmployeeDeductionKind.Penalty || r.IsOpening) return (FinanceOperationResult.Fail("العقوبة والرصيد الافتتاحي لا صرف نقدي فيهما — لا يحتاجان موافقة"), null);
        string name;
        await using (var tx = await _db.Database.BeginTransactionAsync())
        {
            var (check, _) = await new EmployeeDeductionService(_db).CreateAsync(r, userId);
            await tx.RollbackAsync();
            _db.ChangeTracker.Clear();
            if (!check.Success) return (check, null);
            name = await _db.Employees.AsNoTracking().Where(e => e.Id == r.EmployeeId).Select(e => e.FullName).FirstAsync();
        }
        var kind = r.Kind == EmployeeDeductionKind.Loan ? PaymentRequestKind.EmployeeAdvance : PaymentRequestKind.EmployeeWithdrawal;
        var summary = $"{KindText(kind)} لـ {name} — {r.Amount:N0} د.ع" + (r.MonthlyInstallment is decimal m ? $" (قسط {m:N0})" : "")
                      + (string.IsNullOrWhiteSpace(r.Reason) ? "" : $" — {r.Reason.Trim()}");
        return await SaveAsync(kind, r.Amount, summary, JsonSerializer.Serialize(r, Json), r.CashBoxId, userId);
    }

    /// <summary>صرف رواتب شهر معتمد من صندوق (بعد اعتماد المسير): يتحقق من المسير والرصيد ثم يُحفظ طلبًا.</summary>
    public async Task<(FinanceOperationResult result, PaymentRequest? request)> RequestPayrollPayoutAsync(int runId, int cashBoxId, int userId)
    {
        var (error, run, amount) = await CheckPayoutAsync(runId, cashBoxId);
        if (error is not null) return (FinanceOperationResult.Fail(error), null);
        if (await _db.PaymentRequests.AnyAsync(p => p.Kind == PaymentRequestKind.PayrollPayout && p.Status == PaymentRequestStatus.Pending && p.Payload == JsonSerializer.Serialize(new PayrollPayload(runId), Json)))
            return (FinanceOperationResult.Fail("لهذه الرواتب طلب صرف بانتظار اعتماد المالية أصلًا"), null);
        var summary = $"صرف رواتب {run!.PeriodMonth:00}/{run.PeriodYear} — {run.Lines.Count} موظف — {amount:N0} د.ع";
        return await SaveAsync(PaymentRequestKind.PayrollPayout, amount, summary, JsonSerializer.Serialize(new PayrollPayload(runId), Json), cashBoxId, userId);
    }

    /// <summary>سحب أرباح شريك (للأدمن): يتحقق من الرصيد والصندوق ثم يُحفظ طلبًا.</summary>
    public async Task<(FinanceOperationResult result, PaymentRequest? request)> RequestPartnerWithdrawalAsync(int partnerId, decimal amount, DateTime date, string? notes, int? cashBoxId, int userId)
    {
        var cash = new CashBoxService(_db);
        if (!await cash.IsAdminAsync(userId)) return (FinanceOperationResult.Fail("سحب أرباح الشركاء للأدمن فقط"), null);
        if (amount <= 0) return (FinanceOperationResult.Fail("المبلغ يجب أن يكون أكبر من صفر"), null);
        var partner = await _db.Partners.AsNoTracking().FirstOrDefaultAsync(p => p.Id == partnerId);
        if (partner is null) return (FinanceOperationResult.Fail("اختر الشريك"), null);
        var balance = await new ReconciliationService(_db).GetPartnerBalanceAsync(partnerId);
        if (amount > balance) return (FinanceOperationResult.Fail($"رصيد أرباح {partner.Name} {balance:N0} د.ع فقط"), null);
        if (cashBoxId is int box && await cash.GetBalanceAsync(box) < amount) return (FinanceOperationResult.Fail("رصيد الصندوق المختار غير كافٍ"), null);
        var summary = $"سحب أرباح {partner.Name} — {amount:N0} د.ع" + (string.IsNullOrWhiteSpace(notes) ? "" : $" ({notes.Trim()})");
        return await SaveAsync(PaymentRequestKind.PartnerWithdrawal, amount, summary, JsonSerializer.Serialize(new PartnerPayload(partnerId, amount, date, notes), Json), cashBoxId, userId);
    }

    private async Task<(FinanceOperationResult, PaymentRequest?)> SaveAsync(PaymentRequestKind kind, decimal amount, string summary, string payload, int? boxId, int userId)
    {
        var year = DateTime.Today.Year;
        var count = await _db.PaymentRequests.CountAsync(p => p.RequestedAt.Year == year);
        var request = new PaymentRequest
        {
            RequestNumber = $"PQ-{year}-{count + 1:D5}", Kind = kind, Amount = amount, Summary = summary.Length > 300 ? summary[..300] : summary,
            Payload = payload, CashBoxId = boxId, RequestedByUserId = userId
        };
        _db.PaymentRequests.Add(request);
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Insert", "PaymentRequests", request.Id, $"طلب صرف {request.RequestNumber}: {request.Summary}");
        return (FinanceOperationResult.Ok(), request);
    }

    // ============================ الاعتماد والرفض ============================

    /// <summary>هل يوجد مسؤول مالي آخر غير <paramref name="userId"/> يملك «اعتماد الصرف»؟</summary>
    private Task<bool> AnotherApproverExistsAsync(int userId) =>
        _db.Users.AnyAsync(u => u.Id != userId && u.IsActive && u.Role.Permissions.Any(p => p.ModuleCode == SpecialPermission.PaymentApproval && p.CanView));

    public async Task<FinanceOperationResult> ApproveAsync(int requestId, int approverId)
    {
        if (!await SpecialPermission.HasAsync(_db, approverId, SpecialPermission.PaymentApproval))
            return FinanceOperationResult.Fail($"الاعتماد يحتاج صلاحية «{SpecialPermission.NameOf(SpecialPermission.PaymentApproval)}»");
        await using var tx = await _db.Database.BeginTransactionAsync();
        var r = await _db.PaymentRequests.FirstOrDefaultAsync(x => x.Id == requestId);
        if (r is null || r.Status != PaymentRequestStatus.Pending) return FinanceOperationResult.Fail("الطلب ليس بانتظار الاعتماد");
        // فصل المهام: لا يعتمد مقدّم الطلب طلبه إلا الأدمن (مالك المنشأة) أو إن لم يوجد مسؤول مالي آخر
        if (r.RequestedByUserId == approverId && !await new CashBoxService(_db).IsAdminAsync(approverId) && await AnotherApproverExistsAsync(approverId))
            return FinanceOperationResult.Fail("لا يعتمد مقدّم الطلب طلبه بنفسه — يعتمده مسؤول مالي آخر");

        // التنفيذ بصفة مقدّم الطلب (صندوقه وصلاحياته)، والمعتمد يُسجَّل في الطلب والتدقيق
        var doer = r.RequestedByUserId;
        string? table; int? resultId; FinanceOperationResult done;
        switch (r.Kind)
        {
            case PaymentRequestKind.EmployeeAdvance or PaymentRequestKind.EmployeeWithdrawal:
            {
                var req = JsonSerializer.Deserialize<EmployeeDeductionService.CreateRequest>(r.Payload, Json)!;
                var (res, d) = await new EmployeeDeductionService(_db).CreateAsync(req, doer);
                (done, table, resultId) = (res, "EmployeeDeductions", d?.Id);
                break;
            }
            case PaymentRequestKind.PayrollPayout:
            {
                var p = JsonSerializer.Deserialize<PayrollPayload>(r.Payload, Json)!;
                done = await PayRunAsync(p.RunId, r.CashBoxId ?? 0, doer);
                (table, resultId) = ("PayrollRuns", p.RunId);
                break;
            }
            default:
            {
                var p = JsonSerializer.Deserialize<PartnerPayload>(r.Payload, Json)!;
                var (res, t) = await new ReconciliationService(_db).WithdrawAsync(p.PartnerId, p.Amount, p.Date, p.Notes, doer, r.CashBoxId);
                (done, table, resultId) = (res, "PartnerTransactions", t?.Id);
                break;
            }
        }
        if (!done.Success) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return done; }
        r.Status = PaymentRequestStatus.Approved;
        (r.DecidedByUserId, r.DecidedAt, r.ResultTable, r.ResultId) = (approverId, DateTime.UtcNow, table, resultId);
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(approverId, "Post", "PaymentRequests", r.Id, $"اعتماد {r.RequestNumber} وصرفه: {r.Summary}");
        await tx.CommitAsync();
        return FinanceOperationResult.Ok();
    }

    public async Task<FinanceOperationResult> RejectAsync(int requestId, string reason, int approverId)
    {
        if (!await SpecialPermission.HasAsync(_db, approverId, SpecialPermission.PaymentApproval))
            return FinanceOperationResult.Fail($"الرفض يحتاج صلاحية «{SpecialPermission.NameOf(SpecialPermission.PaymentApproval)}»");
        if (string.IsNullOrWhiteSpace(reason)) return FinanceOperationResult.Fail("اكتب سبب الرفض ليصل لمقدّم الطلب");
        var r = await _db.PaymentRequests.FirstOrDefaultAsync(x => x.Id == requestId);
        if (r is null || r.Status != PaymentRequestStatus.Pending) return FinanceOperationResult.Fail("الطلب ليس بانتظار الاعتماد");
        (r.Status, r.RejectReason, r.DecidedByUserId, r.DecidedAt) = (PaymentRequestStatus.Rejected, reason.Trim(), approverId, DateTime.UtcNow);
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(approverId, "Edit", "PaymentRequests", r.Id, $"رفض {r.RequestNumber}: {r.Summary} — {r.RejectReason}");
        return FinanceOperationResult.Ok();
    }

    /// <summary>مقدّم الطلب يسحب طلبه قبل البتّ فيه (يُعدّ مرفوضًا بسبب السحب).</summary>
    public async Task<FinanceOperationResult> WithdrawRequestAsync(int requestId, int userId)
    {
        var r = await _db.PaymentRequests.FirstOrDefaultAsync(x => x.Id == requestId);
        if (r is null || r.Status != PaymentRequestStatus.Pending) return FinanceOperationResult.Fail("الطلب ليس بانتظار الاعتماد");
        if (r.RequestedByUserId != userId) return FinanceOperationResult.Fail("لا يسحب الطلب إلا من قدّمه");
        (r.Status, r.RejectReason, r.DecidedByUserId, r.DecidedAt) = (PaymentRequestStatus.Rejected, "سحبه مقدّمه", userId, DateTime.UtcNow);
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Edit", "PaymentRequests", r.Id, $"سحب طلب {r.RequestNumber}");
        return FinanceOperationResult.Ok();
    }

    // ============================ الاستعلام ============================

    public Task<int> PendingCountAsync() => _db.PaymentRequests.CountAsync(p => p.Status == PaymentRequestStatus.Pending);

    public async Task<List<PaymentRequestRow>> ListAsync(bool pendingOnly, int take = 300) =>
        await _db.PaymentRequests.AsNoTracking().Where(p => !pendingOnly || p.Status == PaymentRequestStatus.Pending)
            .OrderBy(p => p.Status == PaymentRequestStatus.Pending ? 0 : 1).ThenByDescending(p => p.Id).Take(take)
            .Select(p => new PaymentRequestRow
            {
                Id = p.Id, RequestNumber = p.RequestNumber, Kind = p.Kind, Status = p.Status, Amount = p.Amount, Summary = p.Summary,
                RequestedBy = p.RequestedByUser.Username, RequestedAt = p.RequestedAt,
                DecidedBy = p.DecidedByUser != null ? p.DecidedByUser.Username : null, DecidedAt = p.DecidedAt, RejectReason = p.RejectReason,
                ResultTable = p.ResultTable, ResultId = p.ResultId
            }).ToListAsync();

    // ============================ صرف الرواتب ============================

    /// <summary>فحص صرف مسير: معتمد، لم يُصرف، والصندوق فعّال ويكفي رصيده. يعيد المبلغ بالدينار (يشمل رواتب الدولار بسعر القيد).</summary>
    private async Task<(string? error, PayrollRun? run, decimal amount)> CheckPayoutAsync(int runId, int cashBoxId)
    {
        var run = await _db.PayrollRuns.AsNoTracking().Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == runId);
        if (run is null) return ("مسير الرواتب غير موجود", null, 0);
        if (run.Status != PayrollRunStatus.Approved) return ("اعتمد الرواتب أولًا — الصرف بعد اعتماد المسير", run, 0);
        if (run.PaidAt is not null) return ("هذه الرواتب مصروفة مسبقًا", run, 0);
        var summary = await new HrService(_db).SummarizeAsync(runId);
        var amount = summary.TotalInIqd;
        if (amount <= 0) return ("صافي الرواتب صفر — لا شيء يُصرف", run, 0);
        if (!await _db.CashBoxes.AnyAsync(b => b.Id == cashBoxId && b.IsActive)) return ("اختر صندوقًا فعّالًا للصرف منه", run, amount);
        var balance = await new CashBoxService(_db).GetBalanceAsync(cashBoxId);
        if (balance < amount) return ($"رصيد الصندوق غير كافٍ: المتاح {balance:N0} والمطلوب {amount:N0} د.ع", run, amount);
        return (null, run, amount);
    }

    /// <summary>صرف الرواتب المعتمدة: قيد (مدين رواتب مستحقة الدفع / دائن الصندوق) وحركة صندوق، وختم المسير «مصروف».</summary>
    private async Task<FinanceOperationResult> PayRunAsync(int runId, int cashBoxId, int userId)
    {
        var (error, run, amount) = await CheckPayoutAsync(runId, cashBoxId);
        if (error is not null) return FinanceOperationResult.Fail(error);
        var tracked = await _db.PayrollRuns.FirstAsync(r => r.Id == runId);
        var text = $"صرف رواتب {run!.PeriodMonth:00}/{run.PeriodYear} — {run.Lines.Count} موظف";
        var (entry, journalError) = await LedgerHelper.PostJournalAsync(_db, PayrollPayoutRule, amount, DateTime.Today, JournalEntryType.AutoVoucher, text, userId, "PayrollRuns", runId, "PP");
        if (journalError is not null) return FinanceOperationResult.Fail(journalError);
        await _db.SaveChangesAsync();
        await new CashBoxService(_db).RecordAutoAsync(userId, CashBoxTxType.PayrollPayout, -amount, DateTime.Today, "PayrollRuns", runId, null, text, entry!.Id, cashBoxId);
        (tracked.PaidAt, tracked.PaidByUserId, tracked.PayoutJournalEntryId) = (DateTime.UtcNow, userId, entry.Id);
        await _db.SaveChangesAsync();
        return FinanceOperationResult.Ok();
    }
}
