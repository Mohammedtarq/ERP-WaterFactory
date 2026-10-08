using ERP.Cloud.Contracts;
using System.Text.Json;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.ViewModels.Reps;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>
/// شاشتا تطبيق المندوبين كما يستخدمهما المحاسب والمدير: «طلبات المندوبين» (اعتماد المصروف، ومراجعة الآجل بلا رفض)،
/// و«أجهزة التطبيق» (تسجيل هاتف بمفتاح يُعرض مرة، وإيقافه، والإعدادات).
/// </summary>
[Collection("app")]
public class RepAppScreenTests
{
    private readonly AppFixture _f;
    public RepAppScreenTests(AppFixture f) => _f = f;

    private static async Task Open(ModuleViewModel module, SectionViewModel section)
    {
        module.SelectedTab = section;
        await module.IdleAsync();
        await section.IdleAsync();
    }

    [Fact]
    public async Task Approve_expense_review_credit_and_manage_devices()
    {
        int repId;
        await using (var db = _f.NewDb())
        {
            var branch = await db.Branches.FirstAsync();
            var admin = await db.Users.FirstAsync(u => u.Username == AppFixture.AdminUser);
            var store = new Warehouse { BranchId = branch.Id, Name = "مخزن شاشة التطبيق", WarehouseType = WarehouseType.FinishedGoods };
            var water = new Item { ItemCode = "RA-S20", ItemName = "ماء شاشة التطبيق شرنك", SalePrice = 250 };
            var rep = new Employee { FullName = "مندوب شاشة التطبيق", IsSalesRep = true, BaseSalary = 500_000 };
            var shop = new Customer { Name = "زبون شاشة التطبيق" };
            db.AddRange(store, water, rep, shop);
            await db.SaveChangesAsync();
            var shrink = new ItemPackagingLevel { ItemId = water.Id, LevelName = "شرنك", EquivalentBaseUnits = 20 };
            db.ItemPackagingLevels.AddRange(new ItemPackagingLevel { ItemId = water.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 }, shrink);
            db.StockTransactions.Add(new StockTransaction { ItemId = water.Id, WarehouseId = store.Id, QuantityBaseUnits = 1_000, UnitCost = 100,
                                                            TransactionType = StockTransactionType.Receipt, CreatedByUserId = admin.Id });
            var van = new Warehouse { BranchId = branch.Id, Name = "سيارة شاشة التطبيق", WarehouseType = WarehouseType.RepVan, OwnerEmployeeId = rep.Id };
            db.Warehouses.Add(van);
            await db.SaveChangesAsync();
            var ops = new RepOperationsService(db);
            var (_, order) = await ops.CreateLoadOrderAsync(van.Id, store.Id, DateTime.Today, new[] { new RepLoadLineInput(water.Id, shrink.Id, 10) }, null, admin.Id);
            Assert.True((await ops.PrepareLoadOrderAsync(order!.Id, null, admin.Id)).result.Success);

            var app = new RepAppService(db);
            var (_, key) = await app.RegisterDeviceAsync(rep.Id, "هاتف شاشة", admin.Id);
            RepRequestEnvelope Env(RepRequestKind k, object p) => new(Guid.NewGuid(), k, DateTime.Now, JsonSerializer.Serialize(p));
            Assert.Equal(RepRequestStatus.Posted, (await app.ReceiveAsync(key!, Env(RepRequestKind.CreditSale,
                new SalePayload(new CustomerRef(shop.Id), new() { new SaleLinePayload(water.Id, shrink.Id, 2) })))).Status);
            // بيع نقدي يضع في المحفظة ما يُصرف منه المصروف (المصروف لا يتجاوز النقد مع المندوب)
            Assert.Equal(RepRequestStatus.Posted, (await app.ReceiveAsync(key!, Env(RepRequestKind.CashSale,
                new SalePayload(new CustomerRef(shop.Id), new() { new SaleLinePayload(water.Id, shrink.Id, 1) })))).Status);
            Assert.Equal(RepRequestStatus.Pending, (await app.ReceiveAsync(key!, Env(RepRequestKind.Expense, new ExpensePayload(2_500, "وقود", "محطة الزبير")))).Status);
            repId = rep.Id;
        }

        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var reps = shell.Open<RepsModuleViewModel>(ModuleCode.Reps);

        // 1) بانتظار الاعتماد: المصروف — الرفض يحتاج سببًا، ثم الاعتماد
        var requests = reps.Requests;
        await Open(reps, requests);
        requests.Rep = requests.RepOptions.Single(o => o.Value == repId);
        await requests.IdleAsync();
        Assert.True(requests.CanApprove);
        requests.Selected = requests.Rows.Single(r => r.Kind == RepRequestKind.Expense);
        Assert.True(requests.CanDecideSelected);
        await requests.RejectCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("سبب الرفض"));
        await requests.ApproveCommand.ExecuteAsync();
        Assert.Empty(requests.Rows);

        // 2) للاطلاع: الآجل، «تمت المراجعة» بلا رفض
        requests.Filter = requests.Filters.Single(f => f.Value == RepRequestFilter.ToReview);
        await requests.IdleAsync();
        var credit = requests.Rows.Single();
        Assert.Equal(RepRequestKind.CreditSale, credit.Kind);
        requests.Selected = credit;
        Assert.False(requests.CanDecideSelected);
        Assert.True(requests.CanReview);
        await requests.ReviewCommand.ExecuteAsync();
        Assert.Empty(requests.Rows);

        // 3) الأجهزة: تسجيل هاتف بمفتاح يُعرض مرة، وإيقافه، والإعدادات
        var devices = reps.Devices;
        await Open(reps, devices);
        devices.NewRep = devices.Reps.Single(o => o.Value == repId);
        devices.NewName = "هاتف احتياطي";
        await using (var cfg = _f.NewDb())
            Assert.True((await new CloudSyncService(cfg).SaveSettingsAsync("http://192.168.1.10:5080", false, 20,
                await cfg.Users.Where(u => u.Username == AppFixture.AdminUser).Select(u => u.Id).SingleAsync())).Success);
        await devices.RegisterCommand.ExecuteAsync();
        Assert.Equal(64, devices.LastKey!.Length);
        // رمز الربط: صورة PNG فيها العنوان والمفتاح
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, devices.LinkQr![..4]);
        Assert.Contains("http://192.168.1.10:5080", devices.LinkHint);
        devices.Selected = devices.Devices.Single(d => d.DeviceName == "هاتف احتياطي");
        await devices.ToggleCommand.ExecuteAsync();
        Assert.False(devices.Devices.Single(d => d.DeviceName == "هاتف احتياطي").IsActive);
        devices.CashAlertDays = 3;
        await devices.SaveSettingsCommand.ExecuteAsync();
        await using (var check = _f.NewDb())
            Assert.Equal(3, (await check.RepAppSettings.SingleAsync()).CashAlertDays);
        devices.CashAlertDays = 2;
        await devices.SaveSettingsCommand.ExecuteAsync();
        Assert.DoesNotContain(dialogs.Errors, e => !e.Contains("سبب الرفض"));
    }
}
