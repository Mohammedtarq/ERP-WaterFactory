using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.ViewModels.Production;
using ERP.Presentation.ViewModels.Reps;
using ERP.Presentation.ViewModels.Settings;
using ERP.Presentation.ViewModels.Shell;
using ERP.Presentation.ViewModels.Warehouse;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>
/// رحلة المستخدم الحقيقية من الصفر: سيرفر SQL فارغ ← معالج الإعداد ← الدخول ← إنشاء حساب موظفة
/// من داخل البرنامج ← دخولها بصلاحياتها ← المندوبون ← الإنتاج. نفس الـ ViewModels التي تعرضها النوافذ.
/// </summary>
public class WizardJourneyTests : IAsyncLifetime
{
    private static string Master => Environment.GetEnvironmentVariable("ERP_TEST_MASTER_CONNECTION")
        ?? throw new InvalidOperationException("ERP_TEST_MASTER_CONNECTION غير معيّن");
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];
    private string ControlDb => $"ERP_WizCtl_{_suffix}";
    private string ProjectDb => $"ERP_WizPrj_{_suffix}";
    private string ProjectDb2 => $"ERP_WizPrj2_{_suffix}";

    public Task InitializeAsync()
    {
        AsyncRelayCommand.UnhandledErrorHandler ??= ex => throw ex;
        return Task.CompletedTask;
    }

    /// <summary>كل واجهة فُتحت في الاختبار: تُنتظر أعمالها الخلفية (لوحات الأقسام) قبل حذف القاعدة،
    /// وإلا قتل الحذف استعلامًا جاريًا وظهر خطأه في اختبارات أخرى.</summary>
    private static readonly List<MainShellViewModel> Shells = new();

    public async Task DisposeAsync()
    {
        List<MainShellViewModel> shells;
        lock (Shells) { shells = Shells.ToList(); Shells.Clear(); }
        foreach (var s in shells) await s.IdleAllAsync();
        SqlConnection.ClearAllPools();
        await using var conn = new SqlConnection(Master);
        await conn.OpenAsync();
        foreach (var db in new[] { ProjectDb, ProjectDb2, ControlDb })
        {
            await using var cmd = new SqlCommand($"IF DB_ID('{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END", conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// سجل المعالج يُملأ عبر Progress&lt;T&gt; (على خيط الواجهة في البرنامج، وبشكل غير متزامن في الاختبار)،
    /// فيُقرأ بنسخة ثابتة مع انتظار قصير بدل التعداد أثناء الإضافة.
    /// </summary>
    private static async Task LogContains(SetupViewModel setup, string text)
    {
        // Progress<T> يكتب السجل لاحقًا على مجمع الخيوط — مهلة 10 ثوانٍ تكفي حتى مع تحميل الاختبارات المتوازية
        for (var i = 0; i < 500; i++)
        {
            string[] snapshot;
            try { snapshot = setup.Log.ToArray(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException) { snapshot = Array.Empty<string>(); }
            if (snapshot.Any(l => l is not null && l.Contains(text))) return;
            await Task.Delay(20);
        }
        Assert.Fail($"السجل لا يحتوي \"{text}\"");
    }

    private static async Task Open(ModuleViewModel module, SectionViewModel section)
    {
        module.SelectedTab = section;
        await module.IdleAsync();
        await section.IdleAsync();
    }

    private static async Task<(MainShellViewModel shell, RecordingDialogs dialogs)> Login(string controlCs, string user, string password)
    {
        var dialogs = new RecordingDialogs();
        var nav = new RecordingNavigator();
        var login = new LoginViewModel(new AuthService(controlCs), dialogs, nav) { Username = user };
        await login.LoginCommand.ExecuteAsync(password);
        Assert.Null(login.ErrorMessage);
        await nav.ProjectSelection!.OpenCommand.ExecuteAsync(null);
        Assert.Null(nav.ProjectSelection.ErrorMessage);
        lock (Shells) Shells.Add(nav.Shell!);
        return (nav.Shell!, dialogs);
    }

    [Fact]
    public async Task From_empty_server_to_working_system()
    {
        var master = new SqlConnectionStringBuilder(Master);

        // ---------------- 1) معالج الإعداد ----------------
        var nav = new RecordingNavigator();
        var config = new MemoryConfigStore();
        var setup = new SetupViewModel(nav, config, null)
        {
            Server = master.DataSource, UseWindowsAuth = false, SqlUser = master.UserID, SqlPassword = master.Password,
            ControlDatabase = ControlDb, ProjectName = "مصنع مياه الرحلة", ProjectDatabase = ProjectDb,
            AdminFullName = "المدير", AdminUsername = "owner", AdminPassword = "Owner@2026", AdminPasswordConfirm = "Owner@2026",
            DemoData = true
        };
        Assert.True(setup.IsNewInstall);
        await setup.TestConnectionCommand.ExecuteAsync();
        Assert.True(setup.ConnectionOk, setup.ConnectionMessage);

        setup.AdminPasswordConfirm = "خطأ";
        await setup.FinishCommand.ExecuteAsync();
        Assert.Contains("غير متطابقتين", setup.ErrorMessage);
        Assert.Null(config.Value);

        setup.AdminPasswordConfirm = "Owner@2026";
        await setup.FinishCommand.ExecuteAsync();
        Assert.True(setup.ErrorMessage is null, setup.ErrorMessage);
        await LogContains(setup, "اكتمل الإعداد");
        Assert.NotNull(config.Value);                                 // الإعداد حُفظ
        Assert.Equal(config.Value, nav.UsedControlConnection);        // وانتقل للدخول عليه
        var controlCs = config.Value!;

        // "الاتصال بنظام قائم" على نفس القاعدة ينجح، وعلى قاعدة فارغة يُرفض
        var reconnect = new SetupViewModel(new RecordingNavigator(), new MemoryConfigStore { Value = controlCs }, "السيرفر توقف");
        Assert.True(reconnect.IsConnectExisting);
        Assert.Equal(ControlDb, reconnect.ControlDatabase);
        reconnect.SqlPassword = master.Password;
        await reconnect.FinishCommand.ExecuteAsync();
        Assert.Null(reconnect.ErrorMessage);

        // ---------------- 2) دخول المدير: كل الوحدات حقيقية ----------------
        var (shell, dialogs) = await Login(controlCs, "owner", "Owner@2026");
        Assert.Equal(9, shell.NavItems.Count);
        foreach (var n in shell.NavItems) Assert.IsNotType<PlaceholderModuleViewModel>(shell.Open<object>(n.ModuleCode));

        // ---------------- 3) إنشاء حساب موظفة مبيعات من داخل البرنامج ----------------
        var settings = shell.Open<SettingsModuleViewModel>(ModuleCode.SystemSettings);
        var users = settings.Section<UsersSectionViewModel>();
        await Open(settings, users);
        await users.NewCommand.ExecuteAsync();
        users.Editor!.Username = "sara";
        users.Editor.RoleId = users.Roles.Single(r => r.Name == "موظف مبيعات").Id;
        users.NewPassword = "Sara@2026";
        await users.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);

        var (saraShell, _) = await Login(controlCs, "sara", "Sara@2026");
        Assert.Equal(new[] { ModuleCode.Dashboard, ModuleCode.Sales }, saraShell.NavItems.Select(n => n.ModuleCode));

        // تغيير كلمة مرورها من الشاشة يسري على الدخول
        await users.EditCommand.ExecuteAsync(users.Items.Single(u => u.Username == "sara"));
        users.NewPassword = "Sara@2027";
        await users.SaveCommand.ExecuteAsync();
        Assert.False((await new AuthService(controlCs).LoginAsync("sara", "Sara@2026")).Success);
        Assert.True((await new AuthService(controlCs).LoginAsync("sara", "Sara@2027")).Success);

        // تبويب المشاريع يعرض المشروع الحالي
        var projects = settings.Section<ProjectsSectionViewModel>();
        await Open(settings, projects);
        Assert.Contains(projects.Projects, p => p.IsCurrent && p.DatabaseName == ProjectDb && p.UsersCount == 2);

        // ---------------- 4) المندوبون: تحميل السيارة ثم تسليم نقد ----------------
        var reps = shell.Open<RepsModuleViewModel>(ModuleCode.Reps);
        Assert.DoesNotContain(reps.Tabs, t => t is VanOperationsSectionViewModel);   // الشاشة القديمة مخفية
        var van = reps.Documents;
        await Open(reps, van);
        Assert.NotNull(van.Van);
        Assert.Equal(StockDocumentType.RepLoad, van.DocumentType.Value);
        van.Store = van.Stores.Single(w => w.WarehouseType == WarehouseType.FinishedGoods);
        van.LineItem = van.ItemsLookup.Single(i => i.ItemCode == "W-500");
        await van.IdleAsync();
        Assert.Equal("كارتون", van.LineLevel!.LevelName);
        van.LineQuantity = 10;
        van.AddLineCommand.Execute(null);
        await van.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Equal(120m, van.VanTotalPieces);
        var loadDoc = Assert.Single(van.Documents);
        Assert.StartsWith("RL-", loadDoc.DocumentNumber);
        Assert.Equal("علي المندوب", loadDoc.PartyName);

        var wallet = reps.Wallet;
        await Open(reps, wallet);
        Assert.Equal(0m, wallet.Balance);
        wallet.Amount = 1000;
        await wallet.SubmitCommand.ExecuteAsync();                  // تسليم يفوق الرصيد
        Assert.Contains(dialogs.Errors, e => e.Contains("رصيد المحفظة"));
        dialogs.Errors.Clear();
        wallet.Action = wallet.Actions.Single(a => a.Value == WalletAction.Collection);
        wallet.Customer = wallet.Customers.Single(c => c.CustomerType == CustomerType.SubCustomer);
        wallet.Amount = 5000;
        await wallet.SubmitCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Equal(5000m, wallet.Balance);

        var fleet = reps.Section<FleetSectionViewModel>();
        await Open(reps, fleet);
        await fleet.NewCommand.ExecuteAsync();
        fleet.Editor!.VehicleName = "كيا بونكو";
        fleet.Editor.PlateNumber = "12345 بصرة";
        fleet.Editor.VehicleRegistrationExpiry = DateTime.Today.AddDays(10);
        await fleet.SaveCommand.ExecuteAsync();
        Assert.True(fleet.HasAlerts);
        Assert.Contains("سنوية كيا بونكو", fleet.AlertsText);

        // ---------------- 5) الإنتاج: أمر ← تشغيل ← مختبر ← تعبئة ----------------
        var prod = shell.Open<ProductionModuleViewModel>(ModuleCode.Production);
        var orders = prod.Orders;
        await Open(prod, orders);
        await orders.NewOrderCommand.ExecuteAsync();
        Assert.StartsWith($"B{DateTime.Today:yyMMdd}-", orders.BatchNumber);
        orders.FinishedItem = orders.FinishedItems.Single(i => i.ItemCode == "W-500");
        orders.Quantity = 240;
        orders.Machine = orders.Machines.Single(m => m.Name == "نافخة 1");
        await orders.IdleAsync();
        Assert.True(orders.AllSufficient);
        Assert.Equal(3, orders.Preview.Count);
        await orders.CreateCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        var order = orders.Orders.First();
        Assert.Contains("مسودة", order.StageText);
        await orders.StartCommand.ExecuteAsync(order);
        Assert.Contains("بانتظار فحص المختبر", orders.Orders.First().StageText);

        var qc = prod.Qc;
        await Open(prod, qc);
        Assert.Equal(order.Id, qc.Order!.OrderId);
        Assert.Equal(3, qc.Lines.Count);
        qc.Lines.Single(l => l.TestName.StartsWith("درجة")).Measured = "7.4";
        qc.Lines.Single(l => l.TestName.StartsWith("الأملاح")).Measured = "140";
        qc.Lines.Single(l => l.TestName.StartsWith("إحكام")).Measured = "سليم";
        await qc.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Contains("ناجحة", qc.LastResult);

        var packing = prod.Packing;
        await Open(prod, packing);
        Assert.Equal(order.Id, packing.Order!.OrderId);
        packing.Level = packing.Levels.Single(l => l.LevelName == "كارتون");
        packing.Units = 20;
        await packing.PackCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        await Open(prod, orders);
        Assert.Equal("مكتمل", orders.Orders.Single(o => o.Id == order.Id).StageText);
        Assert.Equal("نافخة 1", orders.Orders.Single(o => o.Id == order.Id).MachineName);

        // لوحة المختبر: دفعة واحدة فُحصت ونجحت ← نسبة النجاح 100%
        await prod.Dashboard!.LoadAsync();
        Assert.Contains(prod.Dashboard.Tiles, t => t.Title == "المختبر — فحوصات الشهر" && t.Value == "1");
        Assert.Contains(prod.Dashboard.Tiles, t => t.Title == "المختبر — نسبة النجاح" && t.Value == "100%");

        // تعديل رقم الدفعة من الشاشة ← يظهر في السجل وفي شهادة المختبر المطبوعة
        await orders.EditBatchCommand.ExecuteAsync(orders.Orders.Single(o => o.Id == order.Id));
        Assert.True(orders.IsEditingBatch);
        orders.NewBatchNumber = "L-2026-001";
        orders.BatchReason = "ترقيم العميل";
        await orders.SaveBatchCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.False(orders.IsEditingBatch);
        Assert.Equal("L-2026-001", orders.Orders.Single(o => o.Id == order.Id).OutputBatch);
        await orders.EditBatchCommand.ExecuteAsync(orders.Orders.Single(o => o.Id == order.Id));
        Assert.Single(orders.BatchHistory);
        Assert.Contains("ترقيم العميل", orders.BatchHistory[0]);
        orders.CloseBatchCommand.Execute(null);

        // تحت التصنيع: صُرف 240 من كل مادة واستُهلك 240 (المُنتَج فعلًا) ← المتبقي صفر والمطابقة سليمة
        var wip = prod.Wip;
        await Open(prod, wip);
        Assert.Equal(3, wip.Rows.Count);
        Assert.All(wip.Rows, r => Assert.Equal((240m, 240m, 0m, true), (r.Issued, r.Consumed, r.Remaining, r.IsReconciled)));
        Assert.False(wip.HasAlert);
        wip.PrintCommand.Execute(null);
        Assert.Equal("تقرير تحت التصنيع حسب الماكينة", dialogs.Reports.Last().Title);

        // تعديل المشرف (الأدمن): المتبقي الفعلي بعد الجرد 5 بدل 0، بسبب إلزامي، ويظهر في السجل وفي المطابقة
        Assert.True(wip.IsSupervisor);
        wip.ActionMachine = wip.Machines.Single(m => m.Name == "نافخة 1");
        await wip.IdleAsync();
        wip.ActionItem = wip.ActionItems.First();
        wip.AdjustNewQuantity = 5;
        await wip.AdjustCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("إلزامي"));
        dialogs.Errors.Clear();
        wip.AdjustReason = "جرد الوردية";
        await wip.AdjustCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        var adj = Assert.Single(wip.Adjustments);
        Assert.Equal((0m, 5m, "جرد الوردية"), (adj.BeforeQuantity, adj.AfterQuantity, adj.Reason));
        Assert.Contains(wip.Rows, r => r.Adjusted == 5 && r.Remaining == 5 && r.IsReconciled);
        Assert.False(wip.HasAlert);
        wip.PrintAdjustmentsCommand.Execute(null);
        Assert.Equal("سجل تعديلات المشرف — تحت التصنيع", dialogs.Reports.Last().Title);

        // ماكينة جديدة من تبويب الماكينات تظهر في أمر الإنتاج
        var machinesTab = prod.Machines;
        await Open(prod, machinesTab);
        await machinesTab.NewCommand.ExecuteAsync();
        machinesTab.Editor!.Name = "تغليف 1";
        machinesTab.Editor.MachineType = "تغليف";
        await machinesTab.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        await Open(prod, orders);
        Assert.Contains(orders.Machines, m => m.Name == "تغليف 1");

        // ---------------- قوالب التعبئة ← قائمة المواد ← بديل العميل ← استبدال لأمر واحد ----------------
        var wh = shell.Open<WarehouseModuleViewModel>(ModuleCode.Warehouse);
        var templatesTab = wh.Section<PackagingTemplatesSectionViewModel>();
        await Open(wh, templatesTab);
        Assert.Contains(templatesTab.Templates, t => t.Name == "330×40 كارتون");
        Assert.Contains(templatesTab.Templates, t => t.Name == "330×20 شرنك");
        templatesTab.NewCommand.Execute(null);
        templatesTab.Name = "عبوة 1.5 لتر";
        templatesTab.SetLine(0, "امبولة", templatesTab.RawItems.Single(i => i.ItemCode == "RM-PRE"), 1, 1);
        templatesTab.SetLine(1, "غطاء", templatesTab.RawItems.Single(i => i.ItemCode == "RM-CAP"), 1, 1);
        await templatesTab.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.False(templatesTab.IsEditing);

        var bomTab = wh.Section<BomSectionViewModel>();
        await Open(wh, bomTab);
        bomTab.FinishedItem = bomTab.AllItems.Single(i => i.ItemCode == "W-1500");
        await bomTab.IdleAsync();
        bomTab.Template = bomTab.Templates.Single(t => t.Name == "عبوة 1.5 لتر");
        Assert.Equal(2, bomTab.RoleChoices.Count);
        await bomTab.ApplyTemplateCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Equal(new[] { "امبولة", "غطاء" }, bomTab.Lines.Select(l => l.ComponentRole).OrderBy(r => r).ToArray());

        var recipesTab = prod.Section<CustomRecipesSectionViewModel>();
        await Open(prod, recipesTab);
        await recipesTab.NewCommand.ExecuteAsync();
        recipesTab.Editor!.Name = "1.5 لتر — مطعم";
        recipesTab.Editor.FinishedItemId = recipesTab.FinishedItems.Single(i => i.ItemCode == "W-1500").Id;
        recipesTab.Editor.CustomerId = recipesTab.Customers.First().Id;
        await recipesTab.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        recipesTab.SelectedRecipe = recipesTab.Items.Single(r => r.Name == "1.5 لتر — مطعم");
        await recipesTab.IdleAsync();
        recipesTab.SelectedRole = recipesTab.RoleOptions.Single(r => r.ComponentRole == "غطاء");
        recipesTab.VariantItem = recipesTab.RawItems.Single(i => i.ItemCode == "RM-LBL");
        await recipesTab.SetVariantCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Contains(recipesTab.Lines, l => l.ComponentLabel == "غطاء" && l.ReplacesName == "غطاء قنينة");

        await Open(prod, orders);
        await orders.NewOrderCommand.ExecuteAsync();
        orders.FinishedItem = orders.FinishedItems.Single(i => i.ItemCode == "W-1500");
        await orders.IdleAsync();
        orders.Recipe = orders.Recipes.Single(r => r.Name == "1.5 لتر — مطعم");
        orders.Quantity = 12;
        await orders.CreateCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        var draft = orders.Orders.First();
        await orders.OverrideCommand.ExecuteAsync(draft);
        Assert.True(orders.IsOverriding);
        orders.OverrideOriginal = orders.OverrideComponents.Single(c => c.Name.Contains("بريفورم"));
        orders.OverrideReplacement = orders.ReplacementItems.Single(i => i.ItemCode == "RM-CAP");
        await orders.SaveOverrideCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("إلزامي"));
        dialogs.Errors.Clear();
        orders.OverrideReason = "تجربة الاستبدال";
        await orders.SaveOverrideCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.False(orders.IsOverriding);
        await orders.PrintCommand.ExecuteAsync(orders.Orders.Single(o => o.Id == draft.Id));
        Assert.Contains(dialogs.Reports.Last().HeaderFields, f => f.Label == "استبدال مكوّن" && f.Value.Contains("تجربة الاستبدال"));
        await orders.CancelOrderCommand.ExecuteAsync(orders.Orders.Single(o => o.Id == draft.Id));

        // ---------------- 6) الطباعة: أمر الإنتاج، شهادة المختبر، محضر التعبئة، المحفظة، جرد السيارة ----------------
        await orders.PrintCommand.ExecuteAsync(orders.Orders.Single(o => o.Id == order.Id));
        var mo = dialogs.Reports.Last();
        Assert.Equal("أمر إنتاج", mo.Title);
        Assert.Equal(3, mo.Rows.Count);
        Assert.Contains(mo.HeaderFields, f => f.Label == "الماكينة" && f.Value == "نافخة 1");
        Assert.Contains("المستهلك فعليًا", mo.Columns);
        Assert.Contains(mo.HeaderFields, f => f.Label == "نتيجة المختبر" && f.Value == "ناجحة");
        await Open(prod, qc);
        await qc.PrintCommand.ExecuteAsync(qc.History.First());
        Assert.Equal("شهادة فحص مختبري", dialogs.Reports.Last().Title);
        Assert.Equal(3, dialogs.Reports.Last().Rows.Count);
        await Open(prod, packing);
        await packing.PrintCommand.ExecuteAsync(packing.History.First());
        Assert.Contains(dialogs.Reports.Last().Totals, t => t.Label == "إجمالي المعبّأ" && t.Value == "240 قطعة");
        wallet.PrintStatementCommand.Execute(null);
        Assert.Contains(dialogs.Reports.Last().Totals, t => t.Label == "رصيد المحفظة" && t.Value == "5,000 د.ع");
        van.PrintStockCommand.Execute(null);
        Assert.Contains(dialogs.Reports.Last().Totals, t => t.Value == "120");

        // ---------------- 7) إرجاع من المندوب: كارتون سليم يعود للمخزن، و6 قطع تلف ميداني لا تعود رصيدًا سليمًا ----------------
        await Open(reps, van);
        van.DocumentType = van.DocumentTypes.Single(t => t.Value == StockDocumentType.RepReturn);
        van.LineItem = van.ItemsLookup.Single(i => i.ItemCode == "W-500");
        await van.IdleAsync();
        van.LineLevel = van.LevelOptions.Single(l => l.LevelName == "كارتون");
        van.LineQuantity = 1;
        van.AddLineCommand.Execute(null);
        van.LineLevel = van.LevelOptions.Single(l => l.EquivalentBaseUnits == 1);
        van.LineQuantity = 6;
        van.LineDamaged = true;
        van.AddLineCommand.Execute(null);
        Assert.Contains("تلف ميداني 6", van.LinesTotalText);
        await van.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Equal(102m, van.VanTotalPieces);
        var returnDoc = van.Documents.First();
        Assert.StartsWith("RR-", returnDoc.DocumentNumber);
        Assert.Equal((18m, 6m), (returnDoc.TotalPieces, returnDoc.DamagedPieces));
        await van.PrintCommand.ExecuteAsync(returnDoc);
        var rr = dialogs.Reports.Last();
        Assert.Equal("مستند إرجاع من مندوب", rr.Title);
        Assert.Contains(rr.HeaderFields, f => f.Label == "المندوب" && f.Value == "علي المندوب");
        Assert.Contains(rr.Rows, r => r.Contains("تلف ميداني"));
        Assert.Contains(rr.Totals, t => t.Label == "سليم يعود للمخزن" && t.Value == "12");
        Assert.Contains(rr.Totals, t => t.Label == "تلف ميداني" && t.Value == "6");
    }

    private SetupViewModel Wizard(RecordingNavigator nav, MemoryConfigStore config, string projectDb, string password)
    {
        var master = new SqlConnectionStringBuilder(Master);
        return new SetupViewModel(nav, config, null)
        {
            Server = master.DataSource, UseWindowsAuth = false, SqlUser = master.UserID, SqlPassword = master.Password,
            ControlDatabase = ControlDb, ProjectName = "مصنع مياه البصرة", ProjectDatabase = projectDb,
            AdminFullName = "المدير", AdminUsername = "admin", AdminPassword = password, AdminPasswordConfirm = password, DemoData = true
        };
    }

    /// <summary>
    /// البلاغ الفعلي: تثبيت جديد بمشروع ERP_Basra_V2 على قاعدة تحكم فيها "admin" من تثبيت سابق ←
    /// كان المعالج ينجح ثم ترفض شاشة الدخول نفس القيم. الآن: رفض واضح، ثم إعادة تعيين صريحة والدخول يعمل فورًا.
    /// </summary>
    [Fact]
    public async Task Wizard_then_login_screen_with_the_same_values_on_a_server_with_a_previous_install()
    {
        // تثبيت سابق بكلمة مرور قديمة
        var old = Wizard(new RecordingNavigator(), new MemoryConfigStore(), ProjectDb, "OldPass@2025");
        await old.FinishCommand.ExecuteAsync();
        Assert.True(old.ErrorMessage is null, old.ErrorMessage);

        // تثبيت جديد بكلمة مرور جديدة: لا يُعلن النجاح ولا ينتقل للدخول
        var nav = new RecordingNavigator();
        var config = new MemoryConfigStore();
        var setup = Wizard(nav, config, ProjectDb2, "Basra@2026");
        await setup.FinishCommand.ExecuteAsync();
        Assert.Contains("موجود مسبقًا", setup.ErrorMessage);
        Assert.Null(nav.UsedControlConnection);

        // المستخدم يفعّل إعادة التعيين ← ينجح ← شاشة الدخول بنفس القيم تدخل وتفتح المشروع الجديد
        setup.ResetExistingAdminPassword = true;
        await setup.FinishCommand.ExecuteAsync();
        Assert.True(setup.ErrorMessage is null, setup.ErrorMessage);
        await LogContains(setup, "تحقق الدخول");
        Assert.NotNull(nav.UsedControlConnection);

        var loginNav = new RecordingNavigator();
        var login = new LoginViewModel(new AuthService(nav.UsedControlConnection!), new RecordingDialogs(), loginNav) { Username = "admin" };
        await login.LoginCommand.ExecuteAsync("Basra@2026");
        Assert.Null(login.ErrorMessage);
        var ps = loginNav.ProjectSelection!;
        Assert.Contains(ps.Projects, p => p.DatabaseName == ProjectDb2);
        ps.SelectedProject = ps.Projects.Single(p => p.DatabaseName == ProjectDb2);
        await ps.OpenCommand.ExecuteAsync(null);
        Assert.Null(ps.ErrorMessage);
        lock (Shells) Shells.Add(loginNav.Shell!);
        Assert.Equal(9, loginNav.Shell!.NavItems.Count);
    }
}
