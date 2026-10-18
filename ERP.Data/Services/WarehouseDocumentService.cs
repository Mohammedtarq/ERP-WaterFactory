using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>سطر مستند كما يُدخل في واجهة المخزن (بوحدة التعبئة).</summary>
public record StockDocumentLineInput(int ItemId, int PackagingLevelId, decimal QuantityInLevel,
                                     int? BatchId = null, string? NewBatchNumber = null, DateTime? NewBatchExpiry = null,
                                     string? Notes = null, bool IsDamaged = false, int? CustomRecipeId = null);

public record StockDocumentRequest(
    StockDocumentType Type, int WarehouseId, DateTime Date, IReadOnlyList<StockDocumentLineInput> Lines, int UserId,
    int? CounterWarehouseId = null, string? PartyName = null, DamageReason? DamageReason = null, string? Notes = null,
    bool MoveDamagedToDamagedWarehouse = true, BeneficiaryCategory? Category = null, DamageCause? DamageCause = null);

/// <summary>ملخص حركة صنف في مخزن خلال فترة (تقرير "الرصيد والمتبقي").</summary>
public class StockSummaryRow
{
    public int ItemId { get; init; }
    public string ItemCode { get; init; } = "";
    public string ItemName { get; init; } = "";
    public decimal Opening { get; set; }
    public decimal In { get; set; }
    public decimal Out { get; set; }
    public decimal Damaged { get; set; }
    public decimal Free { get; set; }
    public decimal Closing => Opening + In - Out - Damaged - Free;
    // نفس الأرقام بعبوات الصنف (كارتون/شرنك) للعرض
    public string OpeningText { get; set; } = "";
    public string InText { get; set; } = "";
    public string OutText { get; set; } = "";
    public string DamagedText { get; set; } = "";
    public string FreeText { get; set; } = "";
    public string ClosingText { get; set; } = "";
}

/// <summary>سطر في كشف حركة المخزن مع الرصيد التراكمي.</summary>
public class StockLedgerRow
{
    public DateTime Date { get; init; }
    public int? ItemId { get; init; }
    public string ItemName { get; init; } = "";
    public string TypeLabel { get; init; } = "";
    public string Reference { get; init; } = "";
    public string? BatchNumber { get; init; }
    public string? Party { get; init; }
    public decimal In { get; init; }
    public decimal Out { get; init; }
    public decimal Balance { get; set; }
    // بعبوات الصنف؛ الرصيد التراكمي لكل (صنف، متغير) على حدة — أصناف مختلفة لا تُجمع في رقم واحد
    public string InText { get; set; } = "";
    public string OutText { get; set; } = "";
    public string BalanceText { get; set; } = "";
    /// <summary>الاسم المعروض: الاسم الموحد للمنتج المصنَّع («شرنك 20×330 زواج سعيد») أو اسم المادة الكامل.</summary>
    public string DisplayName { get; set; } = "";
    public string Pack { get; set; } = "";
    public string Content { get; set; } = "";
    public string VariantName { get; set; } = "";
    public bool IsFinished { get; set; }
}

public class StockBalanceRow
{
    public int ItemId { get; init; }
    public string ItemCode { get; init; } = "";
    public string ItemName { get; init; } = "";
    public string? BatchNumber { get; init; }
    public DateTime? ExpiryDate { get; init; }
    /// <summary>متغير التشغيلة: فارغ للأساسي، أو اسم المطعم/المناسبة.</summary>
    public string? Variant { get; init; }
    public decimal Quantity { get; init; }
    /// <summary>الكمية بوحدات التعبئة: "10 كارتون + 3 قطعة".</summary>
    public string Breakdown { get; set; } = "";
    public bool BelowAlert { get; set; }
    /// <summary>أجزاء الاسم الموحد للأعمدة المنفصلة: العبوة | المحتوى | المتغير.</summary>
    public string Pack { get; set; } = "";
    public string Content { get; set; } = "";
    public string VariantName { get; set; } = "";
    /// <summary>منتج مصنَّع (له الاسم الموحد) أم مادة أولية مشتراة (اسمها الكامل).</summary>
    public bool IsFinished { get; set; }
    /// <summary>الاسم المعروض في كل مخزن: الاسم الموحد للمصنَّع («شرنك 20×330 زواج سعيد») أو اسم المادة الكامل («سدادة — أبيض»).</summary>
    public string DisplayName { get; set; } = "";
    public string KindText => IsFinished ? "تام" : "أولية";
    /// <summary>رأس المجموعة: العبوة والمحتوى للمنتج التام («كارتون 40×330») واسم المادة للأولية — بلا رمز.</summary>
    public string GroupTitle { get; set; } = "";
    public string Group => GroupTitle.Length > 0 ? GroupTitle : ItemName;
}

public class StockDocumentRow
{
    public int Id { get; init; }
    public string DocumentNumber { get; init; } = "";
    public StockDocumentType DocumentType { get; init; }
    public DateTime DocumentDate { get; init; }
    public string? CounterWarehouse { get; init; }
    public string? PartyName { get; init; }
    public int LinesCount { get; init; }
    public decimal TotalPieces { get; init; }
    /// <summary>الكمية بعبوات الأصناف: «10 كارتون + 4 شرنك».</summary>
    public string TotalText { get; set; } = "";
    /// <summary>القطع التالفة ميدانيًا (في مستند الإرجاع من المندوب).</summary>
    public decimal DamagedPieces { get; init; }
    /// <summary>التالف الميداني بعبوات الأصناف (فارغ إن لم يوجد).</summary>
    public string DamagedText { get; set; } = "";
    public string CreatedBy { get; init; } = "";
    /// <summary>
    /// الكمية بالاسم في عمود القائمة: سطر واحد ← «10 شرنك 20×330 الرحمة» أو «1,200 سدادة — ذهبي»؛
    /// عدة أسطر ← «3 أسطر · 16 شرنك + 4 كارتون» ويُفتح الصف لرؤية كل سطر باسمه.
    /// </summary>
    public string QuantityText { get; set; } = "";
    public List<DocumentLineSummary> LineSummaries { get; set; } = new();
    public bool HasManyLines => LineSummaries.Count > 1;
    public bool IsVoided { get; init; }
    public string? VoidReason { get; init; }
    /// <summary>«مرحَّل» أو «ملغى — السبب».</summary>
    public string StatusText => IsVoided ? $"ملغى — {VoidReason}" : "مرحَّل";
}

