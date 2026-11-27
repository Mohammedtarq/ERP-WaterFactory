using ERP.Cloud.Contracts;
using System.Text.Json;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>
/// مرتجع الزبون: بسعر آخر بيع له ولا يتجاوز ما اشتراه؛ السليم للمخزن والتالف لمخزن التالف؛ خصم من الدين (سند «مرتجع» في كشفه
/// وتوزيع دفعاته، وقيد إيرادات/عملاء) أو رد نقدي من محفظة المندوب (من التطبيق بعد الاعتماد)؛ ويُطرح من الحسابات الختامية.
/// </summary>
[Collection("controls")]
public class CustomerReturnTests
{
    private readonly ControlsFixture _f;
    public CustomerReturnTests(ControlsFixture f) => _f = f;

    [Fact]
    public async Task Return_credits_debt_or_refunds_cash_moves_stock_and_reverses_revenue()
    {
        await using var db = _f.NewDb(_f.AdminId);
        var fg = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.FinishedGoods);
        var damaged = await db.Warehouses.FirstOrDefaultAsync(w => w.WarehouseType == WarehouseType.Damaged && w.IsActive);
        if (damaged is null) db.Warehouses.Add(damaged = new Warehouse { BranchId = fg.BranchId, Name = "مخزن التالف — مرتجع", WarehouseType = WarehouseType.Damaged, IsSellableStock = false });
        var water = new Item { ItemCode = "CR-S20", ItemName = "ماء مرتجع شرنك", SalePrice = 250, CostPrice = 100 };
        var customer = new Customer { Name = "زبون المرتجع" };
        var rep = new Employee { FullName = "مندوب المرتجع", IsSalesRep = true, BaseSalary = 500_000 };
        db.AddRange(water, customer, rep);
        await db.SaveChangesAsync();
        var shrink = new ItemPackagingLevel { ItemId = water.Id, LevelName = "شرنك", EquivalentBaseUnits = 20 };
        db.ItemPackagingLevels.AddRange(new ItemPackagingLevel { ItemId = water.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 }, shrink);
        db.StockTransactions.Add(new StockTransaction { ItemId = water.Id, WarehouseId = fg.Id, QuantityBaseUnits = 1_000, UnitCost = 100,
                                                        TransactionType = StockTransactionType.Receipt, CreatedByUserId = _f.AdminId });
        var van = new Warehouse { BranchId = fg.BranchId, Name = "سيارة المرتجع", WarehouseType = WarehouseType.RepVan, OwnerEmployeeId = rep.Id };
        db.Warehouses.Add(van);
        await db.SaveChangesAsync();

        async Task<decimal> Stock(int warehouseId) =>
            await db.StockTransactions.Where(t => t.ItemId == water.Id && t.WarehouseId == warehouseId).SumAsync(t => (decimal?)t.QuantityBaseUnits) ?? 0;
        async Task<decimal> Balance() =>
            await db.Database.SqlQueryRaw<decimal>("SELECT ISNULL((SELECT Balance FROM vw_CustomerBalances WHERE CustomerId = {0}), 0) AS Value", customer.Id).FirstAsync();

        // بيع آجل 5 شرنك = 25,000
        var sales = new SalesService(db);
        var (_, invoiceId) = await sales.CreateInvoiceAsync(new SalesInvoiceHeaderInput(customer.Id, fg.Id, DateTime.Today, InvoicePaymentMethod.Credit), _f.AdminId);
        Assert.True((await sales.AddLineAsync(invoiceId!.Value, new SalesInvoiceLineInput(water.Id, shrink.Id, 5), _f.AdminId)).Success);
        Assert.True((await sales.PostInvoiceAsync(invoiceId.Value, _f.AdminId)).result.Success);
        Assert.Equal(25_000m, await Balance());
        var fgAfterSale = await Stock(fg.Id);
        var damagedBefore = await Stock(damaged.Id);

