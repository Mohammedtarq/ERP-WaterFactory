using System.Data;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERP.Data.Services;

public record SalesInvoiceHeaderInput(
    int CustomerId, int WarehouseId, DateTime InvoiceDate, InvoicePaymentMethod PaymentMethod,
    decimal AmountPaidNow = 0, bool TaxEnabled = false, decimal TaxRate = 14,
    bool LoadingSuppliesEnabled = false, bool? IsAgentPricing = null,
    bool IsFreeSale = false, string? FreeSaleRecipient = null,
    int? SalesRepEmployeeId = null, string? Notes = null);

/// <param name="UnitPrice">NULL = يُحسب تلقائيًا بالتسعير الهرمي.</param>
/// <param name="BatchId">NULL = تُختار التشغيلات تلقائيًا (FIFO) وقت الترحيل.</param>
/// <param name="CustomRecipeId">متغير مطلوب بالاسم (مطعم، مناسبة). NULL = الأساسي ومحجوز العميل نفسه.</param>
public record SalesInvoiceLineInput(
    int ItemId, int PackagingLevelId, decimal QuantityInLevel,
    decimal? UnitPrice = null, int? BatchId = null, int? CustomRecipeId = null);

public class SalesPostingSummary
{
    /// <summary>ما سُلّم من نقد المندوب للصندوق فورًا عند الترحيل.</summary>
    public decimal HandedOverToBox { get; set; }
    public string? HandoverError { get; set; }
    public int InvoiceId { get; init; }
    public string InvoiceNumber { get; init; } = string.Empty;
    public decimal SubTotal { get; init; }
    public decimal TaxAmount { get; init; }
    public decimal LoadingSuppliesAmount { get; init; }
    public decimal TotalAmount { get; init; }
    public decimal AmountPaidNow { get; init; }
    public decimal AmountDue { get; init; }
    public int? JournalEntryId { get; init; }
}

