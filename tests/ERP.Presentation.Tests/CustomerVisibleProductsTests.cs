using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.ViewModels.Sales;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>
/// «المنتجات الظاهرة عند البيع» لكل زبون: «المخصص له فقط» لا يُظهر في الأصناف وقائمة التشغيلة إلا منتجه (متغيره) الخاص،
/// و«كل المنتجات» (الافتراضي) كما كان. وزر «قائمة الفواتير» داخل الفاتورة يرجع للقائمة ويحدد الفاتورة.
/// </summary>
[Collection("app")]
public class CustomerVisibleProductsTests
{
    private readonly AppFixture _f;
    public CustomerVisibleProductsTests(AppFixture f) => _f = f;

    [Fact]
    public async Task Assigned_only_customer_sees_only_own_product_and_items()
    {
        int waterId, otherId, recA, recB, recGeneral, custA, custB, custAll, custNone;
        await using (var db = _f.NewDb())
        {
            var branch = await db.Branches.FirstAsync();
            var raw = await db.Warehouses.FirstOrDefaultAsync(w => w.WarehouseType == WarehouseType.RawMaterial);
            if (raw is null) db.Warehouses.Add(raw = new Warehouse { BranchId = branch.Id, Name = "مخزن المواد VP", WarehouseType = WarehouseType.RawMaterial, IsSellableStock = false });
            var wip = new Warehouse { BranchId = branch.Id, Name = "تحت التصنيع VP", WarehouseType = WarehouseType.WorkInProcess, IsSellableStock = false };
            db.Warehouses.Add(wip);
            var admin = await db.Users.FirstAsync(u => u.Username == AppFixture.AdminUser);
            var pre = new Item { ItemCode = "VP-PRE", ItemName = "امبولة VP", SourcingMethod = SourcingMethod.Purchased, CostPrice = 10 };
            var water = new Item { ItemCode = "VP-W", ItemName = "ماء VP", SalePrice = 250 };
            db.Items.AddRange(pre, water);
            await db.SaveChangesAsync();
            db.ItemPackagingLevels.Add(new ItemPackagingLevel { ItemId = pre.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 });
            db.StockTransactions.Add(new StockTransaction { ItemId = pre.Id, WarehouseId = raw.Id, QuantityBaseUnits = 5_000, UnitCost = 10, TransactionType = StockTransactionType.Receipt, CreatedByUserId = admin.Id });
            db.ItemPackagingLevels.AddRange(new ItemPackagingLevel { ItemId = water.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 },
                                            new ItemPackagingLevel { ItemId = water.Id, LevelName = "شرنك", ContainsQuantity = 20, EquivalentBaseUnits = 20, IsSellableUnit = true });
            var bom = new BillOfMaterials { FinishedItemId = water.Id };
            bom.Lines.Add(new BOMLine { RawMaterialItemId = pre.Id, QuantityPerUnit = 1, ComponentRole = "امبولة" });
            db.BillOfMaterials.Add(bom);
            var machine = new Machine { Name = "ماكينة VP", MachineType = "تعبئة", WipWarehouseId = wip.Id };

            var a = new Customer { Name = "زبون خاص أ VP", CustomerType = CustomerType.Direct, AssignedProductsOnly = true };
            var b = new Customer { Name = "زبون خاص ب VP", CustomerType = CustomerType.Direct, AssignedProductsOnly = true };
            var all = new Customer { Name = "زبون عادي VP", CustomerType = CustomerType.Direct };
            var none = new Customer { Name = "زبون مخصص بلا منتج VP", CustomerType = CustomerType.Direct, AssignedProductsOnly = true };
            db.Customers.AddRange(a, b, all, none);
            // صنف آخر برصيد في مخزن المنتج التام: لا يظهر لزبون «المخصص له فقط» لأن أحدًا لم يخصص له متغيرًا
            var other = new Item { ItemCode = "VP-O", ItemName = "منتج آخر VP", SalePrice = 100 };
            db.Items.Add(other);
            await db.SaveChangesAsync();
            var fgWh = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.FinishedGoods && w.IsActive);
            db.StockTransactions.Add(new StockTransaction { ItemId = other.Id, WarehouseId = fgWh.Id, QuantityBaseUnits = 500, UnitCost = 1, TransactionType = StockTransactionType.Receipt, CreatedByUserId = admin.Id });
            db.Add(machine);
            await db.SaveChangesAsync();
            var ra = new CustomRecipe { FinishedItemId = water.Id, Name = "ليبل زبون أ", CustomerId = a.Id };
            var rb = new CustomRecipe { FinishedItemId = water.Id, Name = "ليبل زبون ب", CustomerId = b.Id };
            var rg = new CustomRecipe { FinishedItemId = water.Id, Name = "مناسبة عامة VP", CustomerId = null };
            db.CustomRecipes.AddRange(ra, rb, rg);
            await db.SaveChangesAsync();
            var (r, _) = await new ProductionService(db).CreateOrderAsync(new[]
            {
                new ProductionLineInput(water.Id, 100, ra.Id, "9201"),
                new ProductionLineInput(water.Id, 100, rb.Id, "9202"),
                new ProductionLineInput(water.Id, 100, rg.Id, "9203")
            }, raw.Id, machine.Id, admin.Id);
            Assert.True(r.Success, r.ErrorMessage);   // ثلاثة متغيرات قيد الإنتاج تظهر في قائمة التشغيلة بالاسم
            (waterId, otherId, recA, recB, recGeneral, custA, custB, custAll, custNone) = (water.Id, other.Id, ra.Id, rb.Id, rg.Id, a.Id, b.Id, all.Id, none.Id);
        }

        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var sales = shell.Open<SalesModuleViewModel>(ModuleCode.Sales);
        var inv = sales.Invoice;
        sales.SelectedTab = inv;
        await sales.IdleAsync(); await inv.IdleAsync();
        inv.Warehouse = inv.Warehouses.First();
        inv.LineItem = inv.ItemsLookup.Single(i => i.Id == waterId);
        await inv.IdleAsync();

