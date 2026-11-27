using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

public record DamagedSaleLineInput(int ItemId, decimal Quantity, decimal UnitPrice);

public class DamagedStockRow
{
    public int ItemId { get; init; }
    public string ItemName { get; init; } = "";
    public string ItemCode { get; init; } = "";
    public string Unit { get; init; } = "";
    public decimal Available { get; init; }
    public string Display => $"{ItemName} ({ItemCode}) — متاح {Available:#,0.###} {Unit}";
}

public class DamagedSaleRow
{
    public int Id { get; init; }
    public string SaleNumber { get; init; } = "";
    public DateTime SaleDate { get; init; }
    public string BuyerName { get; init; } = "";
    public decimal TotalAmount { get; init; }
    public string ItemsText { get; init; } = "";
    public string DocumentNumber { get; init; } = "";
    public string CreatedBy { get; init; } = "";
}

/// <summary>
/// بيع المواد التالفة نقدًا لجهة: إخراج الكميات من مخزن التالف (مستند إخراج يحفظ التشغيلات)،
/// والنقد يدخل الصندوق، والقيد مدين الصندوق / دائن "إيراد بيع مواد تالفة" — كله في عملية واحدة.
/// </summary>
public class DamagedSaleService
{
    public const string CashRule = "DamagedSaleCash";   // مدين الصندوق / دائن إيراد بيع مواد تالفة

    private readonly ProjectDbContext _db;
    public DamagedSaleService(ProjectDbContext db) => _db = db;

    private Task<Warehouse?> DamagedWarehouseAsync() =>
        _db.Warehouses.Where(w => w.WarehouseType == WarehouseType.Damaged && w.IsActive).OrderBy(w => w.Id).FirstOrDefaultAsync();

    /// <summary>ما في مخزن التالف الآن من أصناف (بالوحدة الأساسية).</summary>
    public async Task<List<DamagedStockRow>> GetAvailableAsync()
    {
        var wh = await DamagedWarehouseAsync();
        if (wh is null) return new();
        return await _db.StockTransactions.AsNoTracking().Where(t => t.WarehouseId == wh.Id)
            .GroupBy(t => new { t.ItemId, t.Item.ItemName, t.Item.ItemCode, t.Item.BaseUnitName })
            .Select(g => new DamagedStockRow { ItemId = g.Key.ItemId, ItemName = g.Key.ItemName, ItemCode = g.Key.ItemCode, Unit = g.Key.BaseUnitName,
                                               Available = g.Sum(t => t.QuantityBaseUnits) })
            .Where(r => r.Available > 0).OrderBy(r => r.ItemName).ToListAsync();
    }

