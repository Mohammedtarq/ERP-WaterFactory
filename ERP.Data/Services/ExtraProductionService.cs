using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>سطر طلب إنتاج إضافي للعرض (قائمة أمين المخزن وسجل الأمر).</summary>
public record ExtraRequestRow(int Id, int OrderId, string OrderNumber, string ItemName, string? RecipeName, string LevelName, decimal Packs, decimal Pieces,
                              string Reason, string RequestedBy, DateTime RequestedAt, ExtraProductionStatus Status, decimal? ApprovedPacks,
                              string? DecidedBy, DateTime? DecidedAt, string? DecisionNote, string? BatchNumber)
{
    public string StatusText => Status switch
    {
        ExtraProductionStatus.Pending => "بانتظار الموافقة", ExtraProductionStatus.Approved => "موافق", ExtraProductionStatus.Partial => "موافقة جزئية", _ => "مرفوض"
    };
    public string PacksText => $"{Packs:N0} {LevelName}";
    public string VariantText => RecipeName ?? "الأساسي";
}

/// <summary>
/// الإنتاج الإضافي على أمر/دفعة: يُطلب من أوامر الإنتاج (فحص المواد أولًا، وتُسمّى المادة الناقصة)،
/// ثم يوافق أمين المخزن (كاملًا أو جزئيًا) أو يرفض بسبب. الموافقة تصرف المواد وتُدخل الناتج على تشغيلة الأمر نفسها.
/// </summary>
public class ExtraProductionService
{
    private readonly ProjectDbContext _db;
    public ExtraProductionService(ProjectDbContext db) => _db = db;

    public async Task<(FinanceOperationResult result, int? requestId)> RequestAsync(int lineId, int levelId, decimal packs, string? reason, int userId)
    {
        if (string.IsNullOrWhiteSpace(reason)) return (FinanceOperationResult.Fail("اكتب سبب الزيادة"), null);
        if (packs <= 0) return (FinanceOperationResult.Fail("عدد العبوات يجب أن يكون أكبر من صفر"), null);
        var line = await _db.ProductionOrderLines.AsNoTracking().Include(l => l.ProductionOrder).FirstOrDefaultAsync(l => l.Id == lineId);
        if (line is null) return (FinanceOperationResult.Fail("سطر الأمر غير موجود"), null);
        if (line.ProductionOrder.Status is ProductionOrderStatus.Draft or ProductionOrderStatus.Cancelled)
            return (FinanceOperationResult.Fail("الإنتاج الإضافي يكون لأمر بدأ تشغيله أو اكتمل"), null);
        if (line.OutputBatchId is null) return (FinanceOperationResult.Fail("لا تشغيلة لهذا السطر"), null);
        var level = await _db.ItemPackagingLevels.AsNoTracking().FirstOrDefaultAsync(l => l.Id == levelId && l.ItemId == line.FinishedItemId);
        if (level is null) return (FinanceOperationResult.Fail("وحدة التعبئة لا تخص صنف السطر"), null);

        // المواد تُفحص عند الطلب فتُسمّى الناقصة قبل أن يصل الطلب إلى المخزن
        var (error, needs) = await new DailyProductionService(_db).PreviewExtraAsync(lineId, levelId, packs);
        if (error is not null) return (FinanceOperationResult.Fail(error), null);
        var short_ = needs.Where(n => n.IsShort).Select(n => $"«{n.ItemName}»: المطلوب {n.Required:N0}، المتاح {n.Available:N0}، الناقص {n.Short:N0}").ToList();
        if (short_.Count > 0)
            return (FinanceOperationResult.Fail("لا يمكن إرسال الطلب — مادة أولية ناقصة:\n" + string.Join("\n", short_)), null);

        var req = new ExtraProductionRequest
        {
            ProductionOrderId = line.ProductionOrderId, ProductionOrderLineId = lineId, PackagingLevelId = levelId, Packs = packs,
            Pieces = Math.Round(packs * level.EquivalentBaseUnits, 3), Reason = reason.Trim(), RequestedByUserId = userId
        };
        _db.ExtraProductionRequests.Add(req);
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Add", "ExtraProductionRequests", req.Id, $"طلب إنتاج إضافي {packs:N0} {level.LevelName} على {line.ProductionOrder.MONumber}: {req.Reason}");
        return (FinanceOperationResult.Ok(), req.Id);
    }

