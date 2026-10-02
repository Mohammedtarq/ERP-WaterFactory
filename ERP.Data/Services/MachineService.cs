using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>
/// سطر "تحت التصنيع" لماكينة ومادة أولية خلال فترة:
/// المرحّل من الفترة السابقة + المصروف − المُرجَع − المستهلك فعليًا − التالف = المتبقي الحالي.
/// أي حركة أخرى (تعديل يدوي مثلًا) تظهر في "أخرى"، وإن لم تتطابق المعادلة يُرفع تنبيه.
/// </summary>
public class MachineWipRow
{
    public int MachineId { get; init; }
    public string MachineName { get; init; } = "";
    public string MachineType { get; init; } = "";
    public string? ProductionLine { get; init; }
    public int RawItemId { get; init; }
    public string RawItemName { get; init; } = "";
    public string ProductsText { get; init; } = "";
    public decimal CarriedOver { get; init; }
    public decimal Issued { get; init; }
    public decimal Returned { get; init; }
    public decimal Consumed { get; init; }
    public decimal Damaged { get; init; }
    public decimal Other { get; init; }
    public decimal Remaining { get; init; }
    public decimal Expected => CarriedOver + Issued - Returned - Consumed - Damaged;
    /// <summary>المطابقة: المصروف (مع المرحّل) = المستهلك + التالف + المُرجَع + المتبقي.</summary>
    public bool IsReconciled => Expected == Remaining;
    public decimal Difference => Remaining - Expected;
}

/// <summary>مطابقة مادة واحدة في أمر إنتاج: المطلوب، المصروف، المستهلك، التالف، والمتبقي المنسوب للأمر.</summary>
public class OrderMaterialRow
{
    public int RawItemId { get; init; }
    public string RawItemName { get; init; } = "";
    public decimal Required { get; init; }
    public decimal Issued { get; init; }
    public decimal Returned { get; init; }
    public decimal Consumed { get; init; }
    public decimal Damaged { get; init; }
    public decimal Remaining => Issued - Returned - Consumed - Damaged;
    /// <summary>الأصناف التي تستخدم المادة في الأمر؛ أكثر من صنف = مادة مشتركة مجمَّعة.</summary>
    public string UsedBy { get; init; } = "";
    public bool IsShared { get; init; }
}

/// <summary>
/// الماكينات ورصيد تحت التصنيع: لكل ماكينة مخزن داخلي (WorkInProcess) تُصرف إليه المواد لأوامر الإنتاج،
/// ويُستهلك منه بقدر الإنتاج الفعلي، ويُخصم منه التالف، ويبقى المتبقي مرحّلًا على الماكينة.
/// </summary>
public class MachineService
{
    public const string MachinesTable = "Machines";
    private readonly ProjectDbContext _db;

    public MachineService(ProjectDbContext db) => _db = db;

    public static string WipWarehouseName(string machineName) => $"تحت التصنيع — {machineName}";

    public Task<List<Machine>> GetAllAsync(bool activeOnly = false) =>
        _db.Machines.AsNoTracking().Where(m => !activeOnly || m.IsActive).OrderBy(m => m.Name).ToListAsync();

    /// <summary>إضافة/تعديل ماكينة؛ الإضافة تُنشئ مخزن تحت التصنيع الخاص بها، وتغيير الاسم يعيد تسميته.</summary>
    public async Task<(FinanceOperationResult result, int? machineId)> SaveAsync(int? id, string name, string machineType, string? line, string? notes, bool isActive)
    {
        name = (name ?? "").Trim();
        machineType = (machineType ?? "").Trim();
        if (name.Length == 0) return (FinanceOperationResult.Fail("أدخل اسم الماكينة"), null);
        if (machineType.Length == 0) return (FinanceOperationResult.Fail("أدخل نوع الماكينة (نفخ، تعبئة، تغليف...)"), null);
        if (await _db.Machines.AnyAsync(m => m.Name == name && m.Id != (id ?? 0)))
            return (FinanceOperationResult.Fail("يوجد ماكينة بنفس الاسم"), null);

        Machine machine;
        if (id is int existing)
            machine = await _db.Machines.FirstOrDefaultAsync(m => m.Id == existing) ?? throw new InvalidOperationException("الماكينة غير موجودة");
        else
            machine = new Machine();
        machine.Name = name;
        machine.MachineType = machineType;
        machine.ProductionLine = string.IsNullOrWhiteSpace(line) ? null : line.Trim();
        machine.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        machine.IsActive = isActive;
        var error = await EnsureWipWarehouseAsync(_db, machine);
        if (error is not null) return (FinanceOperationResult.Fail(error), null);
        if (id is null) _db.Machines.Add(machine);   // بعد إنشاء مخزنه حتى يكون المفتاح الأجنبي جاهزًا
        await _db.SaveChangesAsync();
        return (FinanceOperationResult.Ok(), machine.Id);
    }

