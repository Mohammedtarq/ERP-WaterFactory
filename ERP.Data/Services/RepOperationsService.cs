using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <param name="CustomRecipeId">متغير بعينه (مطعم، مناسبة). NULL = الأساسي.</param>
public record RepLoadLineInput(int ItemId, int PackagingLevelId, decimal QuantityInLevel, int? CustomRecipeId = null);

/// <summary>مجاني أعطاه المندوب: الكمية بوحدة التعبئة، لمن (اختياري) ولماذا (إلزامي).</summary>
public record RepFreeLineInput(int ItemId, int PackagingLevelId, decimal QuantityInLevel, int? CustomerId, string Reason);

public record RepExpenseInput(decimal Amount, string Description, int? VehicleId = null);

/// <param name="InvoiceRemainingToCustomerId">إن حُدّد: ما بقي في السيارة بعد المرتجع والمجاني يُعتبر مبيعًا نقديًا لهذا العميل بفاتورة تلقائية.</param>
public record RepSettlementRequest(
    int VanWarehouseId, int ReturnToWarehouseId, DateTime Date,
    IReadOnlyList<StockDocumentLineInput> Returns, IReadOnlyList<RepFreeLineInput> FreeGoods, IReadOnlyList<RepExpenseInput> Expenses,
    decimal ReceivedCash, int UserId, int? InvoiceRemainingToCustomerId = null, string? Notes = null);

public class RepLoadOrderRow
{
    public int Id { get; init; }
    public string OrderNumber { get; init; } = "";
    public DateTime LoadDate { get; init; }
    public string RepName { get; init; } = "";
    public string VanName { get; init; } = "";
    public RepLoadOrderStatus Status { get; init; }
    public int LinesCount { get; init; }
    public decimal RequestedPieces { get; init; }
    public string? DocumentNumber { get; init; }
    public string RequestedBy { get; init; } = "";
    public string? PreparedBy { get; init; }
    public string StatusText => Status switch
    {
        RepLoadOrderStatus.Pending => "بانتظار التجهيز",
        RepLoadOrderStatus.Prepared => "مُجهَّز",
        _ => "ملغى"
    };
}

public class RepSettlementRow
{
    public int Id { get; init; }
    public string SettlementNumber { get; init; } = "";
    public DateTime SettlementDate { get; init; }
    public string RepName { get; init; } = "";
    public decimal ReturnedPieces { get; init; }
    public decimal FreePieces { get; init; }
    public decimal FreeCost { get; init; }
    public decimal FieldExpenses { get; init; }
    public decimal ExpectedCash { get; init; }
    public decimal ReceivedCash { get; init; }
    public decimal Difference { get; init; }
    public string? InvoiceNumber { get; init; }
    public string CreatedBy { get; init; } = "";
}

/// <summary>مندوب تأخرت تسويته أكثر من يوم وعنده بضاعة في السيارة أو نقد في المحفظة.</summary>
public class OverdueRepRow
{
    public int RepEmployeeId { get; init; }
    public string RepName { get; init; } = "";
    public string VanName { get; init; } = "";
    public DateTime? LastSettlement { get; init; }
    public int DaysOverdue { get; init; }
    public decimal VanPieces { get; init; }
    public decimal WalletBalance { get; init; }
}

/// <summary>
/// دورة المندوب اليومية (30_rep_loads_settlement.sql):
/// الحمولة الافتراضية ← طلب تحميل من مدير المبيعات ← تجهيز أمين المخزن (مستند إسناد مرقّم) ←
/// تسوية نهاية اليوم مع أمين الصندوق (مرتجع + مجاني + مصاريف + نقد) في معاملة واحدة.
/// </summary>
public class RepOperationsService
{
    /// <summary>بعد كم يوم بلا تسوية يظهر المندوب في قائمة المتأخرين.</summary>
    public const int OverdueAfterDays = 1;

    private readonly ProjectDbContext _db;
    public RepOperationsService(ProjectDbContext db) => _db = db;