        HashSet<int> Variants() => inv.BatchOptions.Where(o => o.RecipeId is not null).Select(o => o.RecipeId!.Value).ToHashSet();

        // بلا زبون: كل المتغيرات
        Assert.Equal(new HashSet<int> { recA, recB, recGeneral }, Variants());

        // زبون «المخصص له فقط»: متغيره الخاص وحده — لا متغير زبون آخر ولا المتغيرات العامة ولا «تلقائي» العام
        inv.Customer = inv.Customers.Single(c => c.Id == custA);
        await inv.IdleAsync();
        Assert.Equal(new HashSet<int> { recA }, Variants());
        Assert.DoesNotContain(inv.BatchOptions, o => o.RecipeId is null && o.BatchId is null);
        Assert.DoesNotContain(inv.BatchOptions, o => o.Label.Contains("ليبل زبون ب") || o.Label.Contains("مناسبة عامة"));
        // والأصناف: صنفه الخاص فقط، لا الصنف الآخر الذي برصيد
        Assert.Contains(inv.ItemsLookup, i => i.Id == waterId);
        Assert.DoesNotContain(inv.ItemsLookup, i => i.Id == otherId);

        inv.Customer = inv.Customers.Single(c => c.Id == custB);
        await inv.IdleAsync();
        Assert.Equal(new HashSet<int> { recB }, Variants());

        // زبون «كل المنتجات» (الافتراضي): كما كان — كل الأصناف وكل المتغيرات، وتُبنى القائمة من جديد عند الانتقال إليه
        inv.Customer = inv.Customers.Single(c => c.Id == custAll);
        await inv.IdleAsync();
        Assert.Equal(new HashSet<int> { recA, recB, recGeneral }, Variants());
        Assert.Contains(inv.ItemsLookup, i => i.Id == otherId);

        // «المخصص له فقط» ولم يُربط له منتج بعد: لا تُخفى كل المنتجات، يرى الكل إلى أن يُخصَّص له منتج
        inv.Customer = inv.Customers.Single(c => c.Id == custNone);
        await inv.IdleAsync();
        Assert.Equal(new HashSet<int> { recA, recB, recGeneral }, Variants());