    /// <summary>
    /// ينشئ مخزن "تحت التصنيع" للماكينة الجديدة (مرة واحدة) أو يعيد تسميته مع اسمها. يبقى فعّالًا دائمًا
    /// ليحمل الرصيد المرحّل حتى لو أُوقفت الماكينة.
    /// </summary>
    public static async Task<string?> EnsureWipWarehouseAsync(ProjectDbContext db, Machine machine)
    {
        Warehouse? wip = machine.WipWarehouseId == 0 ? null : await db.Warehouses.FindAsync(machine.WipWarehouseId);
        if (wip is null)
        {
            var branchId = await db.Warehouses.OrderBy(w => w.Id).Select(w => (int?)w.BranchId).FirstOrDefaultAsync()
                           ?? await db.Branches.OrderBy(b => b.Id).Select(b => (int?)b.Id).FirstOrDefaultAsync();
            if (branchId is null) return "أضف فرعًا أولًا من إعدادات النظام";
            wip = new Warehouse { BranchId = branchId.Value, WarehouseType = WarehouseType.WorkInProcess, IsSellableStock = false };
            db.Warehouses.Add(wip);
        }
        wip.Name = WipWarehouseName(machine.Name.Trim());
        wip.IsActive = true;
        await db.SaveChangesAsync();
        machine.WipWarehouseId = wip.Id;
        return null;
    }

    /// <summary>رصيد تحت التصنيع الحالي لماكينة لكل مادة.</summary>
    public async Task<Dictionary<int, decimal>> BalancesAsync(int machineId)
    {
        var wip = await _db.Machines.Where(m => m.Id == machineId).Select(m => m.WipWarehouseId).FirstAsync();
        return await _db.StockTransactions.Where(t => t.WarehouseId == wip)
            .GroupBy(t => t.ItemId).Select(g => new { g.Key, Qty = g.Sum(t => t.QuantityBaseUnits) })
            .Where(x => x.Qty != 0).ToDictionaryAsync(x => x.Key, x => x.Qty);
    }

    /// <summary>
    /// عرض كل ماكينة (أو ماكينة واحدة) خلال فترة: نوعها، المادة، المنتج، المصروف، المستهلك فعليًا، التالف،
    /// المتبقي الحالي، والمرحّل من الفترة السابقة — مع فحص المطابقة.
    /// </summary>
    public async Task<List<MachineWipRow>> GetWipSummaryAsync(DateTime from, DateTime to, int? machineId = null)
    {
        var fromUtc = from.Date.ToUniversalTime();
        var toUtc = to.Date.AddDays(1).ToUniversalTime();
        var machines = await _db.Machines.AsNoTracking().Where(m => machineId == null || m.Id == machineId).OrderBy(m => m.Name).ToListAsync();
        var wipIds = machines.Select(m => m.WipWarehouseId).ToList();

        var moves = await _db.StockTransactions.AsNoTracking()
            .Where(t => wipIds.Contains(t.WarehouseId) && t.TransactionDate < toUtc)
            .Select(t => new { t.WarehouseId, t.ItemId, t.QuantityBaseUnits, t.TransactionType, t.TransactionDate, t.ReferenceTable, t.ReferenceId })
            .ToListAsync();
        var itemIds = moves.Select(m => m.ItemId).Distinct().ToList();
        var names = await _db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.ItemName);