        var svc = new CustomerReturnService(db);
        // 1) مرتجع 2 شرنك منها 1 تالف، خصم من الدين
        var (ok, ret) = await svc.CreateAsync(new CustomerReturnRequest(customer.Id, fg.Id, DateTime.Today, CustomerReturnSettlement.Debt, "منتهي الشكل",
            new[] { new CustomerReturnLineInput(water.Id, shrink.Id, 2, 1) }, _f.AdminId));
        Assert.True(ok.Success, ok.ErrorMessage);
        Assert.Equal(10_000m, ret!.TotalAmount);                                   // بسعر آخر بيع: 5,000 للشرنك
        Assert.Equal(15_000m, await Balance());
        Assert.Equal(fgAfterSale + 20, await Stock(fg.Id));                       // السليم
        Assert.Equal(damagedBefore + 20, await Stock(damaged.Id));                 // التالف
        var voucher = await db.Vouchers.AsNoTracking().SingleAsync(v => v.Id == ret.VoucherId);
        Assert.Equal(PaymentMethod.Return, voucher.PaymentMethod);
        var lines = await db.JournalEntryLines.AsNoTracking().Where(l => l.JournalEntryId == ret.JournalEntryId).Select(l => new { l.Account.AccountCode, l.Debit, l.Credit }).ToListAsync();
        Assert.Contains(lines, l => l.AccountCode == "4101" && l.Debit == 10_000);
        Assert.Contains(lines, l => l.AccountCode == "1201" && l.Credit == 10_000);
        Assert.Equal(10_000m, (await db.SalesInvoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId)).AmountSettled);   // توزيع الدفعات
        var statement = await db.Database.SqlQueryRaw<string>("SELECT Description AS Value FROM vw_CustomerStatement WHERE CustomerId = {0} AND TxType = N'CustomerReturn'", customer.Id).ToListAsync();
        Assert.Contains(ret.ReturnNumber, Assert.Single(statement));

        // 2) لا يتجاوز ما اشتراه ولم يُرجعه، والرد النقدي يحتاج سيارة مندوب، وسند المرتجع لا يُلغى من السندات
        Assert.Contains("أكثر مما اشتراه", (await svc.CreateAsync(new CustomerReturnRequest(customer.Id, fg.Id, DateTime.Today, CustomerReturnSettlement.Debt, "س",
            new[] { new CustomerReturnLineInput(water.Id, shrink.Id, 4) }, _f.AdminId))).result.ErrorMessage);
        Assert.Contains("محفظة المندوب", (await svc.CreateAsync(new CustomerReturnRequest(customer.Id, fg.Id, DateTime.Today, CustomerReturnSettlement.Cash, "س",
            new[] { new CustomerReturnLineInput(water.Id, shrink.Id, 1) }, _f.AdminId))).result.ErrorMessage);
        Assert.Contains("مرتجع بضاعة", (await new FinanceService(db).VoidVoucherAsync(voucher.Id, "تجربة", _f.AdminId)).ErrorMessage);

        // 3) من التطبيق: بيع نقدي من السيارة ثم مرتجع برد نقدي — بانتظار الاعتماد ويمنع التسوية، ثم الاعتماد
        var ops = new RepOperationsService(db);
        var (_, order) = await ops.CreateLoadOrderAsync(van.Id, fg.Id, DateTime.Today, new[] { new RepLoadLineInput(water.Id, shrink.Id, 10) }, null, _f.AdminId);
        Assert.True((await ops.PrepareLoadOrderAsync(order!.Id, null, _f.AdminId)).result.Success);
        var app = new RepAppService(db);
        var (_, key) = await app.RegisterDeviceAsync(rep.Id, "هاتف المرتجع", _f.AdminId);
        RepRequestEnvelope Env(RepRequestKind k, object p) => new(Guid.NewGuid(), k, DateTime.Now, JsonSerializer.Serialize(p));
        Assert.True((await app.ReceiveAsync(key!, Env(RepRequestKind.CashSale, new SalePayload(new CustomerRef(customer.Id), new() { new SaleLinePayload(water.Id, shrink.Id, 3) })))).Accepted);
        var reps = new RepsService(db);
        Assert.Equal(15_000m, await reps.GetWalletBalanceAsync(rep.Id));
        var vanBefore = await Stock(van.Id);
        var balanceBefore = await Balance();

        var request = await app.ReceiveAsync(key!, Env(RepRequestKind.Return, new ReturnPayload(new CustomerRef(customer.Id), new() { new ReturnLinePayload(water.Id, shrink.Id, 1) }, "زائد عن الحاجة", Cash: true)));
        Assert.Equal(RepRequestStatus.Pending, request.Status);
        Assert.Contains("رد نقدي", request.Message);
        Assert.Contains("بانتظار الاعتماد", (await ops.SettleAsync(new RepSettlementRequest(van.Id, fg.Id, DateTime.Today, Array.Empty<StockDocumentLineInput>(),
            Array.Empty<RepFreeLineInput>(), Array.Empty<RepExpenseInput>(), 0, _f.AdminId))).result.ErrorMessage);
        Assert.True((await app.ApproveAsync(request.RequestId!.Value, _f.AdminId)).Success);
        Assert.Equal(10_000m, await reps.GetWalletBalanceAsync(rep.Id));          // رد 5,000 من المحفظة
        Assert.Equal(vanBefore + 20, await Stock(van.Id));                        // السليم عاد للسيارة
        Assert.Equal(balanceBefore, await Balance());                             // الرد النقدي لا يمس الدين
        var approved = await db.RepRequests.AsNoTracking().SingleAsync(x => x.Id == request.RequestId);
        Assert.Equal(("CustomerReturns", RepRequestStatus.Posted), (approved.ResultTable, approved.Status));

        // 4) الحسابات الختامية: الإيراد = 8 شرنك مبيع − 3 مرتجعة = 5 × 5,000
        var month = await new FinalAccountsService(db).MonthAsync(DateTime.Today.Year, DateTime.Today.Month);
        var product = month.Products.Single(p => p.ItemId == water.Id);
        Assert.Equal((25_000m, 100m), (product.Revenue, product.PiecesSold));
        Assert.Equal(2, (await svc.ListAsync(DateTime.Today, DateTime.Today, customer.Id)).Count);
    }
}