/// <summary>سطر مختصر في المستند: الاسم الموحد/الكامل والكمية بوحدته.</summary>
public record DocumentLineSummary(string Name, string Quantity, bool IsDamaged);

/// <summary>
/// واجهة كل مخزن: مستندات الإدخال والإخراج والمناقلة والتالف والمسحوب المجاني (كل مستند ذري: كله أو لا شيء،
/// والصرف بترتيب الأقرب انتهاءً دون رصيد سالب)، وتقارير الحركة والأرصدة لكل مخزن على حدة.
/// </summary>
public class WarehouseDocumentService
{
    private readonly ProjectDbContext _db;
    public WarehouseDocumentService(ProjectDbContext db) => _db = db;

    public static string Prefix(StockDocumentType t) => t switch
    {
        StockDocumentType.Receipt => "SR",
        StockDocumentType.Issue => "SI",
        StockDocumentType.Transfer => "ST",
        StockDocumentType.Damaged => "SD",
        StockDocumentType.RepLoad => "RL",
        StockDocumentType.RepReturn => "RR",
        _ => "SF"
    };

    /// <summary>
    /// إلغاء مستند مخزني بسبب إلزامي: حركات عكسية بنفس النوع والتشغيلة والكلفة وكمية معاكسة، ويبقى المستند ظاهرًا «ملغى».
    /// لصاحب صلاحية «إلغاء المستندات المرحّلة». يُمنع في فترة مقفلة، أو إن جعل رصيد صنف سالبًا (بيع أو صُرف منه)،
    /// ولا تُلغى هنا مستندات المندوب ولا بيع المواد التالفة (تُصحَّح من مصدرها).
    /// </summary>
    public async Task<FinanceOperationResult> VoidAsync(int documentId, string reason, int userId)
    {
        if (string.IsNullOrWhiteSpace(reason)) return FinanceOperationResult.Fail("اكتب سبب الإلغاء");
        if (!await SpecialPermission.HasAsync(_db, userId, SpecialPermission.VoidPosted))
            return FinanceOperationResult.Fail("إلغاء المستندات لمن يملك صلاحية «إلغاء المستندات المرحّلة» فقط");
        var doc = await _db.StockDocuments.FirstOrDefaultAsync(d => d.Id == documentId);
        if (doc is null) return FinanceOperationResult.Fail("المستند غير موجود");
        if (doc.IsVoided) return FinanceOperationResult.Fail("المستند ملغى مسبقًا");
        if (doc.DocumentType is StockDocumentType.RepLoad or StockDocumentType.RepReturn)
            return FinanceOperationResult.Fail("مستندات المندوب تُصحَّح من شاشات المندوب (طلب التحميل أو مستند إرجاع معاكس)");
        if (await _db.DamagedSales.AnyAsync(x => x.StockDocumentId == doc.Id))
            return FinanceOperationResult.Fail("هذا المستند من «بيع المواد التالفة» — يُصحَّح من شاشة البيع");
        if (await new PeriodLockService(_db).GetLockedThroughAsync() is DateTime locked && doc.DocumentDate.Date <= locked)
            return FinanceOperationResult.Fail($"الفترة حتى {locked:yyyy/MM/dd} مقفلة — لا يُلغى مستند بتاريخ داخلها");

        var original = await _db.StockTransactions.AsNoTracking()
            .Where(t => t.ReferenceTable == "StockDocuments" && t.ReferenceId == doc.Id && !t.IsReversal).ToListAsync();
        if (original.Count == 0) return FinanceOperationResult.Fail("لا حركات لهذا المستند");

        // لا رصيد سالب بعد العكس: لكل (صنف، مخزن، تشغيلة) ما سيُسحب ≤ المتاح
        foreach (var g in original.GroupBy(t => new { t.ItemId, t.WarehouseId, t.BatchId }).Where(g => g.Sum(t => t.QuantityBaseUnits) > 0))
        {
            var balance = await _db.StockTransactions.Where(t => t.ItemId == g.Key.ItemId && t.WarehouseId == g.Key.WarehouseId && t.BatchId == g.Key.BatchId)
                .SumAsync(t => (decimal?)t.QuantityBaseUnits) ?? 0;
            var take = g.Sum(t => t.QuantityBaseUnits);
            if (balance - take < 0)
            {
                var item = await _db.Items.Where(i => i.Id == g.Key.ItemId).Select(i => i.ItemName).FirstAsync();
                var wh = await _db.Warehouses.Where(w => w.Id == g.Key.WarehouseId).Select(w => w.Name).FirstAsync();
                var packs = await PackFormatter.LoadAsync(_db, new[] { g.Key.ItemId });
                return FinanceOperationResult.Fail($"لا يمكن الإلغاء: صُرف أو بيع من «{item}» في «{wh}» — المتبقي {packs.Of(g.Key.ItemId, Math.Max(balance, 0))} " +
                                                   $"والإلغاء يحتاج {packs.Of(g.Key.ItemId, take)}. صحّح بمستند معاكس بدل الإلغاء.");
            }
        }

        await using var tx = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync() : null;
        var now = DateTime.UtcNow;
        foreach (var t in original)
            _db.StockTransactions.Add(new StockTransaction
            {
                ItemId = t.ItemId, WarehouseId = t.WarehouseId, BatchId = t.BatchId, QuantityBaseUnits = -t.QuantityBaseUnits,
                UnitCost = t.UnitCost, TransactionType = t.TransactionType, DamageReason = t.DamageReason, DamageCause = t.DamageCause,
                FreeIssueRecipient = t.FreeIssueRecipient, ReferenceTable = "StockDocuments", ReferenceId = doc.Id,
                IsReversal = true, TransactionDate = now, CreatedByUserId = userId
            });
        doc.IsVoided = true;
        doc.VoidReason = reason.Trim();
        doc.VoidedByUserId = userId;
        doc.VoidedAt = now;
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Void", "StockDocuments", doc.Id, $"{doc.DocumentNumber} — {doc.VoidReason}");
        if (tx is not null) await tx.CommitAsync();
        return FinanceOperationResult.Ok();
    }