    private async Task<(Warehouse? van, string? error)> VanAsync(int vanWarehouseId)
    {
        var van = await _db.Warehouses.Include(w => w.OwnerEmployee).FirstOrDefaultAsync(w => w.Id == vanWarehouseId);
        if (van is null || van.WarehouseType != WarehouseType.RepVan || !van.IsActive) return (null, "اختر سيارة مندوب (كاش فان) فعّالة");
        if (van.OwnerEmployeeId is null) return (null, "السيارة غير مرتبطة بمندوب — حدّده من تعريف المخازن");
        return (van, null);
    }

    private async Task<string?> ValidateLevelsAsync(IEnumerable<(int itemId, int levelId, decimal qty)> lines)
    {
        var list = lines.ToList();
        if (list.Count == 0) return "أضف صنفًا واحدًا على الأقل";
        if (list.Any(l => l.qty <= 0)) return "الكمية يجب أن تكون أكبر من صفر في كل السطور";
        var ids = list.Select(l => l.levelId).Distinct().ToList();
        var levels = await _db.ItemPackagingLevels.Where(l => ids.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.ItemId);
        return list.All(l => levels.TryGetValue(l.levelId, out var item) && item == l.itemId) ? null : "وحدة التعبئة لا تخص الصنف المختار";
    }

    // ============================ الحمولة الافتراضية ============================

    public Task<List<RepDefaultLoad>> GetDefaultLoadAsync(int repEmployeeId) =>
        _db.RepDefaultLoads.AsNoTracking().Include(l => l.Item).Include(l => l.PackagingLevel)
           .Where(l => l.RepEmployeeId == repEmployeeId).OrderBy(l => l.Item.ItemName).ToListAsync();

    /// <summary>يستبدل الحمولة الافتراضية للمندوب كاملة بالسطور المعطاة.</summary>
    public async Task<FinanceOperationResult> SaveDefaultLoadAsync(int repEmployeeId, IReadOnlyList<RepLoadLineInput> lines, int userId)
    {
        if (!await _db.Employees.AnyAsync(e => e.Id == repEmployeeId && e.IsSalesRep)) return FinanceOperationResult.Fail("الموظف المختار ليس مندوب مبيعات");
        var merged = lines.GroupBy(l => (l.ItemId, l.PackagingLevelId)).Select(g => new RepLoadLineInput(g.Key.ItemId, g.Key.PackagingLevelId, g.Sum(x => x.QuantityInLevel))).ToList();
        if (merged.Count > 0 && await ValidateLevelsAsync(merged.Select(l => (l.ItemId, l.PackagingLevelId, l.QuantityInLevel))) is { } error)
            return FinanceOperationResult.Fail(error);
        _db.RepDefaultLoads.RemoveRange(await _db.RepDefaultLoads.Where(l => l.RepEmployeeId == repEmployeeId).ToListAsync());
        foreach (var l in merged)
            _db.RepDefaultLoads.Add(new RepDefaultLoad { RepEmployeeId = repEmployeeId, ItemId = l.ItemId, PackagingLevelId = l.PackagingLevelId, QuantityInLevel = l.QuantityInLevel });
        await _db.SaveChangesAsync();
        return FinanceOperationResult.Ok();
    }

    // ============================ طلبات التحميل ============================