public class SalesInvoiceListRow
{
    public int Id { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public DateTime InvoiceDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public int CustomerId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerType { get; set; } = string.Empty;
    public string WarehouseName { get; set; } = string.Empty;
    public string? SalesRepName { get; set; }
    public string PaymentMethod { get; set; } = string.Empty;
    public bool IsFreeSale { get; set; }
    public string? FreeSaleRecipient { get; set; }
    public decimal SubTotal { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal LoadingSuppliesAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal AmountPaidNow { get; set; }
    public decimal? AmountDue { get; set; }
}

public class CustomerBalanceRow
{
    public int CustomerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string CustomerType { get; set; } = string.Empty;
    public decimal TotalDebit { get; set; }
    public decimal TotalCredit { get; set; }
    public decimal Balance { get; set; }       // موجب = على العميل
}

public class CustomerStatementRow
{
    public DateTime TxDate { get; set; }
    public string TxType { get; set; } = string.Empty;
    public string DocNumber { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public decimal RunningBalance { get; set; }
}

/// <summary>
/// وحدة المبيعات. كل قواعد العمل (التسعير الهرمي، FIFO، القيد، محفظة المندوب،
/// الصلاحيات) مطبّقة داخل قاعدة البيانات في 09_sales_logic.sql — هذه الخدمة
/// تستدعي تلك الإجراءات وتحوّل أخطاءها إلى رسائل عربية جاهزة للعرض، بحيث لا
/// يمكن لأي واجهة (سطح مكتب أو تطبيق مندوب مستقبلًا) تجاوز القواعد.
/// </summary>
public class SalesService
{
    // أرقام الأخطاء التي يرفعها 09_sales_logic.sql (رسائلها عربية أصلًا)
    private const int BusinessErrorMin = 51000, BusinessErrorMax = 51199;

    private readonly ProjectDbContext _db;

    public SalesService(ProjectDbContext db)
    {
        _db = db;
    }

    // ====================== التسعير ======================

    /// <summary>السعر المقترح لوحدة البيع المختارة (كارتون/شرنك/قطعة) — للعرض الفوري في السطر.</summary>
    public async Task<decimal> GetSuggestedUnitPriceAsync(int customerId, int itemId, int packagingLevelId, bool useAgentPricing)
    {
        var baseUnits = await _db.ItemPackagingLevels
            .Where(p => p.Id == packagingLevelId && p.ItemId == itemId)
            .Select(p => (decimal?)p.EquivalentBaseUnits)
            .FirstOrDefaultAsync() ?? 0;

        var piecePrice = await ScalarAsync<decimal?>(
            "SELECT dbo.fn_Sales_BaseUnitPrice(@c, @i, @a)",
            P("@c", customerId), P("@i", itemId), P("@a", useAgentPricing)) ?? 0;

        return Math.Round(piecePrice * baseUnits, 2);
    }

    /// <summary>سعر مستلزمات التحميل للقطعة الساري في تاريخ الفاتورة.</summary>
    public async Task<decimal> GetLoadingRateAsync(DateTime onDate)
        => await ScalarAsync<decimal?>("SELECT dbo.fn_Sales_LoadingRate(@d)", P("@d", onDate.Date)) ?? 0;

    /// <summary>الرصيد المتاح للبيع (بالقطعة) لصنف في مخزن، اختياريًا لتشغيلة محددة.</summary>
    /// <param name="scope">قاعدة المتغيرات (العميل، المتغير المطلوب، صلاحية المحجوز)؛ NULL = كل الرصيد.</param>
    public Task<decimal> GetAvailableQuantityAsync(int itemId, int warehouseId, int? batchId = null, BatchScope? scope = null)
        => scope is not null ? LedgerHelper.AvailableAsync(_db, itemId, warehouseId, batchId, scope)
           : _db.StockTransactions
            .Where(t => t.ItemId == itemId && t.WarehouseId == warehouseId && (batchId == null || t.BatchId == batchId))
            .SumAsync(t => t.QuantityBaseUnits);

    // ====================== الفاتورة ======================

    public async Task<(FinanceOperationResult result, int? invoiceId)> CreateInvoiceAsync(
        SalesInvoiceHeaderInput h, int userId)
    {
        var newId = new SqlParameter("@NewInvoiceId", SqlDbType.Int) { Direction = ParameterDirection.Output };
        var result = await ExecAsync("sp_Sales_CreateInvoice",
            P("@CustomerId", h.CustomerId), P("@WarehouseId", h.WarehouseId),
            P("@InvoiceDate", h.InvoiceDate.Date), P("@PaymentMethod", h.PaymentMethod.ToString()),
            P("@AmountPaidNow", h.AmountPaidNow), P("@TaxEnabled", h.TaxEnabled), P("@TaxRate", h.TaxRate),
            P("@LoadingSuppliesEnabled", h.LoadingSuppliesEnabled), P("@IsAgentPricing", h.IsAgentPricing),
            P("@IsFreeSale", h.IsFreeSale), P("@FreeSaleRecipient", h.FreeSaleRecipient),
            P("@SalesRepEmployeeId", h.SalesRepEmployeeId), P("@Notes", h.Notes),
            P("@UserId", userId), newId);

        return (result, result.Success ? (int)newId.Value : null);
    }

    public async Task<FinanceOperationResult> UpdateDraftHeaderAsync(int invoiceId, SalesInvoiceHeaderInput h, int userId)
        => await ExecAsync("sp_Sales_UpdateDraftHeader",
            P("@InvoiceId", invoiceId),
            P("@CustomerId", h.CustomerId), P("@WarehouseId", h.WarehouseId),
            P("@InvoiceDate", h.InvoiceDate.Date), P("@PaymentMethod", h.PaymentMethod.ToString()),
            P("@AmountPaidNow", h.AmountPaidNow), P("@TaxEnabled", h.TaxEnabled), P("@TaxRate", h.TaxRate),
            P("@LoadingSuppliesEnabled", h.LoadingSuppliesEnabled), P("@IsAgentPricing", h.IsAgentPricing),
            P("@IsFreeSale", h.IsFreeSale), P("@FreeSaleRecipient", h.FreeSaleRecipient),
            P("@SalesRepEmployeeId", h.SalesRepEmployeeId), P("@Notes", h.Notes),
            P("@UserId", userId));

    public async Task<FinanceOperationResult> AddLineAsync(int invoiceId, SalesInvoiceLineInput l, int userId)
        => await ExecAsync("sp_Sales_AddInvoiceLine",
            P("@InvoiceId", invoiceId), P("@ItemId", l.ItemId), P("@PackagingLevelId", l.PackagingLevelId),
            P("@QuantityInLevel", l.QuantityInLevel), P("@UnitPrice", l.UnitPrice), P("@BatchId", l.BatchId),
            P("@UserId", userId), P("@CustomRecipeId", l.CustomRecipeId));

    public async Task<FinanceOperationResult> DeleteLineAsync(int lineId, int userId)
        => await ExecAsync("sp_Sales_DeleteInvoiceLine", P("@LineId", lineId), P("@UserId", userId));

    public async Task<FinanceOperationResult> DeleteDraftInvoiceAsync(int invoiceId, int userId)
        => await ExecAsync("sp_Sales_DeleteDraftInvoice", P("@InvoiceId", invoiceId), P("@UserId", userId));

    /// <summary>
    /// الترحيل الذري: إما أن تُخصم الكميات ويُنشأ القيد وتُحدَّث محفظة المندوب
    /// كلها معًا، أو لا يحدث أي شيء وتبقى الفاتورة مسودة مع رسالة سبب واضحة.
    /// </summary>
    /// <param name="handOverRepCashNow">فاتورة من سيارة مندوب: يُسلَّم النقد المقبوض للصندوق فورًا بدل بقائه في محفظة المندوب.</param>
    public async Task<(FinanceOperationResult result, SalesPostingSummary? summary)> PostInvoiceAsync(int invoiceId, int userId, bool handOverRepCashNow = false)
    {
        SalesPostingSummary? summary = null;
        var result = await RunAsync(async cmd =>
        {
            cmd.CommandText = "sp_Sales_PostInvoice";
            cmd.CommandType = CommandType.StoredProcedure;
            cmd.Parameters.Add(P("@InvoiceId", invoiceId));
            cmd.Parameters.Add(P("@UserId", userId));
            await using var r = await cmd.ExecuteReaderAsync();
            if (await r.ReadAsync())
            {
                summary = new SalesPostingSummary
                {
                    InvoiceId = r.GetInt32(r.GetOrdinal("InvoiceId")),
                    InvoiceNumber = r.GetString(r.GetOrdinal("InvoiceNumber")),
                    SubTotal = r.GetDecimal(r.GetOrdinal("SubTotal")),
                    TaxAmount = r.GetDecimal(r.GetOrdinal("TaxAmount")),
                    LoadingSuppliesAmount = r.GetDecimal(r.GetOrdinal("LoadingSuppliesAmount")),
                    TotalAmount = r.GetDecimal(r.GetOrdinal("TotalAmount")),
                    AmountPaidNow = r.GetDecimal(r.GetOrdinal("AmountPaidNow")),
                    AmountDue = r.GetDecimal(r.GetOrdinal("AmountDue")),
                    JournalEntryId = r.IsDBNull(r.GetOrdinal("JournalEntryId")) ? null : r.GetInt32(r.GetOrdinal("JournalEntryId"))
                };
            }
        });
        // فاتورة آجلة/جزئية جديدة ← تأخذ نصيبها من أي رصيد دائن للعميل (الأقدم أولًا)
        if (result.Success)
        {
            if (summary is not null)
                await new AuditService(_db).LogAsync(userId, "Post", "SalesInvoices", invoiceId, $"{summary.InvoiceNumber} — {summary.TotalAmount:N0}");
            var customerId = await _db.SalesInvoices.Where(i => i.Id == invoiceId).Select(i => i.CustomerId).FirstAsync();
            await new CustomerAccountService(_db).SyncAsync(customerId);

            if (handOverRepCashNow && summary is not null)
            {
                var inv = await _db.SalesInvoices.AsNoTracking().Where(i => i.Id == invoiceId)
                    .Select(i => new { i.SalesRepEmployeeId, i.PaymentMethod, i.InvoiceDate }).FirstAsync();
                if (inv.SalesRepEmployeeId is int repId && summary.AmountPaidNow > 0 && inv.PaymentMethod != InvoicePaymentMethod.Electronic)
                {
                    var handover = await new RepsService(_db).RecordCashHandoverAsync(repId, summary.AmountPaidNow, inv.InvoiceDate, userId);
                    if (handover.Success) summary.HandedOverToBox = summary.AmountPaidNow;
                    else summary.HandoverError = handover.ErrorMessage;
                }
            }
        }
        return (result, summary);
    }

    /// <summary>
    /// إلغاء فاتورة مرحّلة بدل حذفها: المخزون يعود، وقيد عكسي، وحركة الصندوق تُلغى، ويُعاد توزيع دفعات العميل.
    /// الفاتورة تبقى في القائمة بحالة "ملغاة" مع السبب.
    /// </summary>
    public async Task<FinanceOperationResult> VoidInvoiceAsync(int invoiceId, string reason, int userId)
    {
        var result = await ExecAsync("sp_Sales_VoidInvoice", P("@InvoiceId", invoiceId), P("@Reason", reason?.Trim()), P("@UserId", userId));
        if (!result.Success) return result;
        var inv = await _db.SalesInvoices.AsNoTracking().Where(i => i.Id == invoiceId)
            .Select(i => new { i.CustomerId, i.InvoiceNumber, i.TotalAmount }).FirstAsync();
        await new CustomerAccountService(_db).SyncAsync(inv.CustomerId);
        await new AuditService(_db).LogAsync(userId, "Void", "SalesInvoices", invoiceId,
            $"{inv.InvoiceNumber} — {inv.TotalAmount:N0} — السبب: {reason?.Trim()}");
        return result;
    }

    public Task<SalesInvoice?> GetInvoiceAsync(int invoiceId)
        => _db.SalesInvoices.AsNoTracking()
            .Include(i => i.Customer)
            .Include(i => i.Warehouse)
            .Include(i => i.SalesRepEmployee)
            .Include(i => i.Lines).ThenInclude(l => l.Item)
            .Include(i => i.Lines).ThenInclude(l => l.PackagingLevel)
            .Include(i => i.Lines).ThenInclude(l => l.Batch).ThenInclude(b => b!.CustomRecipe)
            .Include(i => i.Lines).ThenInclude(l => l.CustomRecipe)
            .FirstOrDefaultAsync(i => i.Id == invoiceId);

    public Task<List<SalesInvoiceListRow>> GetInvoiceListAsync(DateTime? from = null, DateTime? to = null, int? customerId = null)
    {
        var q = _db.Database.SqlQueryRaw<SalesInvoiceListRow>(
            @"SELECT Id, InvoiceNumber, CAST(InvoiceDate AS DATETIME2) AS InvoiceDate, Status, CustomerId, CustomerName, CustomerType,
                     WarehouseName, SalesRepName, PaymentMethod, IsFreeSale, FreeSaleRecipient,
                     SubTotal, TaxAmount, LoadingSuppliesAmount, TotalAmount, AmountPaidNow, AmountDue
              FROM vw_SalesInvoiceList");
        if (from.HasValue) q = q.Where(r => r.InvoiceDate >= from.Value.Date);
        if (to.HasValue) q = q.Where(r => r.InvoiceDate <= to.Value.Date);
        if (customerId.HasValue) q = q.Where(r => r.CustomerId == customerId.Value);
        return q.OrderByDescending(r => r.InvoiceDate).ThenByDescending(r => r.Id).ToListAsync();
    }

    // ====================== العملاء ======================

    public Task<List<CustomerBalanceRow>> GetCustomerBalancesAsync()
        => _db.Database.SqlQueryRaw<CustomerBalanceRow>(
                "SELECT CustomerId, Name, CustomerType, TotalDebit, TotalCredit, Balance FROM vw_CustomerBalances")
            .OrderByDescending(r => r.Balance).ToListAsync();

    public async Task<List<CustomerStatementRow>> GetCustomerStatementAsync(int customerId)
    {
        var rows = await _db.Database.SqlQueryRaw<CustomerStatementRow>(
                @"SELECT CAST(TxDate AS DATETIME2) AS TxDate, TxType, DocNumber, Description, Debit, Credit,
                         CAST(0 AS DECIMAL(18,2)) AS RunningBalance
                  FROM vw_CustomerStatement WHERE CustomerId = {0}", customerId)
            .ToListAsync();

        // ترتيب زمني ثابت: الرصيد الافتتاحي ثم الفاتورة قبل المدفوع عند البيع في نفس اليوم
        decimal running = 0;
        var ordered = rows.OrderBy(r => r.TxDate)
                          .ThenBy(r => r.TxType == "OpeningBalance" ? 0 : r.TxType == "SalesInvoice" ? 1 : 2)
                          .ThenBy(r => r.DocNumber)
                          .ToList();
        foreach (var r in ordered) r.RunningBalance = running += r.Debit - r.Credit;
        return ordered;
    }

    // ====================== أدوات داخلية ======================

    private static SqlParameter P(string name, object? value) => new(name, value ?? DBNull.Value);

    private Task<FinanceOperationResult> ExecAsync(string procedure, params SqlParameter[] parameters)
        => RunAsync(async cmd =>
        {
            cmd.CommandText = procedure;
            cmd.CommandType = CommandType.StoredProcedure;
            cmd.Parameters.AddRange(parameters);
            await cmd.ExecuteNonQueryAsync();
        });

    private async Task<T?> ScalarAsync<T>(string sql, params SqlParameter[] parameters)
    {
        T? value = default;
        var r = await RunAsync(async cmd =>
        {
            cmd.CommandText = sql;
            cmd.Parameters.AddRange(parameters);
            var o = await cmd.ExecuteScalarAsync();
            value = o is null or DBNull ? default : (T)o;
        });
        if (!r.Success) throw new InvalidOperationException(r.ErrorMessage);
        return value;
    }

    private async Task<FinanceOperationResult> RunAsync(Func<SqlCommand, Task> action)
    {
        var conn = (SqlConnection)_db.Database.GetDbConnection();
        bool opened = false;
        if (conn.State != ConnectionState.Open) { await conn.OpenAsync(); opened = true; }
        try
        {
            await using var cmd = conn.CreateCommand();
            if (_db.Database.CurrentTransaction is { } tx) cmd.Transaction = (SqlTransaction)tx.GetDbTransaction();
            await action(cmd);
            return FinanceOperationResult.Ok();
        }
        catch (SqlException ex) when (ex.Number is >= BusinessErrorMin and <= BusinessErrorMax)
        {
            return FinanceOperationResult.Fail(ex.Message);
        }
        finally
        {
            if (opened) await conn.CloseAsync();
        }
    }
}