        // المنتج: المنتجات النهائية لأوامر الإنتاج التي استهلكت المادة على الماكينة في الفترة
        var orderIds = moves.Where(m => m.ReferenceTable == "ProductionOrders" && m.ReferenceId != null && m.TransactionDate >= fromUtc)
                            .Select(m => m.ReferenceId!.Value).Distinct().ToList();
        var orderProducts = await _db.ProductionOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id))
            .Select(o => new { o.Id, o.FinishedItem.ItemName }).ToDictionaryAsync(o => o.Id, o => o.ItemName);

        var rows = new List<MachineWipRow>();
        foreach (var m in machines)
        {
            foreach (var g in moves.Where(x => x.WarehouseId == m.WipWarehouseId).GroupBy(x => x.ItemId).OrderBy(g => names.GetValueOrDefault(g.Key)))
            {
                var period = g.Where(x => x.TransactionDate >= fromUtc).ToList();
                decimal Sum(StockTransactionType t, int sign) => period.Where(x => x.TransactionType == t).Sum(x => x.QuantityBaseUnits) * sign;
                var issued = period.Where(x => x.TransactionType == StockTransactionType.WipIssue && x.QuantityBaseUnits > 0).Sum(x => x.QuantityBaseUnits);
                var returned = Sum(StockTransactionType.WipReturn, -1);
                var consumed = Sum(StockTransactionType.ProductionConsume, -1);
                var damaged = Sum(StockTransactionType.Damaged, -1);
                var known = new[] { StockTransactionType.WipIssue, StockTransactionType.WipReturn, StockTransactionType.ProductionConsume, StockTransactionType.Damaged };
                var other = period.Where(x => !known.Contains(x.TransactionType)).Sum(x => x.QuantityBaseUnits);
                var carried = g.Where(x => x.TransactionDate < fromUtc).Sum(x => x.QuantityBaseUnits);
                var remaining = g.Sum(x => x.QuantityBaseUnits);
                if (carried == 0 && period.Count == 0) continue;
                rows.Add(new MachineWipRow
                {
                    MachineId = m.Id, MachineName = m.Name, MachineType = m.MachineType, ProductionLine = m.ProductionLine,
                    RawItemId = g.Key, RawItemName = names.GetValueOrDefault(g.Key, ""),
                    ProductsText = string.Join("، ", period.Where(x => x.ReferenceTable == "ProductionOrders" && x.ReferenceId != null)
                                                            .Select(x => orderProducts.GetValueOrDefault(x.ReferenceId!.Value))
                                                            .Where(n => n != null).Distinct()),
                    CarriedOver = carried, Issued = issued, Returned = returned, Consumed = consumed, Damaged = damaged,
                    Other = other, Remaining = remaining
                });
            }
        }
        return rows;
    }

    /// <summary>مطابقة مواد أمر إنتاج: ما صُرف له، وما استُهلك منه فعليًا، وما تلف، والمتبقي المنسوب له.</summary>
    public async Task<List<OrderMaterialRow>> GetOrderMaterialsAsync(int orderId)
    {
        var order = await _db.ProductionOrders.AsNoTracking().Include(o => o.Consumptions).ThenInclude(c => c.RawMaterialItem)
            .Include(o => o.Lines).ThenInclude(l => l.FinishedItem).Include(o => o.Machine).FirstAsync(o => o.Id == orderId);
        var wip = order.Machine?.WipWarehouseId;
        var moves = await _db.StockTransactions.AsNoTracking()
            .Where(t => t.ReferenceTable == "ProductionOrders" && t.ReferenceId == orderId && (wip == null || t.WarehouseId == wip))
            .Select(t => new { t.ItemId, t.QuantityBaseUnits, t.TransactionType }).ToListAsync();
        var itemOf = order.Lines.ToDictionary(l => l.Id, l => l.FinishedItem.ItemName);
        // المواد المشتركة بين الأصناف تُجمَّع في سطر واحد، مع بيان الأصناف المستخدمة لها
        return order.Consumptions.GroupBy(c => c.RawMaterialItemId).OrderBy(g => g.First().RawMaterialItem.ItemName).Select(g =>
        {
            var mine = moves.Where(x => x.ItemId == g.Key).ToList();
            decimal Of(StockTransactionType t) => Math.Abs(mine.Where(x => x.TransactionType == t).Sum(x => x.QuantityBaseUnits));
            var users = g.Where(c => c.ProductionOrderLineId is int id && itemOf.ContainsKey(id)).Select(c => itemOf[c.ProductionOrderLineId!.Value]).Distinct().ToList();
            var consumed = g.Sum(c => c.QuantityConsumed);
            return new OrderMaterialRow
            {
                RawItemId = g.Key, RawItemName = g.First().RawMaterialItem.ItemName, Required = g.Sum(c => c.QuantityRequired),
                // الأوامر القديمة (بلا ماكينة) صُرفت واستُهلكت دفعة واحدة عند البدء
                Issued = wip is null ? consumed : Of(StockTransactionType.WipIssue),
                Returned = wip is null ? 0 : Of(StockTransactionType.WipReturn),
                Consumed = wip is null ? consumed : Of(StockTransactionType.ProductionConsume),
                Damaged = wip is null ? 0 : Of(StockTransactionType.Damaged),
                UsedBy = string.Join("، ", users), IsShared = users.Count > 1
            };
        }).ToList();
    }

    /// <summary>تالف إنتاج: يُخصم من رصيد تحت التصنيع للماكينة (سبب التلف: أثناء الإنتاج).</summary>
    public async Task<FinanceOperationResult> RecordDamageAsync(int machineId, int rawItemId, decimal quantity, int? orderId, int userId)
    {
        var machine = await _db.Machines.FirstOrDefaultAsync(m => m.Id == machineId);
        if (machine is null) return FinanceOperationResult.Fail("اختر الماكينة");
        if (quantity <= 0) return FinanceOperationResult.Fail("كمية التالف يجب أن تكون أكبر من صفر");
        var (alloc, error) = await LedgerHelper.AllocateAsync(_db, rawItemId, machine.WipWarehouseId, null, quantity);
        if (error is not null) return FinanceOperationResult.Fail(error.Replace("الرصيد غير كافٍ", $"رصيد تحت التصنيع في {machine.Name} غير كافٍ"));
        foreach (var (batchId, qty) in alloc)
            _db.StockTransactions.Add(new StockTransaction
            {
                ItemId = rawItemId, WarehouseId = machine.WipWarehouseId, BatchId = batchId, QuantityBaseUnits = -qty,
                TransactionType = StockTransactionType.Damaged, DamageReason = DamageReason.Production,
                ReferenceTable = orderId is null ? MachinesTable : "ProductionOrders", ReferenceId = orderId ?? machineId, CreatedByUserId = userId
            });
        await _db.SaveChangesAsync();
        return FinanceOperationResult.Ok();
    }

    /// <summary>إرجاع متبقٍّ من تحت التصنيع إلى مخزن مواد أولية.</summary>
    public async Task<FinanceOperationResult> ReturnToWarehouseAsync(int machineId, int rawItemId, decimal quantity, int warehouseId, int userId)
    {
        var machine = await _db.Machines.FirstOrDefaultAsync(m => m.Id == machineId);
        if (machine is null) return FinanceOperationResult.Fail("اختر الماكينة");
        var target = await _db.Warehouses.FindAsync(warehouseId);
        if (target is null || !target.IsActive || target.WarehouseType is WarehouseType.WorkInProcess or WarehouseType.RepVan)
            return FinanceOperationResult.Fail("اختر مخزن المواد الأولية المستلم");
        if (quantity <= 0) return FinanceOperationResult.Fail("الكمية يجب أن تكون أكبر من صفر");
        await using var tx = await _db.Database.BeginTransactionAsync();
        var (alloc, error) = await LedgerHelper.AllocateAsync(_db, rawItemId, machine.WipWarehouseId, null, quantity);
        if (error is not null) return FinanceOperationResult.Fail(error.Replace("الرصيد غير كافٍ", $"رصيد تحت التصنيع في {machine.Name} غير كافٍ"));
        foreach (var (batchId, qty) in alloc)
        {
            _db.StockTransactions.Add(new StockTransaction { ItemId = rawItemId, WarehouseId = machine.WipWarehouseId, BatchId = batchId, QuantityBaseUnits = -qty,
                                                             TransactionType = StockTransactionType.WipReturn, ReferenceTable = MachinesTable, ReferenceId = machineId, CreatedByUserId = userId });
            _db.StockTransactions.Add(new StockTransaction { ItemId = rawItemId, WarehouseId = target.Id, BatchId = batchId, QuantityBaseUnits = qty,
                                                             TransactionType = StockTransactionType.WipReturn, ReferenceTable = MachinesTable, ReferenceId = machineId, CreatedByUserId = userId });
        }
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return FinanceOperationResult.Ok();
    }
}