    public async Task<(FinanceOperationResult result, RepLoadOrder? order)> CreateLoadOrderAsync(
        int vanWarehouseId, int fromWarehouseId, DateTime date, IReadOnlyList<RepLoadLineInput> lines, string? notes, int userId)
    {
        var (van, vanError) = await VanAsync(vanWarehouseId);
        if (vanError is not null) return (FinanceOperationResult.Fail(vanError), null);
        var source = await _db.Warehouses.FindAsync(fromWarehouseId);
        if (source is null || !source.IsActive || !source.IsSellableStock || source.WarehouseType is WarehouseType.RepVan or WarehouseType.WorkInProcess or WarehouseType.RawMaterial)
            return (FinanceOperationResult.Fail("التحميل يكون من مخزن منتج تام قابل للبيع"), null);
        if (await ValidateLevelsAsync(lines.Select(l => (l.ItemId, l.PackagingLevelId, l.QuantityInLevel))) is { } error)
            return (FinanceOperationResult.Fail(error), null);
        if (await _db.RepLoadOrders.AnyAsync(o => o.VanWarehouseId == vanWarehouseId && o.Status == RepLoadOrderStatus.Pending))
            return (FinanceOperationResult.Fail($"لدى {van!.Name} طلب تحميل بانتظار التجهيز — جهّزه أو ألغه أولًا"), null);

        var year = date.Year;
        var count = await _db.RepLoadOrders.CountAsync(o => o.LoadDate.Year == year);
        var order = new RepLoadOrder
        {
            OrderNumber = $"LO-{year}-{count + 1:D5}", RepEmployeeId = van!.OwnerEmployeeId!.Value, VanWarehouseId = van.Id,
            FromWarehouseId = fromWarehouseId, LoadDate = date.Date, Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            RequestedByUserId = userId,
            Lines = lines.GroupBy(l => (l.ItemId, l.PackagingLevelId, l.CustomRecipeId))
                         .Select(g => new RepLoadOrderLine { ItemId = g.Key.ItemId, PackagingLevelId = g.Key.PackagingLevelId, CustomRecipeId = g.Key.CustomRecipeId,
                                                             QuantityInLevel = g.Sum(x => x.QuantityInLevel) })
                         .ToList()
        };
        _db.RepLoadOrders.Add(order);
        await _db.SaveChangesAsync();
        return (FinanceOperationResult.Ok(), order);
    }

    /// <summary>
    /// تجهيز الطلب من أمين المخزن: الكميات المجهَّزة فعلًا (افتراضيًا = المطلوب) تخرج بمستند إسناد حمولة مرقّم.
    /// السطر المجهَّز بصفر يُترك (لم يتوفر)؛ ولا شيء يتحرك قبل التجهيز.
    /// </summary>
    public async Task<(FinanceOperationResult result, StockDocument? document)> PrepareLoadOrderAsync(
        int orderId, IReadOnlyDictionary<int, decimal>? preparedByLineId, int userId)
    {
        var order = await _db.RepLoadOrders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == orderId);
        if (order is null) return (FinanceOperationResult.Fail("الطلب غير موجود"), null);
        if (order.Status != RepLoadOrderStatus.Pending) return (FinanceOperationResult.Fail("الطلب ليس بانتظار التجهيز"), null);

        foreach (var l in order.Lines)
        {
            var qty = preparedByLineId is not null && preparedByLineId.TryGetValue(l.Id, out var q) ? q : l.QuantityInLevel;
            if (qty < 0) return (FinanceOperationResult.Fail("الكمية المجهَّزة لا تكون سالبة"), null);
            l.PreparedQuantity = qty;
        }
        var docLines = order.Lines.Where(l => l.PreparedQuantity > 0)
                                  .Select(l => new StockDocumentLineInput(l.ItemId, l.PackagingLevelId, l.PreparedQuantity!.Value, CustomRecipeId: l.CustomRecipeId)).ToList();
        if (docLines.Count == 0) return (FinanceOperationResult.Fail("لا كمية مجهَّزة — ألغِ الطلب بدل تجهيزه بصفر"), null);