    public async Task<(FinanceOperationResult result, StockDocument? document)> CreateAsync(StockDocumentRequest r)
    {
        if (r.Lines.Count == 0) return (FinanceOperationResult.Fail("أضف صنفًا واحدًا على الأقل"), null);
        if (r.Lines.Any(l => l.QuantityInLevel <= 0)) return (FinanceOperationResult.Fail("الكمية يجب أن تكون أكبر من صفر في كل السطور"), null);
        var warehouse = await _db.Warehouses.FindAsync(r.WarehouseId);
        if (warehouse is null || !warehouse.IsActive) return (FinanceOperationResult.Fail("المخزن غير موجود أو موقوف"), null);
        if (warehouse.WarehouseType == WarehouseType.WorkInProcess)
            return (FinanceOperationResult.Fail("رصيد تحت التصنيع يُدار من الإنتاج ← الماكينات وتحت التصنيع فقط"), null);
        // لا صرف حر للمواد الأولية: خروجها من المخزن يكون فقط "صرف مواد لأمر إنتاج"
        if (warehouse.WarehouseType == WarehouseType.RawMaterial && r.Type is StockDocumentType.Issue or StockDocumentType.FreeIssue)
            return (FinanceOperationResult.Fail("لا يُسمح بالصرف الحر من مخزن المواد الأولية — المواد تُصرف فقط لأمر إنتاج (بدء التشغيل)"), null);

        Warehouse? counter = null;
        Employee? rep = null;
        if (r.Type is StockDocumentType.RepLoad or StockDocumentType.RepReturn)
        {
            // إسناد حمولة: من مخزن المنتج التام إلى الكاش فان. إرجاع من مندوب: من الكاش فان إلى المخزن.
            if (r.CounterWarehouseId is null) return (FinanceOperationResult.Fail(r.Type == StockDocumentType.RepLoad ? "اختر سيارة المندوب" : "اختر المخزن المستلم"), null);
            counter = await _db.Warehouses.FindAsync(r.CounterWarehouseId);
            if (counter is null || !counter.IsActive) return (FinanceOperationResult.Fail("المخزن الآخر غير موجود أو موقوف"), null);
            var (van, store) = r.Type == StockDocumentType.RepLoad ? (counter, warehouse) : (warehouse, counter);
            if (van.WarehouseType != WarehouseType.RepVan || van.OwnerEmployeeId is null)
                return (FinanceOperationResult.Fail("المستند يخص كاش فان مندوب (حدّد صاحب السيارة من تعريف المخازن)"), null);
            if (store.WarehouseType is WarehouseType.RepVan or WarehouseType.WorkInProcess or WarehouseType.Damaged or WarehouseType.RawMaterial || !store.IsSellableStock)
                return (FinanceOperationResult.Fail("المخزن يجب أن يكون مخزن منتج تام قابل للبيع"), null);
            if (r.Type == StockDocumentType.RepLoad && r.Lines.Any(l => l.IsDamaged))
                return (FinanceOperationResult.Fail("التالف يُسجَّل في مستند الإرجاع من المندوب"), null);
            rep = await _db.Employees.FindAsync(van.OwnerEmployeeId);
        }
        if (r.Type == StockDocumentType.Transfer)
        {
            if (r.CounterWarehouseId is null) return (FinanceOperationResult.Fail("اختر المخزن المستلم"), null);
            if (r.CounterWarehouseId == r.WarehouseId) return (FinanceOperationResult.Fail("لا يمكن المناقلة إلى نفس المخزن"), null);
            counter = await _db.Warehouses.FindAsync(r.CounterWarehouseId);
            if (counter is null || !counter.IsActive) return (FinanceOperationResult.Fail("المخزن المستلم غير موجود أو موقوف"), null);
            if (counter.WarehouseType == WarehouseType.WorkInProcess)
                return (FinanceOperationResult.Fail("الصرف لتحت التصنيع يكون من أمر الإنتاج فقط"), null);
        }
        if (r.Type == StockDocumentType.Damaged && r.DamageReason is null)
            return (FinanceOperationResult.Fail("حدّد مكان التلف (نقل / مخزن / إنتاج)"), null);
        if (r.Type == StockDocumentType.Damaged && r.DamageCause is null)
            return (FinanceOperationResult.Fail("حدّد سبب التلف (سوء خزن / تسرب / انتهاء صلاحية / خطأ تصنيع / طباعة خاطئة / أخرى)"), null);
        if (r.Type == StockDocumentType.FreeIssue && string.IsNullOrWhiteSpace(r.PartyName))
            return (FinanceOperationResult.Fail("اكتب الجهة المستفيدة من المسحوب المجاني"), null);
        if (r.Type == StockDocumentType.Issue && string.IsNullOrWhiteSpace(r.PartyName))
            return (FinanceOperationResult.Fail("اكتب الجهة المستلمة أو الغرض من الإخراج"), null);

        var levelIds = r.Lines.Select(l => l.PackagingLevelId).Distinct().ToList();
        var levels = await _db.ItemPackagingLevels.Where(l => levelIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id);
        foreach (var l in r.Lines)
            if (!levels.TryGetValue(l.PackagingLevelId, out var lv) || lv.ItemId != l.ItemId)
                return (FinanceOperationResult.Fail("وحدة التعبئة لا تخص الصنف المختار"), null);

        // مخزن التالف يستقبل الكميات التالفة (إن وُجد ولم يكن المصدر نفسه)
        Warehouse? damagedStore = null;
        if ((r.Type == StockDocumentType.Damaged && r.MoveDamagedToDamagedWarehouse && warehouse.WarehouseType != WarehouseType.Damaged)
            || (r.Type == StockDocumentType.RepReturn && r.Lines.Any(l => l.IsDamaged)))
            damagedStore = await _db.Warehouses.Where(w => w.IsActive && w.WarehouseType == WarehouseType.Damaged).OrderBy(w => w.Id).FirstOrDefaultAsync();

        // داخل معاملة المستدعي إن وُجدت (مثل بيع المواد التالفة: مستند + قيد + صندوق في عملية واحدة)
        await using var tx = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync() : null;
        // المسحوب المجاني: تصنيف الجهة من قائمة الجهات الثابتة (أو كما اختير صراحةً) لتقرير شهري لكل جهة
        BeneficiaryCategory? category = null;
        if (r.Type == StockDocumentType.FreeIssue)
        {
            var name = Clean(r.PartyName);
            category = r.Category ?? await _db.FreeIssueBeneficiaries.Where(b => b.Name == name).Select(b => (BeneficiaryCategory?)b.Category).FirstOrDefaultAsync()
                       ?? BeneficiaryCategory.Other;
        }
        var seq = await _db.Database.SqlQueryRaw<int>("SELECT NEXT VALUE FOR seq_StockDocuments AS [Value]").ToListAsync();
        var doc = new StockDocument
        {
            DocumentNumber = $"{Prefix(r.Type)}-{r.Date.Year}-{seq[0]:D6}",
            DocumentType = r.Type, WarehouseId = r.WarehouseId, CounterWarehouseId = counter?.Id,
            DocumentDate = r.Date.Date, PartyName = rep?.FullName ?? Clean(r.PartyName), RepEmployeeId = rep?.Id,
            DamageReason = r.Type == StockDocumentType.Damaged ? r.DamageReason
                         : r.Type == StockDocumentType.RepReturn && r.Lines.Any(l => l.IsDamaged) ? ProjectDb.Entities.DamageReason.Field : null,
            DamageCause = r.Type == StockDocumentType.Damaged ? r.DamageCause : null,
            Notes = Clean(r.Notes), CreatedByUserId = r.UserId, BeneficiaryCategory = category
        };
        _db.StockDocuments.Add(doc);
        await _db.SaveChangesAsync();

        var when = r.Date.Date == DateTime.Today ? DateTime.UtcNow : r.Date.Date.AddHours(12).ToUniversalTime();
        foreach (var l in r.Lines)
        {
            var pieces = l.QuantityInLevel * levels[l.PackagingLevelId].EquivalentBaseUnits;
            int? batchId = l.BatchId;

            if (r.Type == StockDocumentType.Receipt)
            {
                // طلب خاص بلا تشغيلة: تشغيلة تلقائية لمتغيره في يوم المستند حتى لا يُسجَّل كأساسي
                var newNumber = !string.IsNullOrWhiteSpace(l.NewBatchNumber) ? l.NewBatchNumber.Trim()
                              : batchId is null && l.CustomRecipeId is int rid ? $"V{rid}-{r.Date:yyyyMMdd}" : null;
                if (batchId is not null && l.CustomRecipeId is not null
                    && await _db.ItemBatches.Where(b => b.Id == batchId).Select(b => b.CustomRecipeId).FirstOrDefaultAsync() != l.CustomRecipeId)
                    return (FinanceOperationResult.Fail("التشغيلة المختارة لمتغير آخر — اختر تشغيلة هذا الاسم الخاص أو تشغيلة جديدة"), null);
                if (batchId is null && newNumber is not null)
                {
                    var number = newNumber;
                    var batch = await _db.ItemBatches.FirstOrDefaultAsync(b => b.ItemId == l.ItemId && b.BatchNumber == number);
                    if (batch is null)
                    {
                        // تشغيلة جديدة لطلب خاص تحمل متغيرها، وإلا ضاع الاسم الخاص وظهرت كالأساسي
                        batch = new ItemBatch { ItemId = l.ItemId, BatchNumber = number, ExpiryDate = l.NewBatchExpiry?.Date, ManufactureDate = r.Date.Date,
                                                CustomRecipeId = l.CustomRecipeId };
                        _db.ItemBatches.Add(batch);
                        await _db.SaveChangesAsync();
                    }
                    else if (batch.CustomRecipeId != l.CustomRecipeId)
                        return (FinanceOperationResult.Fail($"التشغيلة {number} موجودة لمتغير آخر — اختر رقم تشغيلة جديدًا"), null);
                    batchId = batch.Id;
                }
                Add(l.ItemId, r.WarehouseId, batchId, pieces, StockTransactionType.Receipt);
            }
            else
            {
                // تحميل السيارة بلا تشغيلة محددة: المتغير المطلوب، أو الأساسي فقط فلا يخرج محجوز مطعم في حمولة عامة
                var scope = l.BatchId is null && (r.Type == StockDocumentType.RepLoad || l.CustomRecipeId is not null) ? BatchScope.Variant(l.CustomRecipeId) : null;
                var (allocation, error) = await LedgerHelper.AllocateAsync(_db, l.ItemId, r.WarehouseId, l.BatchId, pieces, scope);
                if (error is not null) return (FinanceOperationResult.Fail(error), null);
                foreach (var (b, qty) in allocation)
                {
                    switch (r.Type)
                    {
                        case StockDocumentType.Transfer:
                            Add(l.ItemId, r.WarehouseId, b, -qty, StockTransactionType.Transfer);
                            Add(l.ItemId, counter!.Id, b, qty, StockTransactionType.Transfer);
                            break;
                        case StockDocumentType.Damaged:
                            Add(l.ItemId, r.WarehouseId, b, -qty, StockTransactionType.Damaged, r.DamageReason);
                            if (damagedStore is not null) Add(l.ItemId, damagedStore.Id, b, qty, StockTransactionType.Damaged, r.DamageReason);
                            break;
                        case StockDocumentType.FreeIssue:
                            Add(l.ItemId, r.WarehouseId, b, -qty, StockTransactionType.FreeIssue);
                            break;
                        case StockDocumentType.RepLoad:
                            Add(l.ItemId, r.WarehouseId, b, -qty, StockTransactionType.RepLoad);
                            Add(l.ItemId, counter!.Id, b, qty, StockTransactionType.RepLoad);
                            break;
                        case StockDocumentType.RepReturn when l.IsDamaged:
                            // تلف ميداني: يُخصم من السيارة ولا يعود رصيدًا سليمًا (ينتقل لمخزن التالف إن وُجد)
                            Add(l.ItemId, r.WarehouseId, b, -qty, StockTransactionType.RepDamaged, ProjectDb.Entities.DamageReason.Field);
                            if (damagedStore is not null) Add(l.ItemId, damagedStore.Id, b, qty, StockTransactionType.Damaged, ProjectDb.Entities.DamageReason.Field);
                            break;
                        case StockDocumentType.RepReturn:
                            Add(l.ItemId, r.WarehouseId, b, -qty, StockTransactionType.RepReturn);
                            Add(l.ItemId, counter!.Id, b, qty, StockTransactionType.RepReturn);
                            break;
                        default:
                            Add(l.ItemId, r.WarehouseId, b, -qty, StockTransactionType.Issue);
                            break;
                    }
                }
            }
            doc.Lines.Add(new StockDocumentLine
            {
                ItemId = l.ItemId, PackagingLevelId = l.PackagingLevelId, QuantityInLevel = l.QuantityInLevel,
                QuantityBaseUnits = pieces, BatchId = batchId, Notes = Clean(l.Notes), IsDamaged = l.IsDamaged
            });
            await _db.SaveChangesAsync();
        }
        if (tx is not null) await tx.CommitAsync();
        return (FinanceOperationResult.Ok(), doc);

        void Add(int itemId, int whId, int? batch, decimal qty, StockTransactionType type, DamageReason? reason = null) =>
            _db.StockTransactions.Add(new StockTransaction
            {
                ItemId = itemId, WarehouseId = whId, BatchId = batch, QuantityBaseUnits = qty, TransactionType = type,
                DamageReason = reason, DamageCause = reason is null ? null : doc.DamageCause, FreeIssueRecipient = Clean(r.PartyName), ReferenceTable = "StockDocuments", ReferenceId = doc.Id,
                TransactionDate = when, CreatedByUserId = r.UserId
            });
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static (DateTime fromUtc, DateTime toUtc) Range(DateTime from, DateTime to) =>
        (from.Date.ToUniversalTime(), to.Date.AddDays(1).ToUniversalTime());

    // ============================ التقارير ============================

    /// <summary>لكل صنف: رصيد أول المدة، الوارد، الصادر، التالف، المجاني، والمتبقي آخر المدة.</summary>
    public async Task<List<StockSummaryRow>> GetSummaryAsync(int warehouseId, DateTime from, DateTime to)
    {
        var (f, t) = Range(from, to);
        var opening = await _db.StockTransactions.Where(x => x.WarehouseId == warehouseId && x.TransactionDate < f)
            .GroupBy(x => x.ItemId).Select(g => new { ItemId = g.Key, Qty = g.Sum(x => x.QuantityBaseUnits) })
            .ToDictionaryAsync(x => x.ItemId, x => x.Qty);
        var period = await _db.StockTransactions.Where(x => x.WarehouseId == warehouseId && x.TransactionDate >= f && x.TransactionDate < t)
            .GroupBy(x => new { x.ItemId, x.TransactionType })
            .Select(g => new
            {
                g.Key.ItemId, g.Key.TransactionType,
                In = g.Sum(x => x.QuantityBaseUnits > 0 ? x.QuantityBaseUnits : 0),
                Out = g.Sum(x => x.QuantityBaseUnits < 0 ? -x.QuantityBaseUnits : 0)
            }).ToListAsync();

        var ids = opening.Keys.Concat(period.Select(p => p.ItemId)).Distinct().ToList();
        var items = await _db.Items.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
        var rows = ids.Select(id => new StockSummaryRow
        {
            ItemId = id, ItemCode = items[id].ItemCode, ItemName = items[id].ItemName, Opening = opening.GetValueOrDefault(id)
        }).ToDictionary(r => r.ItemId);

        foreach (var p in period)
        {
            var row = rows[p.ItemId];
            row.In += p.In;
            switch (p.TransactionType)
            {
                case StockTransactionType.Damaged or StockTransactionType.RepDamaged: row.Damaged += p.Out; break;
                case StockTransactionType.FreeIssue or StockTransactionType.RepFreeSale: row.Free += p.Out; break;
                default: row.Out += p.Out; break;
            }
        }
        var packs = await PackFormatter.LoadAsync(_db, ids);
        var result = rows.Values.Where(r => r.Opening != 0 || r.In != 0 || r.Out != 0 || r.Damaged != 0 || r.Free != 0)
                          .OrderBy(r => r.ItemName).ToList();
        foreach (var r in result)
        {
            r.OpeningText = packs.Of(r.ItemId, r.Opening);
            r.InText = packs.Of(r.ItemId, r.In);
            r.OutText = packs.Of(r.ItemId, r.Out);
            r.DamagedText = packs.Of(r.ItemId, r.Damaged);
            r.FreeText = packs.Of(r.ItemId, r.Free);
            r.ClosingText = packs.Of(r.ItemId, r.Closing);
        }
        return result;
    }

    /// <summary>
    /// كشف حركة مفصّل بالترتيب الزمني مع رصيد تراكمي لكل (صنف، متغير) على حدة — لصنف واحد أو لكل الأصناف.
    /// </summary>
    public async Task<List<StockLedgerRow>> GetLedgerAsync(int warehouseId, int? itemId, DateTime from, DateTime to)
    {
        var (f, t) = Range(from, to);
        var openings = (await _db.StockTransactions.Where(x => x.WarehouseId == warehouseId && x.TransactionDate < f && (itemId == null || x.ItemId == itemId))
            .GroupBy(x => new { x.ItemId, RecipeId = x.Batch != null ? x.Batch.CustomRecipeId : null })
            .Select(g => new { g.Key.ItemId, g.Key.RecipeId, Qty = g.Sum(x => x.QuantityBaseUnits) }).ToListAsync())
            .ToDictionary(o => (o.ItemId, o.RecipeId), o => o.Qty);
        var tx = await _db.StockTransactions.AsNoTracking()
            .Where(x => x.WarehouseId == warehouseId && x.TransactionDate >= f && x.TransactionDate < t && (itemId == null || x.ItemId == itemId))
            .OrderBy(x => x.TransactionDate).ThenBy(x => x.Id)
            .Select(x => new { x.TransactionDate, x.ItemId, x.Item.ItemName,
                               x.TransactionType, x.DamageReason, x.DamageCause, x.IsReversal, x.ReferenceTable, x.ReferenceId,
                               BatchNumber = x.Batch != null ? x.Batch.BatchNumber : null, RecipeId = x.Batch != null ? x.Batch.CustomRecipeId : null,
                               x.FreeIssueRecipient, x.QuantityBaseUnits })
            .ToListAsync();

        var refs = await ResolveReferencesAsync(tx.Where(x => x.ReferenceId != null).Select(x => (x.ReferenceTable!, x.ReferenceId!.Value)));
        var ids = tx.Select(x => x.ItemId).Concat(openings.Keys.Select(k => k.ItemId)).Append(itemId ?? 0).Distinct().ToList();
        var packs = await PackFormatter.LoadAsync(_db, ids);
        var names = await ProductNames.LoadAsync(_db, ids);
        var finished = await _db.Items.AsNoTracking().Where(i => ids.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => i.SourcingMethod != SourcingMethod.Purchased);
        var itemNames = await _db.Items.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.ItemName);

        StockLedgerRow Named(StockLedgerRow r, int item, int? recipe, string itemName)
        {
            r.IsFinished = finished.GetValueOrDefault(item);
            if (r.IsFinished)
            {
                (r.Pack, r.Content, r.VariantName) = names.Parts(item, recipe);
                r.DisplayName = names.Name(item, recipe);
            }
            else r.DisplayName = itemName;
            return r;
        }

        var rows = new List<StockLedgerRow>();
        // رصيد أول المدة: سطر لكل (صنف، متغير) له رصيد سابق — أو سطر واحد بالصفر لكشف صنف واحد بلا رصيد سابق
        var balances = new Dictionary<(int, int?), decimal>(openings);
        foreach (var ((item, recipe), qty) in openings.Where(o => o.Value != 0).OrderBy(o => itemNames.GetValueOrDefault(o.Key.ItemId)))
            rows.Add(Named(new StockLedgerRow { Date = from.Date, ItemId = item, ItemName = itemNames.GetValueOrDefault(item, ""), TypeLabel = "رصيد أول المدة",
                                                Reference = "", Balance = qty, BalanceText = packs.Of(item, qty) }, item, recipe, itemNames.GetValueOrDefault(item, "")));
        if (itemId is int only && rows.Count == 0)
            rows.Add(Named(new StockLedgerRow { Date = from.Date, ItemId = only, ItemName = itemNames.GetValueOrDefault(only, ""), TypeLabel = "رصيد أول المدة",
                                                Reference = "", Balance = 0, BalanceText = "0" }, only, null, itemNames.GetValueOrDefault(only, "")));
        foreach (var x in tx)
        {
            var key = (x.ItemId, x.RecipeId);
            var balance = balances.GetValueOrDefault(key) + x.QuantityBaseUnits;
            balances[key] = balance;
            var source = x.ReferenceId is null ? default : refs.GetValueOrDefault((x.ReferenceTable!, x.ReferenceId.Value));
            rows.Add(Named(new StockLedgerRow
            {
                Date = x.TransactionDate.ToLocalTime(), ItemId = x.ItemId, ItemName = x.ItemName,
                TypeLabel = (x.IsReversal ? "إلغاء: " + TypeLabel(x.TransactionType, -x.QuantityBaseUnits) : TypeLabel(x.TransactionType, x.QuantityBaseUnits)) + (x.DamageReason is null ? "" : $" ({DamageLabel(x.DamageReason.Value)}{(x.DamageCause is null ? "" : " · " + CauseLabel(x.DamageCause.Value))})"),
                Reference = x.ReferenceId is null ? (x.ReferenceTable == "OpeningBalance" ? "رصيد افتتاحي" : "")
                          : source.Number ?? $"{x.ReferenceTable} #{x.ReferenceId}",
                BatchNumber = x.BatchNumber, Party = x.FreeIssueRecipient ?? source.Party,
                In = x.QuantityBaseUnits > 0 ? x.QuantityBaseUnits : 0, Out = x.QuantityBaseUnits < 0 ? -x.QuantityBaseUnits : 0,
                InText = x.QuantityBaseUnits > 0 ? packs.Of(x.ItemId, x.QuantityBaseUnits) : "",
                OutText = x.QuantityBaseUnits < 0 ? packs.Of(x.ItemId, -x.QuantityBaseUnits) : "",
                Balance = balance, BalanceText = packs.Of(x.ItemId, balance)
            }, x.ItemId, x.RecipeId, x.ItemName));
        }
        return rows;
    }