    /// <summary>موافقة أمين المخزن: كاملة، أو جزئية بعدد عبوات أقل. يصرف المواد ويُدخل الناتج على تشغيلة الأمر.</summary>
    public async Task<FinanceOperationResult> ApproveAsync(int requestId, decimal? approvedPacks, int userId)
    {
        var req = await _db.ExtraProductionRequests.Include(r => r.ProductionOrder).FirstOrDefaultAsync(r => r.Id == requestId);
        if (req is null) return FinanceOperationResult.Fail("الطلب غير موجود");
        if (req.Status != ExtraProductionStatus.Pending) return FinanceOperationResult.Fail("تم البتّ في هذا الطلب سابقًا");
        var packs = approvedPacks ?? req.Packs;
        if (packs <= 0 || packs > req.Packs) return FinanceOperationResult.Fail($"المعتمد يجب أن يكون بين الصفر والمطلوب ({req.Packs:N0})");

        await using var tx = await _db.Database.BeginTransactionAsync();
        var (r, _, _) = await new DailyProductionService(_db).PostExtraAsync(req.ProductionOrderLineId, req.PackagingLevelId, packs, userId);
        if (!r.Success) { await tx.RollbackAsync(); return r; }
        req = await _db.ExtraProductionRequests.FirstAsync(x => x.Id == requestId);
        req.ApprovedPacks = packs;
        req.Status = packs < req.Packs ? ExtraProductionStatus.Partial : ExtraProductionStatus.Approved;
        req.DecidedByUserId = userId;
        req.DecidedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Approve", "ExtraProductionRequests", req.Id, $"اعتماد إنتاج إضافي: {packs:N0} من {req.Packs:N0} عبوة");
        await tx.CommitAsync();
        return FinanceOperationResult.Ok();
    }

    public async Task<FinanceOperationResult> RejectAsync(int requestId, string? note, int userId)
    {
        if (string.IsNullOrWhiteSpace(note)) return FinanceOperationResult.Fail("اكتب سبب الرفض");
        var req = await _db.ExtraProductionRequests.FirstOrDefaultAsync(r => r.Id == requestId);
        if (req is null) return FinanceOperationResult.Fail("الطلب غير موجود");
        if (req.Status != ExtraProductionStatus.Pending) return FinanceOperationResult.Fail("تم البتّ في هذا الطلب سابقًا");
        req.Status = ExtraProductionStatus.Rejected;
        req.DecisionNote = note.Trim();
        req.DecidedByUserId = userId;
        req.DecidedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Reject", "ExtraProductionRequests", req.Id, $"رفض إنتاج إضافي: {req.DecisionNote}");
        return FinanceOperationResult.Ok();
    }

    private IQueryable<ExtraRequestRow> Rows(IQueryable<ExtraProductionRequest> q) =>
        q.OrderByDescending(r => r.Id).Select(r => new ExtraRequestRow(
            r.Id, r.ProductionOrderId, r.ProductionOrder.MONumber, r.ProductionOrderLine.FinishedItem.ItemName,
            r.ProductionOrderLine.CustomRecipe != null ? r.ProductionOrderLine.CustomRecipe.Name : null,
            r.PackagingLevel.LevelName, r.Packs, r.Pieces, r.Reason, r.RequestedByUser.Username, r.RequestedAt, r.Status, r.ApprovedPacks,
            r.DecidedByUser != null ? r.DecidedByUser.Username : null, r.DecidedAt, r.DecisionNote,
            r.ProductionOrderLine.OutputBatch != null ? r.ProductionOrderLine.OutputBatch.BatchNumber : null));

    /// <summary>الطلبات بانتظار موافقة أمين المخزن.</summary>
    public Task<List<ExtraRequestRow>> PendingAsync() =>
        Rows(_db.ExtraProductionRequests.AsNoTracking().Where(r => r.Status == ExtraProductionStatus.Pending)).ToListAsync();

    /// <summary>سجل الزيادات على أمر.</summary>
    public Task<List<ExtraRequestRow>> ForOrderAsync(int orderId) =>
        Rows(_db.ExtraProductionRequests.AsNoTracking().Where(r => r.ProductionOrderId == orderId)).ToListAsync();

    /// <summary>الكمية الإضافية المعتمدة بالقطع لكل سطر في الأمر (عمود «إضافي»).</summary>
    public async Task<Dictionary<int, decimal>> ApprovedPiecesByLineAsync(int orderId) =>
        (await _db.ExtraProductionRequests.AsNoTracking()
            .Where(r => r.ProductionOrderId == orderId && (r.Status == ExtraProductionStatus.Approved || r.Status == ExtraProductionStatus.Partial))
            .Select(r => new { r.ProductionOrderLineId, Pieces = (r.ApprovedPacks ?? r.Packs) * r.PackagingLevel.EquivalentBaseUnits }).ToListAsync())
        .GroupBy(x => x.ProductionOrderLineId).ToDictionary(g => g.Key, g => g.Sum(x => x.Pieces));
}