        await using var tx = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync() : null;
        var (result, doc) = await new WarehouseDocumentService(_db).CreateAsync(new StockDocumentRequest(
            StockDocumentType.RepLoad, order.FromWarehouseId, order.LoadDate, docLines, userId,
            CounterWarehouseId: order.VanWarehouseId, Notes: $"تجهيز طلب التحميل {order.OrderNumber}"));
        if (!result.Success) return (result, null);
        order.Status = RepLoadOrderStatus.Prepared;
        order.PreparedByUserId = userId;
        order.PreparedAt = DateTime.UtcNow;
        order.StockDocumentId = doc!.Id;
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Post", "RepLoadOrders", order.Id, $"تجهيز {order.OrderNumber} ← {doc.DocumentNumber}");
        if (tx is not null) await tx.CommitAsync();
        return (FinanceOperationResult.Ok(), doc);
    }

    public async Task<FinanceOperationResult> CancelLoadOrderAsync(int orderId, string reason, int userId)
    {
        if (string.IsNullOrWhiteSpace(reason)) return FinanceOperationResult.Fail("اكتب سبب الإلغاء");
        var order = await _db.RepLoadOrders.FindAsync(orderId);
        if (order is null) return FinanceOperationResult.Fail("الطلب غير موجود");
        if (order.Status != RepLoadOrderStatus.Pending) return FinanceOperationResult.Fail("لا يُلغى إلا طلب بانتظار التجهيز");
        order.Status = RepLoadOrderStatus.Cancelled;
        order.CancelReason = reason.Trim();
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(userId, "Void", "RepLoadOrders", order.Id, $"إلغاء {order.OrderNumber}: {order.CancelReason}");
        return FinanceOperationResult.Ok();
    }

    public async Task<List<RepLoadOrderRow>> GetLoadOrdersAsync(DateTime from, DateTime to, RepLoadOrderStatus? status = null)
    {
        var q = _db.RepLoadOrders.AsNoTracking().Where(o => o.LoadDate >= from.Date && o.LoadDate <= to.Date || o.Status == RepLoadOrderStatus.Pending);
        if (status is not null) q = q.Where(o => o.Status == status);
        return await q.OrderBy(o => o.Status == RepLoadOrderStatus.Pending ? 0 : 1).ThenByDescending(o => o.LoadDate).ThenByDescending(o => o.Id)
            .Select(o => new RepLoadOrderRow
            {
                Id = o.Id, OrderNumber = o.OrderNumber, LoadDate = o.LoadDate, RepName = o.RepEmployee.FullName, VanName = o.VanWarehouse.Name,
                Status = o.Status, LinesCount = o.Lines.Count,
                RequestedPieces = o.Lines.Sum(l => l.QuantityInLevel * l.PackagingLevel.EquivalentBaseUnits),
                DocumentNumber = o.StockDocument != null ? o.StockDocument.DocumentNumber : null,
                RequestedBy = o.RequestedByUser.Username, PreparedBy = o.PreparedByUser != null ? o.PreparedByUser.Username : null
            }).ToListAsync();
    }

    public Task<RepLoadOrder?> GetLoadOrderAsync(int orderId) =>
        _db.RepLoadOrders.AsNoTracking().Include(o => o.Lines).ThenInclude(l => l.Item).Include(o => o.Lines).ThenInclude(l => l.PackagingLevel)
           .Include(o => o.Lines).ThenInclude(l => l.CustomRecipe)
           .Include(o => o.RepEmployee).Include(o => o.VanWarehouse).Include(o => o.FromWarehouse).Include(o => o.StockDocument)
           .FirstOrDefaultAsync(o => o.Id == orderId);

    // ============================ التسوية اليومية ============================

    /// <summary>
    /// تسوية المندوب مع أمين الصندوق، كلها أو لا شيء:
    /// 1) المرتجع (السليم للمخزن، والتالف الميداني لمخزن التالف) بمستند إرجاع مرقّم
    /// 2) مجاني المندوب: يخرج من السيارة بالكلفة مع العميل والسبب
    /// 3) اختياريًا: ما بقي في السيارة يُفوتر نقدًا تلقائيًا (فيدخل نقده المحفظة)
    /// 4) المصاريف الميدانية من المحفظة (مع السيارة)
    /// 5) النقد المتوقع = رصيد المحفظة؛ المستلم يدخل صندوق المستلم، والفرق يبقى في ذمة المندوب
    /// </summary>
    public async Task<(FinanceOperationResult result, RepSettlement? settlement)> SettleAsync(RepSettlementRequest r)
    {
        var (van, vanError) = await VanAsync(r.VanWarehouseId);
        if (vanError is not null) return (FinanceOperationResult.Fail(vanError), null);
        var repId = van!.OwnerEmployeeId!.Value;
        if (r.ReceivedCash < 0) return (FinanceOperationResult.Fail("المبلغ المستلم لا يكون سالبًا"), null);
        if (r.FreeGoods.Any(f => string.IsNullOrWhiteSpace(f.Reason))) return (FinanceOperationResult.Fail("اكتب سبب كل مادة مجانية (لمن ولماذا)"), null);
        if (r.FreeGoods.Count > 0 && await ValidateLevelsAsync(r.FreeGoods.Select(f => (f.ItemId, f.PackagingLevelId, f.QuantityInLevel))) is { } freeError)
            return (FinanceOperationResult.Fail(freeError), null);
        if (r.Expenses.Any(e => e.Amount <= 0 || string.IsNullOrWhiteSpace(e.Description)))
            return (FinanceOperationResult.Fail("كل مصروف ميداني يحتاج مبلغًا أكبر من صفر ووصفًا"), null);

        var reps = new RepsService(_db);
        await using var tx = _db.Database.CurrentTransaction is null ? await _db.Database.BeginTransactionAsync() : null;

        var count = await _db.RepSettlements.CountAsync(s => s.SettlementDate.Year == r.Date.Year);
        var settlement = new RepSettlement
        {
            SettlementNumber = $"RS-{r.Date.Year}-{count + 1:D5}", RepEmployeeId = repId, VanWarehouseId = van.Id,
            SettlementDate = r.Date.Date, Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes.Trim(), CreatedByUserId = r.UserId
        };
        _db.RepSettlements.Add(settlement);
        await _db.SaveChangesAsync();

        // 1) المرتجع
        if (r.Returns.Count > 0)
        {
            var (ret, doc) = await new WarehouseDocumentService(_db).CreateAsync(new StockDocumentRequest(
                StockDocumentType.RepReturn, van.Id, r.Date, r.Returns, r.UserId, CounterWarehouseId: r.ReturnToWarehouseId,
                Notes: $"تسوية {settlement.SettlementNumber}"));
            if (!ret.Success) return (ret, null);
            settlement.ReturnDocumentId = doc!.Id;
            settlement.ReturnedPieces = doc.Lines.Sum(l => l.QuantityBaseUnits);
        }

        // 2) مجاني المندوب
        if (r.FreeGoods.Count > 0)
        {
            var levelIds = r.FreeGoods.Select(f => f.PackagingLevelId).Distinct().ToList();
            var levels = await _db.ItemPackagingLevels.Where(l => levelIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.EquivalentBaseUnits);
            var customerIds = r.FreeGoods.Where(f => f.CustomerId != null).Select(f => f.CustomerId!.Value).Distinct().ToList();
            var customers = await _db.Customers.Where(c => customerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name);
            var when = r.Date.Date == DateTime.Today ? DateTime.UtcNow : r.Date.Date.AddHours(12).ToUniversalTime();
            var canReserved = await SpecialPermission.HasAsync(_db, r.UserId, SpecialPermission.ReservedStock);
            foreach (var f in r.FreeGoods)
            {
                var pieces = f.QuantityInLevel * levels[f.PackagingLevelId];
                var (alloc, error) = await LedgerHelper.AllocateAsync(_db, f.ItemId, van.Id, null, pieces, BatchScope.ForCustomer(f.CustomerId, null, canReserved));
                if (error is not null) return (FinanceOperationResult.Fail($"المجاني: {error}"), null);
                var who = f.CustomerId is int cid && customers.TryGetValue(cid, out var name) ? $"{name} — {f.Reason.Trim()}" : f.Reason.Trim();
                foreach (var (batchId, qty) in alloc)
                    _db.StockTransactions.Add(new StockTransaction
                    {
                        ItemId = f.ItemId, WarehouseId = van.Id, BatchId = batchId, QuantityBaseUnits = -qty, TransactionType = StockTransactionType.RepFreeSale,
                        FreeIssueRecipient = who.Length > 200 ? who[..200] : who, ReferenceTable = "RepSettlements", ReferenceId = settlement.Id,
                        TransactionDate = when, CreatedByUserId = r.UserId
                    });
                var unitCost = await _db.Items.Where(i => i.Id == f.ItemId).Select(i => i.CostPrice).FirstAsync();
                settlement.FreeGoods.Add(new RepFreeGood { ItemId = f.ItemId, QuantityBaseUnits = pieces, UnitCost = unitCost, CustomerId = f.CustomerId, Reason = f.Reason.Trim() });
                settlement.FreePieces += pieces;
                settlement.FreeCost += Math.Round(pieces * (unitCost ?? 0), 2);
                await _db.SaveChangesAsync();
            }
        }

        // 3) ما بقي في السيارة يُفوتر نقدًا
        if (r.InvoiceRemainingToCustomerId is int invoiceCustomer)
        {
            // لكل تشغيلة سطر: فلا يختلط متغير مطعم بالأساسي، ويطبّق الترحيل قاعدة الحجز على العميل المختار
            var remaining = await _db.StockTransactions.Where(t => t.WarehouseId == van.Id)
                .GroupBy(t => new { t.ItemId, t.BatchId }).Select(g => new { g.Key.ItemId, g.Key.BatchId, Qty = g.Sum(t => t.QuantityBaseUnits) })
                .Where(x => x.Qty > 0).ToListAsync();
            if (remaining.Count > 0)
            {
                var sales = new SalesService(_db);
                var (created, invoiceId) = await sales.CreateInvoiceAsync(new SalesInvoiceHeaderInput(
                    invoiceCustomer, van.Id, r.Date, InvoicePaymentMethod.Cash, SalesRepEmployeeId: repId,
                    Notes: $"مبيعات اليوم — تسوية {settlement.SettlementNumber}"), r.UserId);
                if (!created.Success) return (created, null);
                foreach (var x in remaining)
                {
                    var pieceLevel = await _db.ItemPackagingLevels.Where(l => l.ItemId == x.ItemId && l.EquivalentBaseUnits == 1)
                                                                 .Select(l => (int?)l.Id).FirstOrDefaultAsync();
                    if (pieceLevel is null)
                        return (FinanceOperationResult.Fail("صنف في السيارة بلا وحدة \"قطعة\" — عرّفها من بطاقة الصنف أو فوتره يدويًا"), null);
                    var added = await sales.AddLineAsync(invoiceId!.Value, new SalesInvoiceLineInput(x.ItemId, pieceLevel.Value, x.Qty, BatchId: x.BatchId), r.UserId);
                    if (!added.Success) return (added, null);
                }
                var (posted, _) = await sales.PostInvoiceAsync(invoiceId!.Value, r.UserId);
                if (!posted.Success) return (posted, null);
                settlement.AutoInvoiceId = invoiceId;
            }
        }

        // 4) المصاريف الميدانية
        foreach (var e in r.Expenses)
        {
            var spent = await reps.RecordFieldExpenseAsync(repId, e.Amount, e.Description.Trim(), r.Date, r.UserId, e.VehicleId);
            if (!spent.Success) return (FinanceOperationResult.Fail($"المصروف \"{e.Description}\": {spent.ErrorMessage}"), null);
            settlement.FieldExpenses += e.Amount;
        }

        // 5) النقد
        settlement.ExpectedCash = await reps.GetWalletBalanceAsync(repId);
        if (r.ReceivedCash > settlement.ExpectedCash)
            return (FinanceOperationResult.Fail($"المستلم ({r.ReceivedCash:N0}) أكبر من النقد المتوقع في المحفظة ({settlement.ExpectedCash:N0}) — راجع المبيعات والتحصيلات"), null);
        if (r.ReceivedCash > 0)
        {
            var handed = await reps.RecordCashHandoverAsync(repId, r.ReceivedCash, r.Date, r.UserId);
            if (!handed.Success) return (handed, null);
        }
        settlement.ReceivedCash = r.ReceivedCash;
        settlement.Difference = settlement.ExpectedCash - r.ReceivedCash;
        await _db.SaveChangesAsync();
        await new AuditService(_db).LogAsync(r.UserId, "Post", "RepSettlements", settlement.Id,
            $"{settlement.SettlementNumber} — متوقع {settlement.ExpectedCash:N0}، مستلم {settlement.ReceivedCash:N0}، فرق {settlement.Difference:N0}");
        if (tx is not null) await tx.CommitAsync();
        return (FinanceOperationResult.Ok(), settlement);
    }

    public async Task<List<RepSettlementRow>> GetSettlementsAsync(DateTime from, DateTime to, int? repEmployeeId = null)
    {
        var q = _db.RepSettlements.AsNoTracking().Where(s => s.SettlementDate >= from.Date && s.SettlementDate <= to.Date);
        if (repEmployeeId is not null) q = q.Where(s => s.RepEmployeeId == repEmployeeId);
        return await q.OrderByDescending(s => s.SettlementDate).ThenByDescending(s => s.Id)
            .Select(s => new RepSettlementRow
            {
                Id = s.Id, SettlementNumber = s.SettlementNumber, SettlementDate = s.SettlementDate, RepName = s.RepEmployee.FullName,
                ReturnedPieces = s.ReturnedPieces, FreePieces = s.FreePieces, FreeCost = s.FreeCost, FieldExpenses = s.FieldExpenses,
                ExpectedCash = s.ExpectedCash, ReceivedCash = s.ReceivedCash, Difference = s.Difference,
                InvoiceNumber = s.AutoInvoice != null ? s.AutoInvoice.InvoiceNumber : null, CreatedBy = s.CreatedByUser.Username
            }).ToListAsync();
    }

    public Task<RepSettlement?> GetSettlementAsync(int settlementId) =>
        _db.RepSettlements.AsNoTracking().Include(s => s.RepEmployee).Include(s => s.VanWarehouse).Include(s => s.CreatedByUser)
           .Include(s => s.ReturnDocument).ThenInclude(d => d!.Lines).ThenInclude(l => l.Item)
           .Include(s => s.AutoInvoice).Include(s => s.FreeGoods).ThenInclude(f => f.Item).Include(s => s.FreeGoods).ThenInclude(f => f.Customer)
           .FirstOrDefaultAsync(s => s.Id == settlementId);

    /// <summary>المندوبون الذين لم يُسوَّوا منذ أكثر من يوم ولديهم بضاعة في السيارة أو نقد في المحفظة.</summary>
    public async Task<List<OverdueRepRow>> GetOverdueAsync(DateTime today)
    {
        var vans = await _db.Warehouses.AsNoTracking().Where(w => w.IsActive && w.WarehouseType == WarehouseType.RepVan && w.OwnerEmployeeId != null)
            .Select(w => new { w.Id, w.Name, RepId = w.OwnerEmployeeId!.Value, RepName = w.OwnerEmployee!.FullName }).ToListAsync();
        var rows = new List<OverdueRepRow>();
        foreach (var v in vans)
        {
            var pieces = await _db.StockTransactions.Where(t => t.WarehouseId == v.Id).SumAsync(t => (decimal?)t.QuantityBaseUnits) ?? 0;
            var wallet = await _db.RepWalletTransactions.Where(w => w.EmployeeId == v.RepId).SumAsync(w => (decimal?)(w.AmountIn - w.AmountOut)) ?? 0;
            if (pieces <= 0 && wallet <= 0) continue;
            var last = await _db.RepSettlements.Where(s => s.RepEmployeeId == v.RepId).MaxAsync(s => (DateTime?)s.SettlementDate);
            // بلا تسوية سابقة: من أول حركة في السيارة أو المحفظة
            var since = last ?? (await _db.StockTransactions.Where(t => t.WarehouseId == v.Id).MinAsync(t => (DateTime?)t.TransactionDate))?.ToLocalTime().Date
                             ?? (await _db.RepWalletTransactions.Where(w => w.EmployeeId == v.RepId).MinAsync(w => (DateTime?)w.TransactionDate))?.Date
                             ?? today.Date;
            var days = (today.Date - since.Date).Days;
            if (days <= OverdueAfterDays) continue;
            rows.Add(new OverdueRepRow { RepEmployeeId = v.RepId, RepName = v.RepName, VanName = v.Name, LastSettlement = last, DaysOverdue = days, VanPieces = pieces, WalletBalance = wallet });
        }
        return rows.OrderByDescending(r => r.DaysOverdue).ToList();
    }
}