        // الخيار يُحفظ من شاشة العملاء
        var customers = sales.Section<CustomersSectionViewModel>();
        Assert.Equal(new[] { false, true }, customers.VisibleProductsOptions.Select(o => o.Value));
        await using (var db = _f.NewDb())
        {
            Assert.True((await db.Customers.SingleAsync(c => c.Id == custA)).AssignedProductsOnly);
            Assert.False((await db.Customers.SingleAsync(c => c.Id == custAll)).AssignedProductsOnly);
        }
        Assert.Empty(dialogs.Errors);
        Assert.Empty(_f.Unhandled);
    }

    [Fact]
    public async Task Invoice_list_button_returns_to_the_list_with_the_invoice_selected_and_asks_only_for_unsaved_input()
    {
        int invoiceId, customerId;
        await using (var db = _f.NewDb())
        {
            var admin = await db.Users.FirstAsync(u => u.Username == AppFixture.AdminUser);
            var fg = await db.Warehouses.FirstAsync(w => w.WarehouseType == WarehouseType.FinishedGoods && w.IsActive);
            var c = new Customer { Name = "زبون الرجوع للقائمة", CustomerType = CustomerType.Direct };
            db.Customers.Add(c);
            await db.SaveChangesAsync();
            var (res, id) = await new SalesService(db).CreateInvoiceAsync(new SalesInvoiceHeaderInput(c.Id, fg.Id, DateTime.Today, InvoicePaymentMethod.Credit), admin.Id);
            Assert.True(res.Success, res.ErrorMessage);
            (invoiceId, customerId) = (id!.Value, c.Id);
        }

        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var sales = shell.Open<SalesModuleViewModel>(ModuleCode.Sales);
        var list = sales.InvoiceList;

        // من القائمة نفتح الفاتورة، وزر «قائمة الفواتير» داخلها يرجع للقائمة ويحدد الفاتورة نفسها
        sales.SelectedTab = list;
        await sales.LastActivation; await list.IdleAsync();
        await sales.OpenInvoiceAsync(invoiceId);
        await sales.Invoice.IdleAsync();
        Assert.Same(sales.Invoice, sales.SelectedTab);
        Assert.Equal(invoiceId, sales.Invoice.InvoiceId);
        // الفاتورة المفتوحة من القائمة تأخذ العرض كاملًا: يختفي الشريط الجانبي، ويرجع بالرجوع إلى القائمة
        Assert.True(sales.WantsFullWidth);
        Assert.True(shell.IsSidebarCollapsed);

        dialogs.Confirms.Clear();
        await sales.Invoice.ShowListCommand.ExecuteAsync();
        await sales.LastActivation; await list.IdleAsync();
        Assert.Same(list, sales.SelectedTab);
        Assert.Equal(invoiceId, list.SelectedRow?.Id);
        Assert.False(sales.WantsFullWidth);
        Assert.False(shell.IsSidebarCollapsed);
        Assert.Empty(dialogs.Confirms);            // مسودة بلا سطور: لا سؤال

        // التحديد يبقى بعد إعادة تحميل القائمة (الفلاتر لم تتغير)
        await list.LoadAsync();
        Assert.Equal(invoiceId, list.SelectedRow?.Id);

        // فاتورة فيها سطور غير محفوظة: يُسأل قبل الرجوع، وبالرفض تبقى في الفاتورة، وبالقبول ترجع
        sales.SelectedTab = sales.Invoice;
        await sales.LastActivation;
        sales.Invoice.ResetForm();
        sales.Invoice.Lines.Add(new InvoiceLineDraft(() => { }) { ItemId = 1, ItemName = "سطر غير محفوظ" });
        dialogs.Confirms.Clear();
        dialogs.ConfirmAnswer = false;
        await sales.Invoice.ShowListCommand.ExecuteAsync();
        Assert.Same(sales.Invoice, sales.SelectedTab);
        Assert.Contains(dialogs.Confirms, m => m.Contains("غير المحفوظة"));
        dialogs.ConfirmAnswer = true;
        await sales.Invoice.ShowListCommand.ExecuteAsync();
        await sales.LastActivation;
        Assert.Same(list, sales.SelectedTab);
        sales.Invoice.ResetForm();
        Assert.NotEqual(0, customerId);
        Assert.Empty(dialogs.Errors);
        Assert.Empty(_f.Unhandled);
    }
}
