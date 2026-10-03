using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>سطر مستند كما يُدخل في واجهة المخزن (بوحدة التعبئة).</summary>
public record StockDocumentLineInput(int ItemId, int PackagingLevelId, decimal QuantityInLevel,
                                     int? BatchId = null, string? NewBatchNumber = null, DateTime? NewBatchExpiry = null,
                                     string? Notes = null, bool IsDamaged = false);

public record StockDocumentRequest(
    StockDocumentType Type, int WarehouseId, DateTime Date, IReadOnlyList<StockDocumentLineInput> Lines, int UserId,
    int? CounterWarehouseId = null, string? PartyName = null, DamageReason? DamageReason = null, string? Notes = null,
    bool MoveDamagedToDamagedWarehouse = true, BeneficiaryCategory? Category = null);

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
}

/// <summary>سطر في كشف حركة المخزن مع الرصيد التراكمي.</summary>
public class StockLedgerRow
{
    public DateTime Date { get; init; }
    public string ItemName { get; init; } = "";
    public string TypeLabel { get; init; } = "";
    public string Reference { get; init; } = "";
    public string? BatchNumber { get; init; }
    public string? Party { get; init; }
    public decimal In { get; init; }
    public decimal Out { get; init; }
    public decimal Balance { get; set; }
}