    public async Task<(FinanceOperationResult result, DamagedSale? sale)> CreateAsync(string buyerName, DateTime date, IReadOnlyList<DamagedSaleLineInput> lines,
                                                                                        string? notes, int userId, int? cashBoxId = null)
    {
        if (string.IsNullOrWhiteSpace(buyerName)) return Fail("اكتب اسم المشتري (الجهة)");
        if (lines.Count == 0) return Fail("أضف مادة واحدة على الأقل");
        if (lines.Any(l => l.Quantity <= 0)) return Fail("الكمية يجب أن تكون أكبر من صفر في كل سطر");
        if (lines.Any(l => l.UnitPrice < 0)) return Fail("السعر لا يمكن أن يكون سالبًا");
        if (lines.GroupBy(l => l.ItemId).Any(g => g.Count() > 1)) return Fail("المادة مكررة — اجمع كميتها في سطر واحد");
        var total = lines.Sum(l => Math.Round(l.Quantity * l.UnitPrice, 2));
        if (total <= 0) return Fail("إجمالي البيع يجب أن يكون أكبر من صفر");
        var wh = await DamagedWarehouseAsync();
        if (wh is null) return Fail("لا يوجد مخزن تالف مفعّل");

        // كل مادة تُخرج بوحدتها الأساسية (القطعة)
        var itemIds = lines.Select(l => l.ItemId).ToList();
        var baseLevels = await _db.ItemPackagingLevels.Where(l => itemIds.Contains(l.ItemId) && l.ParentLevelId == null)
                                  .GroupBy(l => l.ItemId).Select(g => new { g.Key, Id = g.Min(l => l.Id) }).ToDictionaryAsync(x => x.Key, x => x.Id);
        if (itemIds.Any(id => !baseLevels.ContainsKey(id))) return Fail("مادة بلا وحدة أساسية في هيكلية التعبئة");

        var boxId = cashBoxId ?? await _db.CashBoxes.Where(b => b.IsActive && (b.OwnerUserId == userId || b.IsDefault || b.BoxType == CashBoxType.Main))
                                        .OrderBy(b => b.OwnerUserId == userId ? 0 : b.IsDefault ? 1 : 2).ThenBy(b => b.Id).Select(b => (int?)b.Id).FirstOrDefaultAsync();
        if (boxId is null) return Fail("لا يوجد صندوق مفعّل لاستلام النقد");

        await using var tx = await _db.Database.BeginTransactionAsync();
        buyerName = buyerName.Trim();
        var (docResult, doc) = await new WarehouseDocumentService(_db).CreateAsync(new StockDocumentRequest(
            StockDocumentType.Issue, wh.Id, date, lines.Select(l => new StockDocumentLineInput(l.ItemId, baseLevels[l.ItemId], l.Quantity)).ToList(), userId,
            PartyName: $"بيع مواد تالفة: {buyerName}", Notes: notes));
        if (!docResult.Success) return Fail(docResult.ErrorMessage ?? "تعذّر إخراج المواد من مخزن التالف");

        var n = (await _db.Database.SqlQueryRaw<int>("SELECT NEXT VALUE FOR seq_DamagedSale AS [Value]").ToListAsync())[0];
        var number = $"DS-{date.Year}-{n:D5}";
        var text = $"بيع مواد تالفة {number} — {buyerName}";
        var (entry, error) = await LedgerHelper.PostJournalAsync(_db, CashRule, total, date, JournalEntryType.AutoSales, text, userId, "DamagedSales", null, "DS");
        if (error is not null) return Fail(error);
        await _db.SaveChangesAsync();

        var sale = new DamagedSale
        {
            SaleNumber = number, SaleDate = date.Date, BuyerName = buyerName, StockDocumentId = doc!.Id, TotalAmount = total,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(), JournalEntryId = entry!.Id, CreatedByUserId = userId
        };
        foreach (var l in lines)
            sale.Lines.Add(new DamagedSaleLine { ItemId = l.ItemId, Quantity = l.Quantity, UnitPrice = l.UnitPrice, Amount = Math.Round(l.Quantity * l.UnitPrice, 2) });
        _db.DamagedSales.Add(sale);
        await _db.SaveChangesAsync();
        entry.SourceId = sale.Id;
        await _db.SaveChangesAsync();
        await new CashBoxService(_db).RecordAutoAsync(userId, CashBoxTxType.SalesReceipt, total, date, "DamagedSales", sale.Id, buyerName, text, entry.Id, boxId);
        await tx.CommitAsync();
        return (FinanceOperationResult.Ok(), sale);
    }

    public async Task<List<DamagedSaleRow>> GetListAsync(DateTime from, DateTime to)
    {
        var raw = await _db.DamagedSales.AsNoTracking().Where(s => s.SaleDate >= from.Date && s.SaleDate <= to.Date)
            .OrderByDescending(s => s.SaleDate).ThenByDescending(s => s.Id)
            .Select(s => new
            {
                s.Id, s.SaleNumber, s.SaleDate, s.BuyerName, s.TotalAmount, Doc = s.StockDocument.DocumentNumber, User = s.CreatedByUser.Username,
                Items = s.Lines.Select(l => l.Item.ItemName + " × " + l.Quantity).ToList()
            }).ToListAsync();
        return raw.Select(s => new DamagedSaleRow
        {
            Id = s.Id, SaleNumber = s.SaleNumber, SaleDate = s.SaleDate, BuyerName = s.BuyerName, TotalAmount = s.TotalAmount,
            DocumentNumber = s.Doc, CreatedBy = s.User, ItemsText = string.Join("، ", s.Items)
        }).ToList();
    }

    public Task<DamagedSale?> GetAsync(int id) =>
        _db.DamagedSales.AsNoTracking().Include(s => s.Lines).ThenInclude(l => l.Item).Include(s => s.StockDocument).Include(s => s.CreatedByUser)
           .FirstOrDefaultAsync(s => s.Id == id);

    private static (FinanceOperationResult, DamagedSale?) Fail(string m) => (FinanceOperationResult.Fail(m), null);
}