    public async Task<List<StockBalanceRow>> GetBalancesAsync(int warehouseId)
    {
        var raw = await _db.StockTransactions.Where(x => x.WarehouseId == warehouseId)
            .GroupBy(x => new { x.ItemId, x.BatchId })
            .Select(g => new { g.Key.ItemId, g.Key.BatchId, Qty = g.Sum(x => x.QuantityBaseUnits) })
            .Where(x => x.Qty != 0).ToListAsync();
        var itemIds = raw.Select(r => r.ItemId).Distinct().ToList();
        var batchIds = raw.Where(r => r.BatchId != null).Select(r => r.BatchId!.Value).Distinct().ToList();
        var items = await _db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
        var batches = await _db.ItemBatches.AsNoTracking().Include(b => b.CustomRecipe).Where(b => batchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id);
        var levels = (await _db.ItemPackagingLevels.AsNoTracking().Where(l => itemIds.Contains(l.ItemId)).ToListAsync())
            .GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.EquivalentBaseUnits).ToList());
        var totals = raw.GroupBy(r => r.ItemId).ToDictionary(g => g.Key, g => g.Sum(x => x.Qty));
        // حد التنبيه لهذا المخزن وحده (المضبوط من داخله، أو حد بطاقة الصنف إن كان مخزنه الرئيسي)
        var mins = await new StockAlertService(_db).TotalsAsync(new[] { warehouseId }, items.Values.ToList());

        var names = await ProductNames.LoadAsync(_db, itemIds);
        return raw.Select(r =>
        {
            var item = items[r.ItemId];
            var b = r.BatchId is null ? null : batches.GetValueOrDefault(r.BatchId.Value);
            var (pack, content, variant) = names.Parts(r.ItemId, b?.CustomRecipeId);
            var isFinished = item.SourcingMethod != SourcingMethod.Purchased;
            return new StockBalanceRow
            {
                Pack = pack, Content = content, VariantName = variant.Length > 0 ? variant : "أساسي",
                IsFinished = isFinished, DisplayName = isFinished ? names.Name(r.ItemId, b?.CustomRecipeId) : item.ItemName,
                GroupTitle = isFinished ? names.PackName(r.ItemId) : item.ItemName,
                ItemId = r.ItemId, ItemCode = item.ItemCode, ItemName = item.ItemName, BatchNumber = b?.BatchNumber, ExpiryDate = b?.ExpiryDate,
                Variant = b?.CustomRecipe?.Name,
                Quantity = r.Qty, Breakdown = Breakdown(r.Qty, levels.GetValueOrDefault(r.ItemId)),
                BelowAlert = mins.TryGetValue(r.ItemId, out var min) && totals[r.ItemId] <= min
            };
        }).OrderBy(r => r.ItemName).ThenBy(r => r.ExpiryDate ?? DateTime.MaxValue).ToList();
    }

    public static string Breakdown(decimal pieces, IReadOnlyList<ItemPackagingLevel>? levelsDesc)
    {
        // بفواصل الآلاف دائمًا: «4,449,330 قطعة» لا «4449330 قطعة»
        static string N(decimal n) => n.ToString("#,0.###", System.Globalization.CultureInfo.InvariantCulture);
        if (levelsDesc is null || levelsDesc.Count == 0 || pieces <= 0) return N(pieces);
        var parts = new List<string>();
        var rest = pieces;
        foreach (var l in levelsDesc.Where(l => l.EquivalentBaseUnits > 0))
        {
            var n = Math.Floor(rest / l.EquivalentBaseUnits);
            if (n <= 0) continue;
            parts.Add($"{N(n)} {l.LevelName}");
            rest -= n * l.EquivalentBaseUnits;
        }
        if (rest > 0) parts.Add(N(rest));
        return string.Join(" + ", parts);
    }

    /// <summary>مستندات المندوبين (إسناد حمولة وإرجاع) لكل السيارات أو لمندوب واحد.</summary>
    /// <summary>كمية كل مستند بعبوات أصنافه (القطع تبقى في TotalPieces).</summary>
    private async Task<List<StockDocumentRow>> WithPackTotalsAsync(List<StockDocumentRow> docs)
    {
        if (docs.Count == 0) return docs;
        var ids = docs.Select(d => d.Id).ToList();
        var lines = await _db.StockDocumentLines.AsNoTracking().Where(l => ids.Contains(l.StockDocumentId))
            .GroupBy(l => new { l.StockDocumentId, l.ItemId, l.IsDamaged, RecipeId = l.Batch != null ? l.Batch.CustomRecipeId : null })
            .Select(g => new { g.Key.StockDocumentId, g.Key.ItemId, g.Key.IsDamaged, g.Key.RecipeId, Qty = g.Sum(l => l.QuantityBaseUnits) }).ToListAsync();
        var itemIds = lines.Select(l => l.ItemId).Distinct().ToList();
        var packs = await PackFormatter.LoadAsync(_db, itemIds);
        var names = await ProductNames.LoadAsync(_db, itemIds);
        var items = await _db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => (i.ItemName, Finished: i.SourcingMethod != SourcingMethod.Purchased));
        var byDoc = lines.ToLookup(l => l.StockDocumentId);
        foreach (var d in docs)
        {
            var docLines = byDoc[d.Id].ToList();
            d.TotalText = packs.Total(docLines.Select(l => (l.ItemId, l.Qty)));
            var damaged = docLines.Where(l => l.IsDamaged).ToList();
            d.DamagedText = damaged.Count == 0 ? "" : packs.Total(damaged.Select(l => (l.ItemId, l.Qty)));
            d.LineSummaries = docLines.Select(l =>
            {
                var item = items.TryGetValue(l.ItemId, out var found) ? found : (ItemName: "؟", Finished: false);
                // المصنَّع بعبوته الكبرى: «10 شرنك» + الاسم الموحد؛ المادة الأولية: «1,200 قطعة» + اسمها الكامل
                var name = item.Finished ? names.Name(l.ItemId, l.RecipeId) : item.ItemName;
                return new DocumentLineSummary(name, packs.Of(l.ItemId, l.Qty), l.IsDamaged);
            }).ToList();
            // الإجمالي بلا جمع لقطع مواد مختلفة: عبوات المنتج التام، والمواد الأولية تُعدّ أصنافًا
            var fin = docLines.Where(l => items.TryGetValue(l.ItemId, out var it) && it.Finished).ToList();
            var rawCount = docLines.Where(l => !(items.TryGetValue(l.ItemId, out var it) && it.Finished)).Select(l => l.ItemId).Distinct().Count();
            var total = fin.Count == 0 ? (rawCount > 0 ? $"{rawCount} مادة أولية" : "0")
                      : packs.Total(fin.Select(l => (l.ItemId, l.Qty))) + (rawCount > 0 ? $" + {rawCount} مادة أولية" : "");
            d.QuantityText = d.LineSummaries.Count switch
            {
                0 => "",
                1 => $"{d.LineSummaries[0].Quantity} — {d.LineSummaries[0].Name}",
                var n => $"▾ {n} أسطر · {total}"
            };
        }
        return docs;
    }

    public async Task<List<StockDocumentRow>> GetRepDocumentsAsync(DateTime from, DateTime to, int? repEmployeeId = null)
    {
        var rows = await _db.StockDocuments.AsNoTracking()
            .Where(d => d.RepEmployeeId != null && (repEmployeeId == null || d.RepEmployeeId == repEmployeeId)
                        && d.DocumentDate >= from.Date && d.DocumentDate <= to.Date)
            .OrderByDescending(d => d.Id)
            .Select(d => new
            {
                d.Id, d.DocumentNumber, d.DocumentType, d.DocumentDate, Counter = d.CounterWarehouse != null ? d.CounterWarehouse.Name : null,
                d.PartyName, Lines = d.Lines.Count, Pieces = d.Lines.Sum(l => (decimal?)l.QuantityBaseUnits) ?? 0,
                Damaged = d.Lines.Where(l => l.IsDamaged).Sum(l => (decimal?)l.QuantityBaseUnits) ?? 0, User = d.CreatedByUser.Username
            }).ToListAsync();
        return await WithPackTotalsAsync(rows.Select(d => new StockDocumentRow
        {
            Id = d.Id, DocumentNumber = d.DocumentNumber, DocumentType = d.DocumentType, DocumentDate = d.DocumentDate,
            CounterWarehouse = d.Counter, PartyName = d.PartyName, LinesCount = d.Lines, TotalPieces = d.Pieces, DamagedPieces = d.Damaged, CreatedBy = d.User
        }).ToList());
    }

    public async Task<List<StockDocumentRow>> GetDocumentsAsync(int warehouseId, DateTime from, DateTime to, StockDocumentType? type = null)
    {
        var rows = await _db.StockDocuments.AsNoTracking()
            .Where(d => (d.WarehouseId == warehouseId || d.CounterWarehouseId == warehouseId) && d.DocumentDate >= from.Date && d.DocumentDate <= to.Date)
            .Where(d => type == null || d.DocumentType == type)
            .OrderByDescending(d => d.Id)
            .Select(d => new
            {
                d.Id, d.DocumentNumber, d.DocumentType, d.DocumentDate, Counter = d.CounterWarehouse != null ? d.CounterWarehouse.Name : null,
                d.PartyName, Lines = d.Lines.Count, Pieces = d.Lines.Sum(l => (decimal?)l.QuantityBaseUnits) ?? 0, User = d.CreatedByUser.Username,
                d.IsVoided, d.VoidReason
            }).ToListAsync();
        return await WithPackTotalsAsync(rows.Select(d => new StockDocumentRow
        {
            Id = d.Id, DocumentNumber = d.DocumentNumber, DocumentType = d.DocumentType, DocumentDate = d.DocumentDate,
            CounterWarehouse = d.Counter, PartyName = d.PartyName, LinesCount = d.Lines, TotalPieces = d.Pieces, CreatedBy = d.User,
            IsVoided = d.IsVoided, VoidReason = d.VoidReason
        }).ToList());
    }

    public Task<StockDocument?> GetDocumentAsync(int id) =>
        _db.StockDocuments.AsNoTracking()
            .Include(d => d.Warehouse).Include(d => d.CounterWarehouse).Include(d => d.CreatedByUser).Include(d => d.RepEmployee).Include(d => d.VoidedByUser)
            .Include(d => d.Lines).ThenInclude(l => l.Item)
            .Include(d => d.Lines).ThenInclude(l => l.PackagingLevel)
            .Include(d => d.Lines).ThenInclude(l => l.Batch)
            .FirstOrDefaultAsync(d => d.Id == id);

    /// <summary>أرقام المستندات المصدر للحركات (فاتورة، مستند مخزني، أمر إنتاج، استلام...) والجهة المقابلة فيها.</summary>
    private async Task<Dictionary<(string, int), (string? Number, string? Party)>> ResolveReferencesAsync(IEnumerable<(string table, int id)> refs)
    {
        var result = new Dictionary<(string, int), (string? Number, string? Party)>();
        foreach (var g in refs.Distinct().GroupBy(r => r.table))
        {
            var ids = g.Select(r => r.id).ToList();
            IEnumerable<(int Id, string Number, string? Party)> found = g.Key switch
            {
                "StockDocuments" => (await _db.StockDocuments.Where(d => ids.Contains(d.Id))
                        .Select(d => new { d.Id, d.DocumentNumber, d.PartyName, Counter = d.CounterWarehouse != null ? d.CounterWarehouse.Name : null }).ToListAsync())
                    .Select(x => (x.Id, x.DocumentNumber, x.PartyName ?? x.Counter)),
                "SalesInvoices" => (await _db.SalesInvoices.Where(d => ids.Contains(d.Id)).Select(d => new { d.Id, d.InvoiceNumber, d.Customer.Name }).ToListAsync())
                    .Select(x => (x.Id, x.InvoiceNumber, (string?)x.Name)),
                "ProductionOrders" => (await _db.ProductionOrders.Where(d => ids.Contains(d.Id)).Select(d => new { d.Id, d.MONumber }).ToListAsync())
                    .Select(x => (x.Id, x.MONumber, (string?)"الإنتاج")),
                "GoodsReceipts" => (await _db.GoodsReceipts.Where(d => ids.Contains(d.Id)).Select(d => new { d.Id, d.ReceiptNumber }).ToListAsync())
                    .Select(x => (x.Id, x.ReceiptNumber, (string?)null)),
                "LegacyImports" => ids.Select(i => (i, "رصيد افتتاحي (نقل من نظام الرحمة)", (string?)null)),
                _ => ids.Select(i => (i, $"{g.Key} #{i}", (string?)null))
            };
            foreach (var (id, number, party) in found) result[(g.Key, id)] = (number, party);
        }
        return result;
    }

    public static string TypeLabel(StockTransactionType t, decimal qty) => t switch
    {
        StockTransactionType.Receipt => "إدخال مخزني",
        StockTransactionType.Issue => "إخراج مخزني",
        StockTransactionType.SalesIssue => "مبيعات",
        StockTransactionType.Transfer => qty > 0 ? "مناقلة واردة" : "مناقلة صادرة",
        StockTransactionType.Damaged => qty > 0 ? "تالف وارد" : "تالف",
        StockTransactionType.FreeIssue => "مسحوب مجاني",
        StockTransactionType.ProductionConsume => "استهلاك إنتاج",
        StockTransactionType.WipIssue => qty > 0 ? "وارد تحت التصنيع" : "صرف لأمر إنتاج",
        StockTransactionType.WipReturn => qty > 0 ? "إرجاع من تحت التصنيع" : "إرجاع للمخزن",
        StockTransactionType.WipAdjust => "تعديل مشرف",
        StockTransactionType.ProductionOutput => "ناتج إنتاج",
        StockTransactionType.Packing => "تعبئة",
        StockTransactionType.ReturnToWarehouse => "إرجاع للمخزن",
        StockTransactionType.RepLoad => qty > 0 ? "تحميل سيارة (وارد)" : "تحميل سيارة",
        StockTransactionType.RepSale => "بيع مندوب",
        StockTransactionType.RepFreeSale => "بيع مجاني مندوب",
        StockTransactionType.RepDamaged => "تالف مندوب",
        StockTransactionType.RepReturn => qty > 0 ? "إرجاع من سيارة" : "إرجاع للمخزن",
        StockTransactionType.SyncConflictAdjustment => "تسوية تعارض",
        StockTransactionType.CustomerReturn => "مرتجع زبون",
        _ => t.ToString()
    };

    public static string DamageLabel(DamageReason r) => r switch
    {
        ProjectDb.Entities.DamageReason.Transit => "نقل",
        ProjectDb.Entities.DamageReason.Warehouse => "مخزن",
        ProjectDb.Entities.DamageReason.Field => "ميداني",
        _ => "إنتاج"
    };

    public static string CauseLabel(DamageCause c) => c switch
    {
        ProjectDb.Entities.DamageCause.PoorStorage => "سوء خزن",
        ProjectDb.Entities.DamageCause.Leakage => "تسرب",
        ProjectDb.Entities.DamageCause.Expired => "انتهاء صلاحية",
        ProjectDb.Entities.DamageCause.ManufacturingDefect => "خطأ تصنيع",
        ProjectDb.Entities.DamageCause.Misprint => "طباعة خاطئة",
        _ => "أخرى"
    };
}