public class StockBalanceRow
{
    public int ItemId { get; init; }
    public string ItemCode { get; init; } = "";
    public string ItemName { get; init; } = "";
    public string? BatchNumber { get; init; }
    public DateTime? ExpiryDate { get; init; }
    public decimal Quantity { get; init; }
    /// <summary>الكمية بوحدات التعبئة: "10 كارتون + 3 قطعة".</summary>
    public string Breakdown { get; set; } = "";
    public bool BelowAlert { get; set; }
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
    /// <summary>القطع التالفة ميدانيًا (في مستند الإرجاع من المندوب).</summary>
    public decimal DamagedPieces { get; init; }
    public string CreatedBy { get; init; } = "";
}

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
            return (FinanceOperationResult.Fail("حدّد سبب التلف (نقل / مخزن / إنتاج)"), null);
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
                if (batchId is null && !string.IsNullOrWhiteSpace(l.NewBatchNumber))
                {
                    var number = l.NewBatchNumber.Trim();
                    var batch = await _db.ItemBatches.FirstOrDefaultAsync(b => b.ItemId == l.ItemId && b.BatchNumber == number);
                    if (batch is null)
                    {
                        batch = new ItemBatch { ItemId = l.ItemId, BatchNumber = number, ExpiryDate = l.NewBatchExpiry?.Date, ManufactureDate = r.Date.Date };
                        _db.ItemBatches.Add(batch);
                        await _db.SaveChangesAsync();
                    }
                    batchId = batch.Id;
                }
                Add(l.ItemId, r.WarehouseId, batchId, pieces, StockTransactionType.Receipt);
            }
            else
            {
                var (allocation, error) = await LedgerHelper.AllocateAsync(_db, l.ItemId, r.WarehouseId, l.BatchId, pieces);
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
                DamageReason = reason, FreeIssueRecipient = Clean(r.PartyName), ReferenceTable = "StockDocuments", ReferenceId = doc.Id,
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
        return rows.Values.Where(r => r.Opening != 0 || r.In != 0 || r.Out != 0 || r.Damaged != 0 || r.Free != 0)
                          .OrderBy(r => r.ItemName).ToList();
    }

    /// <summary>كشف حركة مفصّل بالترتيب الزمني مع رصيد تراكمي (لصنف واحد أو لكل الأصناف).</summary>
    public async Task<List<StockLedgerRow>> GetLedgerAsync(int warehouseId, int? itemId, DateTime from, DateTime to)
    {
        var (f, t) = Range(from, to);
        var opening = await _db.StockTransactions.Where(x => x.WarehouseId == warehouseId && x.TransactionDate < f && (itemId == null || x.ItemId == itemId))
            .SumAsync(x => (decimal?)x.QuantityBaseUnits) ?? 0;
        var tx = await _db.StockTransactions.AsNoTracking()
            .Where(x => x.WarehouseId == warehouseId && x.TransactionDate >= f && x.TransactionDate < t && (itemId == null || x.ItemId == itemId))
            .OrderBy(x => x.TransactionDate).ThenBy(x => x.Id)
            .Select(x => new { x.TransactionDate, x.Item.ItemName, x.TransactionType, x.DamageReason, x.ReferenceTable, x.ReferenceId,
                               BatchNumber = x.Batch != null ? x.Batch.BatchNumber : null, x.FreeIssueRecipient, x.QuantityBaseUnits })
            .ToListAsync();

        var refs = await ResolveReferencesAsync(tx.Where(x => x.ReferenceId != null).Select(x => (x.ReferenceTable!, x.ReferenceId!.Value)));
        var rows = new List<StockLedgerRow>();
        var balance = opening;
        if (itemId is not null || tx.Count > 0)
            rows.Add(new StockLedgerRow { Date = from.Date, ItemName = "", TypeLabel = "رصيد أول المدة", Reference = "", Balance = opening });
        foreach (var x in tx)
        {
            balance += x.QuantityBaseUnits;
            rows.Add(new StockLedgerRow
            {
                Date = x.TransactionDate.ToLocalTime(), ItemName = x.ItemName,
                TypeLabel = TypeLabel(x.TransactionType, x.QuantityBaseUnits) + (x.DamageReason is null ? "" : $" ({DamageLabel(x.DamageReason.Value)})"),
                Reference = x.ReferenceId is null ? "" : refs.GetValueOrDefault((x.ReferenceTable!, x.ReferenceId.Value), $"{x.ReferenceTable} #{x.ReferenceId}"),
                BatchNumber = x.BatchNumber, Party = x.FreeIssueRecipient,
                In = x.QuantityBaseUnits > 0 ? x.QuantityBaseUnits : 0, Out = x.QuantityBaseUnits < 0 ? -x.QuantityBaseUnits : 0,
                Balance = balance
            });
        }
        return rows;
    }

    /// <summary>الرصيد الحالي لكل صنف وتشغيلة في المخزن، مع التفكيك بوحدات التعبئة.</summary>
    public async Task<List<StockBalanceRow>> GetBalancesAsync(int warehouseId)
    {
        var raw = await _db.StockTransactions.Where(x => x.WarehouseId == warehouseId)
            .GroupBy(x => new { x.ItemId, x.BatchId })
            .Select(g => new { g.Key.ItemId, g.Key.BatchId, Qty = g.Sum(x => x.QuantityBaseUnits) })
            .Where(x => x.Qty != 0).ToListAsync();
        var itemIds = raw.Select(r => r.ItemId).Distinct().ToList();
        var batchIds = raw.Where(r => r.BatchId != null).Select(r => r.BatchId!.Value).Distinct().ToList();
        var items = await _db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
        var batches = await _db.ItemBatches.AsNoTracking().Where(b => batchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id);
        var levels = (await _db.ItemPackagingLevels.AsNoTracking().Where(l => itemIds.Contains(l.ItemId)).ToListAsync())
            .GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.EquivalentBaseUnits).ToList());
        var totals = raw.GroupBy(r => r.ItemId).ToDictionary(g => g.Key, g => g.Sum(x => x.Qty));

        return raw.Select(r =>
        {
            var item = items[r.ItemId];
            var b = r.BatchId is null ? null : batches.GetValueOrDefault(r.BatchId.Value);
            return new StockBalanceRow
            {
                ItemId = r.ItemId, ItemCode = item.ItemCode, ItemName = item.ItemName, BatchNumber = b?.BatchNumber, ExpiryDate = b?.ExpiryDate,
                Quantity = r.Qty, Breakdown = Breakdown(r.Qty, levels.GetValueOrDefault(r.ItemId)),
                BelowAlert = item.MinStockAlertLevel is { } min && totals[r.ItemId] <= min
            };
        }).OrderBy(r => r.ItemName).ThenBy(r => r.ExpiryDate ?? DateTime.MaxValue).ToList();
    }

    public static string Breakdown(decimal pieces, IReadOnlyList<ItemPackagingLevel>? levelsDesc)
    {
        if (levelsDesc is null || levelsDesc.Count == 0 || pieces <= 0) return $"{pieces:0.###}";
        var parts = new List<string>();
        var rest = pieces;
        foreach (var l in levelsDesc.Where(l => l.EquivalentBaseUnits > 0))
        {
            var n = Math.Floor(rest / l.EquivalentBaseUnits);
            if (n <= 0) continue;
            parts.Add($"{n:0} {l.LevelName}");
            rest -= n * l.EquivalentBaseUnits;
        }
        if (rest > 0) parts.Add($"{rest:0.###}");
        return string.Join(" + ", parts);
    }

    /// <summary>مستندات المندوبين (إسناد حمولة وإرجاع) لكل السيارات أو لمندوب واحد.</summary>
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
        return rows.Select(d => new StockDocumentRow
        {
            Id = d.Id, DocumentNumber = d.DocumentNumber, DocumentType = d.DocumentType, DocumentDate = d.DocumentDate,
            CounterWarehouse = d.Counter, PartyName = d.PartyName, LinesCount = d.Lines, TotalPieces = d.Pieces, DamagedPieces = d.Damaged, CreatedBy = d.User
        }).ToList();
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
                d.PartyName, Lines = d.Lines.Count, Pieces = d.Lines.Sum(l => (decimal?)l.QuantityBaseUnits) ?? 0, User = d.CreatedByUser.Username
            }).ToListAsync();
        return rows.Select(d => new StockDocumentRow
        {
            Id = d.Id, DocumentNumber = d.DocumentNumber, DocumentType = d.DocumentType, DocumentDate = d.DocumentDate,
            CounterWarehouse = d.Counter, PartyName = d.PartyName, LinesCount = d.Lines, TotalPieces = d.Pieces, CreatedBy = d.User
        }).ToList();
    }

    public Task<StockDocument?> GetDocumentAsync(int id) =>
        _db.StockDocuments.AsNoTracking()
            .Include(d => d.Warehouse).Include(d => d.CounterWarehouse).Include(d => d.CreatedByUser).Include(d => d.RepEmployee)
            .Include(d => d.Lines).ThenInclude(l => l.Item)
            .Include(d => d.Lines).ThenInclude(l => l.PackagingLevel)
            .Include(d => d.Lines).ThenInclude(l => l.Batch)
            .FirstOrDefaultAsync(d => d.Id == id);

    /// <summary>أرقام المستندات المصدر للحركات (فاتورة، مستند مخزني، أمر إنتاج، استلام...).</summary>
    private async Task<Dictionary<(string, int), string>> ResolveReferencesAsync(IEnumerable<(string table, int id)> refs)
    {
        var result = new Dictionary<(string, int), string>();
        foreach (var g in refs.Distinct().GroupBy(r => r.table))
        {
            var ids = g.Select(r => r.id).ToList();
            IEnumerable<(int Id, string Number)> found = g.Key switch
            {
                "StockDocuments" => (await _db.StockDocuments.Where(d => ids.Contains(d.Id)).Select(d => new { d.Id, d.DocumentNumber }).ToListAsync()).Select(x => (x.Id, x.DocumentNumber)),
                "SalesInvoices" => (await _db.SalesInvoices.Where(d => ids.Contains(d.Id)).Select(d => new { d.Id, d.InvoiceNumber }).ToListAsync()).Select(x => (x.Id, x.InvoiceNumber)),
                "ProductionOrders" => (await _db.ProductionOrders.Where(d => ids.Contains(d.Id)).Select(d => new { d.Id, d.MONumber }).ToListAsync()).Select(x => (x.Id, x.MONumber)),
                "GoodsReceipts" => (await _db.GoodsReceipts.Where(d => ids.Contains(d.Id)).Select(d => new { d.Id, d.ReceiptNumber }).ToListAsync()).Select(x => (x.Id, x.ReceiptNumber)),
                _ => ids.Select(i => (i, $"{g.Key} #{i}"))
            };
            foreach (var (id, number) in found) result[(g.Key, id)] = number;
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
        _ => t.ToString()
    };

    public static string DamageLabel(DamageReason r) => r switch
    {
        ProjectDb.Entities.DamageReason.Transit => "نقل",
        ProjectDb.Entities.DamageReason.Warehouse => "مخزن",
        ProjectDb.Entities.DamageReason.Field => "ميداني",
        _ => "إنتاج"
    };
}
