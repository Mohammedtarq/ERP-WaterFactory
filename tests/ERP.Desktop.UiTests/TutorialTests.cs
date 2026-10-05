using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Data.Setup;
using ERP.Desktop.Views.Shell;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels;
using ERP.Presentation.ViewModels.HR;
using ERP.Presentation.ViewModels.Production;
using ERP.Presentation.ViewModels.Shell;
using ERP.Presentation.ViewModels.Warehouse;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace ERP.Desktop.UiTests;

/// <summary>رسائل الدرس: التأكيد بنعم (كما يفعل المستخدم)، وحفظ المستندات المطبوعة لتصويرها.</summary>
public class TutorialDialogs : IDialogService
{
    public List<string> Errors { get; } = new();
    public List<ReportDocument> Reports { get; } = new();
    public void Info(string message) { }
    public void Error(string message) => Errors.Add(message);
    public bool Confirm(string message) => true;
    public void ShowReport(ReportDocument report) => Reports.Add(report);
    public string? PickImageFile() => null;
}

/// <summary>
/// الدليل المصوَّر: ينفّذ على الشاشات الحقيقية وبيانات حقيقية (LocalDB) المسار الذي يتّبعه المستخدم بالترتيب:
/// 1) تعريف مواد أولية جديدة وإدخالها لمخزن المواد الأولية، 2) صنف جديد بقالب تعبئة ووصفة (السدادة واللاصق)،
/// 3) إنتاج كامل: أمر ← صرف ← مختبر ← تعبئة ← مخزن المنتج التام. صورة للنافذة الرئيسية بعد كل خطوة في مجلد tutorial.
/// </summary>
[Collection("ui")]
public class TutorialTests
{
    private readonly ITestOutputHelper _out;
    public TutorialTests(ITestOutputHelper output) => _out = output;

    private const string Dir = "tutorial";

    [Fact]
    public async Task Illustrated_guide_raw_materials_new_item_and_full_production()
    {
        var server = Environment.GetEnvironmentVariable("ERP_UI_SQL_CONNECTION");
        if (server is null) { _out.WriteLine("ERP_UI_SQL_CONNECTION غير معيّن — تخطي"); return; }

        var suffix = Guid.NewGuid().ToString("N")[..6];
        var controlCs = new SqlConnectionStringBuilder(server) { InitialCatalog = $"ERP_TutCtl_{suffix}" }.ConnectionString;
        var install = await new ProvisioningService().InstallAsync(new InstallRequest(
            controlCs, "مصنع مياه البصرة", $"ERP_TutPrj_{suffix}", "المدير العام", "admin", "Admin@123", DemoData: true));
        Assert.True(install.Success, install.ErrorMessage);

        var dialogs = new TutorialDialogs();
        var shots = new List<string>();
        await UiThread.RunAsync(async () =>
        {
            var nav = new CapturingNavigator();
            var login = new LoginViewModel(new AuthService(controlCs), dialogs, nav) { Username = "admin" };
            await login.LoginCommand.ExecuteAsync("Admin@123");
            Assert.Null(login.ErrorMessage);
            await nav.ProjectSelection!.OpenCommand.ExecuteAsync(null);
            var shell = nav.Shell!;
            var main = new MainWindow { DataContext = shell, ShowActivated = false, Width = 1440, Height = 900,
                                        Left = 0, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual, WindowState = WindowState.Normal };
            main.Show();

            async Task Shot(string name, ViewModelBase? busy = null)
            {
                if (busy is not null) await busy.IdleAsync();
                await UiThread.SettleAsync();
                RefreshBindings(main);
                await UiThread.SettleAsync();
                Assert.True(dialogs.Errors.Count == 0, $"قبل الصورة {name}: " + string.Join(" | ", dialogs.Errors));
                shots.Add(UiThread.Save((FrameworkElement)main.Content, $"{Dir}/{name}.png"));
            }
            async Task Open(ModuleViewModel module, SectionViewModel section)
            {
                module.SelectedTab = section;
                await module.LastActivation;
                await section.IdleAsync();
                await UiThread.SettleAsync();
            }
            async Task PreviewShot(string name, ReportDocument report)
            {
                var preview = new ReportPreviewWindow(report) { ShowActivated = false, ShowInTaskbar = false, Left = 0, Top = 0,
                                                                WindowStartupLocation = WindowStartupLocation.Manual };
                preview.Show();
                await UiThread.SettleAsync();
                shots.Add(UiThread.Save((FrameworkElement)preview.Content, $"{Dir}/{name}.png"));
                preview.Close();
            }

            // ================= الجزء الأول: المواد الأولية =================
            var wh = shell.Open<WarehouseModuleViewModel>(ModuleCode.Warehouse);
            await wh.IdleAsync();
            await Shot("01-وحدة-المخازن-الرئيسية", wh);

            var items = wh.Section<ItemsSectionViewModel>();
            await Open(wh, items);
            async Task NewItem(string code, string name, SourcingMethod sourcing, decimal price, string? shot)
            {
                await items.NewCommand.ExecuteAsync();
                items.Editor!.ItemCode = code;
                items.Editor.ItemName = name;
                items.Editor.SourcingMethod = sourcing;
                items.Editor.SalePrice = price;
                if (shot is not null) await Shot(shot, items);
                await items.SaveCommand.ExecuteAsync();
                await items.IdleAsync();
            }
            await NewItem("RM-CAP330", "سدادة زرقاء 330 مل", SourcingMethod.Purchased, 0, "02-تعريف-مادة-أولية-جديدة");
            await NewItem("RM-LBL330", "لاصق ماء البصرة 330 مل", SourcingMethod.Purchased, 0, null);
            await NewItem("RM-SHR20", "نايلون شرنك 20 قنينة", SourcingMethod.Purchased, 0, null);
            items.SearchText = "330";
            await Shot("03-المواد-الأولية-الجديدة-في-قائمة-الأصناف", items);
            items.SearchText = "";

            var raw = wh.Workspaces.First(w => w.WarehouseType == WarehouseType.RawMaterial);
            await Open(wh, raw);
            raw.Operation = raw.OperationOptions.Single(o => o.Value == StockDocumentType.Receipt);
            raw.PartyName = "شركة البلاستيك الحديثة — فاتورة 4471";
            async Task Line(string code, decimal qty, string batch)
            {
                raw.LineItem = raw.ItemsLookup.Single(i => i.ItemCode == code);
                await raw.IdleAsync();
                raw.LineQuantity = qty;
                raw.LineNewBatch = batch;
                raw.LineExpiry = DateTime.Today.AddYears(2);
                await raw.AddLineCommand.ExecuteAsync();
            }
            await Line("RM-CAP330", 5000, "CAP-2610");
            await Line("RM-LBL330", 10000, "LBL-2610");
            await Line("RM-SHR20", 500, "SHR-2610");
            await Shot("04-مستند-إدخال-مخزني-قبل-الحفظ", raw);
            await raw.SaveCommand.ExecuteAsync();
            await Shot("05-بعد-حفظ-الإدخال", raw);
            await PreviewShot("06-معاينة-طباعة-مستند-الإدخال", dialogs.Reports.Last());
            SelectInnerTab(main, "الأرصدة الحالية");
            await Shot("07-أرصدة-مخزن-المواد-الأولية", raw);
            SelectInnerTab(main, "عملية جديدة");

            // ================= الجزء الثاني: صنف جديد بقالب تعبئة ووصفة =================
            await Open(wh, items);
            await NewItem("W-330", "ماء البصرة 330 مل", SourcingMethod.Manufactured, 150, "08-تعريف-المنتج-الجديد");

            var pack = wh.Section<PackagingSectionViewModel>();
            await Open(wh, pack);
            pack.SelectedItem = pack.ItemsLookup.Single(i => i.ItemCode == "W-330");
            await pack.IdleAsync();
            await pack.NewCommand.ExecuteAsync();
            pack.Editor!.LevelName = "شرنك";
            pack.Editor.ParentLevelId = pack.Items.Single().Id;
            pack.Editor.ContainsQuantity = 20;
            await Shot("09-وحدة-تعبئة-شرنك-20", pack);
            await pack.SaveCommand.ExecuteAsync();
            await Shot("10-وحدات-تعبئة-المنتج", pack);

            var templates = wh.Section<PackagingTemplatesSectionViewModel>();
            await Open(wh, templates);
            await Shot("11-قوالب-التعبئة", templates);

            var bom = wh.Section<BomSectionViewModel>();
            await Open(wh, bom);
            bom.FinishedItem = bom.AllItems.Single(i => i.ItemCode == "W-330");
            await bom.IdleAsync();
            bom.Template = bom.Templates.Single(t => t.Name == "330×20 شرنك");
            Item Pick(string code) => bom.AllItems.Single(i => i.ItemCode == code);
            bom.RoleChoices.Single(c => c.Role == "شرنك").ChosenItem = Pick("RM-SHR20");
            bom.RoleChoices.Single(c => c.Role == "غطاء").ChosenItem = Pick("RM-CAP330");
            bom.RoleChoices.Single(c => c.Role == "لاصق").ChosenItem = Pick("RM-LBL330");
            await Shot("12-اختيار-القالب-والسدادة-واللاصق", bom);
            await bom.ApplyTemplateCommand.ExecuteAsync();
            await Shot("13-وصفة-المنتج-بعد-تطبيق-القالب", bom);
            Assert.Equal(4, bom.Lines.Count);

            // ================= الجزء الثالث: الإنتاج حتى مخزن المنتج التام =================
            var prod = shell.Open<ProductionModuleViewModel>(ModuleCode.Production);
            await prod.IdleAsync();
            await Shot("14-وحدة-الإنتاج-والمختبر", prod);

            var orders = prod.Orders;
            await Open(prod, orders);
            await orders.NewOrderCommand.ExecuteAsync();
            orders.FinishedItem = orders.FinishedItems.Single(i => i.ItemCode == "W-330");
            await orders.IdleAsync();
            orders.Quantity = 400;
            orders.Machine = orders.Machines.Single(m => m.Name == "نافخة 1");
            await orders.PreviewCommand.ExecuteAsync();
            await Shot("15-أمر-إنتاج-جديد-الماكينة-ورقم-الدفعة", orders);
            Assert.True(orders.AllSufficient);
            await orders.CreateCommand.ExecuteAsync();
            await Shot("16-الأمر-مسودة", orders);

            var order = orders.Orders.First();
            await orders.StartCommand.ExecuteAsync(order);
            await Shot("17-بعد-بدء-التشغيل-وصرف-المواد", orders);

            var wip = prod.Wip;
            await Open(prod, wip);
            await Shot("18-المواد-تحت-التصنيع-على-الماكينة", wip);

            var qc = prod.Qc;
            await Open(prod, qc);
            qc.Lines.Single(l => l.TestName.StartsWith("درجة")).Measured = "7.3";
            qc.Lines.Single(l => l.TestName.StartsWith("الأملاح")).Measured = "135";
            qc.Lines.Single(l => l.TestName.StartsWith("إحكام")).Measured = "سليم";
            await Shot("19-تسجيل-نتائج-المختبر", qc);
            await qc.SaveCommand.ExecuteAsync();
            await Shot("20-الدفعة-ناجحة", qc);
            await PreviewShot("21-شهادة-فحص-المختبر", await PrintFirst(qc));

            var packing = prod.Packing;
            await Open(prod, packing);
            packing.Level = packing.Levels.Single(l => l.LevelName == "شرنك");
            packing.Units = 20;
            packing.Warehouse = packing.Warehouses.First(w => w.WarehouseType == WarehouseType.FinishedGoods);
            await Shot("22-أمر-التعبئة", packing);
            await packing.PackCommand.ExecuteAsync();
            await Shot("23-بعد-التعبئة", packing);

            await Open(prod, orders);
            await Shot("24-الأمر-مكتمل", orders);
            await orders.PrintCommand.ExecuteAsync(orders.Orders.Single(o => o.Id == order.Id));
            await orders.IdleAsync();
            await PreviewShot("25-طباعة-أمر-الإنتاج", dialogs.Reports.Last());

            await Open(prod, wip);
            await Shot("26-مطابقة-تحت-التصنيع-بعد-الإنتاج", wip);

            var fg = wh.Workspaces.First(w => w.WarehouseType == WarehouseType.FinishedGoods);
            shell.Open<WarehouseModuleViewModel>(ModuleCode.Warehouse);
            await Open(wh, fg);
            SelectInnerTab(main, "الأرصدة الحالية");
            await Shot("27-المنتج-في-مخزن-المنتج-التام", fg);
            Assert.Contains(fg.Balances, b => b.ItemCode == "W-330" && b.Quantity == 400);

            main.Close();

            async Task<ReportDocument> PrintFirst(QcSectionViewModel q)
            {
                await q.PrintCommand.ExecuteAsync(q.History.First());
                await q.IdleAsync();
                return dialogs.Reports.Last();
            }
        });

        _out.WriteLine($"الدليل: {shots.Count} صورة → {Path.Combine(UiThread.OutputDir, Dir)}");
        Assert.Equal(27, shots.Count);
        Assert.Empty(dialogs.Errors);
        lock (UiThread.Unhandled) Assert.True(UiThread.Unhandled.Count == 0, string.Join("\n", UiThread.Unhandled.Select(e => e.ToString())));
    }

    /// <summary>
    /// الدليل المصوَّر للموارد البشرية بالترتيب الذي يعمل به القسم:
    /// قسم ← شفت ← موظف ← عامل وقتي ← حضور يوم ← سلفة ← مقياس الحافز ← التقييم ← الرواتب (توليد، اعتماد، طباعة) ← كشف العمال الوقتيين وصرف أجرهم.
    /// صورة بعد كل خطوة في مجلد tutorial-hr.
    /// </summary>
    [Fact]
    public async Task Illustrated_guide_human_resources()
    {
        var server = Environment.GetEnvironmentVariable("ERP_UI_SQL_CONNECTION");
        if (server is null) { _out.WriteLine("ERP_UI_SQL_CONNECTION غير معيّن — تخطي"); return; }
        const string hrDir = "tutorial-hr";

        var suffix = Guid.NewGuid().ToString("N")[..6];
        var controlCs = new SqlConnectionStringBuilder(server) { InitialCatalog = $"ERP_TutHrCtl_{suffix}" }.ConnectionString;
        var install = await new ProvisioningService().InstallAsync(new InstallRequest(
            controlCs, "مصنع مياه البصرة", $"ERP_TutHrPrj_{suffix}", "المدير العام", "admin", "Admin@123", DemoData: true));
        Assert.True(install.Success, install.ErrorMessage);
        // رصيد افتتاحي للصندوق الافتراضي (كما يفعل المالك أول يوم): منه تُصرف السلفة وأجور العمال الوقتيين
        await using (var db = new ERP.Data.ProjectDb.ProjectDbContext(new DbContextOptionsBuilder<ERP.Data.ProjectDb.ProjectDbContext>()
                         .UseSqlServer(install.ProjectConnectionString).Options))
        {
            var adminId = await db.Users.Where(u => u.Username == "admin").Select(u => u.Id).SingleAsync();
            var boxId = await db.CashBoxes.Where(b => b.IsActive && b.IsDefault).Select(b => b.Id).FirstAsync();
            var (funded, _) = await new CashBoxService(db).OpeningAsync(boxId, 2_000_000, DateTime.Today.AddMonths(-2), "رصيد افتتاحي", adminId);
            Assert.True(funded.Success, funded.ErrorMessage);
        }

        var dialogs = new TutorialDialogs();
        var shots = new List<string>();
        await UiThread.RunAsync(async () =>
        {
            var nav = new CapturingNavigator();
            var login = new LoginViewModel(new AuthService(controlCs), dialogs, nav) { Username = "admin" };
            await login.LoginCommand.ExecuteAsync("Admin@123");
            Assert.Null(login.ErrorMessage);
            await nav.ProjectSelection!.OpenCommand.ExecuteAsync(null);
            var shell = nav.Shell!;
            var main = new MainWindow { DataContext = shell, ShowActivated = false, Width = 1440, Height = 900,
                                        Left = 0, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual, WindowState = WindowState.Normal };
            main.Show();

            async Task Shot(string name, ViewModelBase? busy = null)
            {
                if (busy is not null) await busy.IdleAsync();
                await UiThread.SettleAsync();
                RefreshBindings(main);
                await UiThread.SettleAsync();
                Assert.True(dialogs.Errors.Count == 0, $"قبل الصورة {name}: " + string.Join(" | ", dialogs.Errors));
                shots.Add(UiThread.Save((FrameworkElement)main.Content, $"{hrDir}/{name}.png"));
            }
            async Task Open(ModuleViewModel module, SectionViewModel section)
            {
                module.SelectedTab = section;
                await module.LastActivation;
                await section.IdleAsync();
                await UiThread.SettleAsync();
            }
            async Task PreviewShot(string name, ReportDocument report)
            {
                var preview = new ReportPreviewWindow(report) { ShowActivated = false, ShowInTaskbar = false, Left = 0, Top = 0,
                                                                WindowStartupLocation = WindowStartupLocation.Manual };
                preview.Show();
                await UiThread.SettleAsync();
                shots.Add(UiThread.Save((FrameworkElement)preview.Content, $"{hrDir}/{name}.png"));
                preview.Close();
            }

            var hr = shell.Open<HrModuleViewModel>(ModuleCode.HR);
            await hr.IdleAsync();
            await Shot("01-وحدة-الموارد-البشرية", hr);

            // 1) القسم
            var deps = hr.Section<DepartmentsSectionViewModel>();
            await Open(hr, deps);
            await deps.NewCommand.ExecuteAsync();
            deps.Editor!.Name = "الإنتاج";
            await Shot("02-قسم-جديد", deps);
            await deps.SaveCommand.ExecuteAsync();
            var depId = deps.Items.Single(x => x.Name == "الإنتاج").Id;

            // 2) الشفت الصباحي للإنتاج 6:00–16:00
            var shifts = hr.Section<ShiftsSectionViewModel>();
            await Open(hr, shifts);
            await shifts.NewCommand.ExecuteAsync();
            shifts.Editor!.Name = "الإنتاج الصباحي";
            shifts.Editor.CheckInTime = new TimeSpan(6, 0, 0);
            shifts.Editor.CheckOutTime = new TimeSpan(16, 0, 0);
            shifts.Editor.CheckInGraceMinutes = 10;
            await Shot("03-شفت-الإنتاج-الصباحي", shifts);
            await shifts.SaveCommand.ExecuteAsync();
            var shiftId = shifts.Items.Single(x => x.Name == "الإنتاج الصباحي").Id;

            // 3) موظف بالراتب الشهري
            var emps = hr.Section<EmployeesSectionViewModel>();
            await Open(hr, emps);
            await emps.NewCommand.ExecuteAsync();
            emps.Editor!.FullName = "حسين كاظم";
            emps.Editor.JobTitle = "مشغّل نافخة";
            emps.Editor.DepartmentId = depId;
            emps.Editor.ShiftId = shiftId;
            emps.Editor.BaseSalary = 750_000;
            emps.Editor.HireDate = new DateTime(2024, 3, 1);
            await Shot("04-موظف-جديد", emps);
            await emps.SaveCommand.ExecuteAsync();
            var empId = emps.Items.Single(e => e.FullName == "حسين كاظم").Id;

            // 4) عامل وقتي: أجر يومي بلا بصمة ولا سلف
            await emps.NewCommand.ExecuteAsync();
            emps.Editor!.FullName = "عباس جاسم";
            emps.Editor.JobTitle = "عامل تحميل";
            emps.Editor.DepartmentId = depId;
            emps.Editor.IsTemporary = true;
            emps.Editor.DailyWage = 20_000;
            emps.Editor.HireDate = DateTime.Today.AddDays(-7);
            await Shot("05-عامل-وقتي-بأجر-يومي", emps);
            await emps.SaveCommand.ExecuteAsync();
            await Shot("06-قائمة-الموظفين", emps);

            // 5) الحضور: أول يوم عمل من الشهر الماضي
            var day = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-1);
            while (day.DayOfWeek == DayOfWeek.Friday) day = day.AddDays(1);
            var att = hr.Attendance;
            await Open(hr, att);
            att.Date = day;
            await att.IdleAsync();
            att.AllPresentCommand.Execute(null);
            var mine = att.Rows.Single(r => r.EmployeeId == empId);
            mine.CheckIn = "06:25";
            mine.CheckOut = "16:00";
            await Shot("07-تسجيل-حضور-اليوم", att);
            await att.SaveCommand.ExecuteAsync();
            await Shot("08-الحضور-بعد-الحفظ-متأخر-25-دقيقة", att);

            // 6) سلفة بأقساط تبدأ من رواتب الشهر الماضي
            var ded = hr.Deductions;
            await Open(hr, ded);
            ded.Employee = ded.Employees.Single(e => e.Id == empId);
            ded.Kind = ded.Kinds.Single(k => k.Value == EmployeeDeductionKind.Loan);
            ded.Amount = 300_000;
            ded.Installment = 100_000;
            ded.StartMonth = day.Month;
            ded.StartYear = day.Year;
            await Shot("09-سلفة-بأقساط", ded);
            await ded.SaveCommand.ExecuteAsync();
            Assert.True(dialogs.Errors.Count == 0, string.Join(" | ", dialogs.Errors));
            await PreviewShot("10-سند-صرف-السلفة", dialogs.Reports.Last());

            // 7) مقياس الحافز الشهري
            var scale = hr.Section<IncentiveSettingsSectionViewModel>();
            await Open(hr, scale);
            async Task Tier(decimal min, decimal max, decimal amount)
            {
                await scale.NewCommand.ExecuteAsync();
                scale.Editor!.MinScore = min;
                scale.Editor.MaxScore = max;
                scale.Editor.Amount = amount;
                await scale.SaveCommand.ExecuteAsync();
            }
            await Tier(60, 79.99m, 50_000);
            await Tier(80, 100, 100_000);
            await Shot("11-مقياس-الحافز", scale);

            // 8) التقييم الشهري: الانضباط من الحضور، والأداء والمهارات من المسؤول
            var inc = hr.Incentives;
            await Open(hr, inc);
            var irow = inc.Rows.Single(r => r.EmployeeId == empId);
            irow.Performance = 90;
            irow.Skills = 85;
            await inc.SaveAllCommand.ExecuteAsync();
            await Shot("12-التقييم-الشهري", inc);

            // 9) الرواتب: توليد ← اعتماد ← طباعة
            var pay = hr.Payroll;
            await Open(hr, pay);
            await pay.GenerateCommand.ExecuteAsync();
            await Shot("13-مسودة-الرواتب", pay);
            await pay.ApproveCommand.ExecuteAsync();
            await Shot("14-الرواتب-معتمدة", pay);
            await pay.PrintCommand.ExecuteAsync();
            Assert.True(dialogs.Errors.Count == 0, string.Join(" | ", dialogs.Errors));
            await PreviewShot("15-كشف-الرواتب", dialogs.Reports.Last());

            // 10) العمال الوقتيون: كشف اليوم ← صرف الأجر بوصل
            var temps = hr.TempWorkers;
            await Open(hr, temps);
            foreach (var d in Enumerable.Range(1, 3))
            {
                temps.Date = DateTime.Today.AddDays(-d);
                await temps.IdleAsync();
                temps.Rows.Single(r => r.FullName == "عباس جاسم").Days = d == 2 ? 0.5m : 1;
                await temps.SaveDayCommand.ExecuteAsync();
            }
            temps.Date = DateTime.Today;
            await temps.IdleAsync();
            temps.Rows.Single(r => r.FullName == "عباس جاسم").Days = 1;
            await Shot("16-كشف-العمال-الوقتيين", temps);
            await temps.SaveDayCommand.ExecuteAsync();
            await Shot("17-المستحق-غير-المصروف", temps);
            await temps.PayCommand.ExecuteAsync(temps.Balances.Single(b => b.FullName == "عباس جاسم"));
            Assert.True(dialogs.Errors.Count == 0, string.Join(" | ", dialogs.Errors));
            await Shot("18-بعد-صرف-الأجر", temps);
            await PreviewShot("19-وصل-أجور-العامل-الوقتي", dialogs.Reports.Last());

            main.Close();
        });

        _out.WriteLine($"دليل الموارد البشرية: {shots.Count} صورة → {Path.Combine(UiThread.OutputDir, hrDir)}");
        Assert.Equal(19, shots.Count);
        Assert.Empty(dialogs.Errors);
        lock (UiThread.Unhandled) Assert.True(UiThread.Unhandled.Count == 0, string.Join("\n", UiThread.Unhandled.Select(e => e.ToString())));
    }

    /// <summary>
    /// الدليل المصوَّر للوصفات المخصصة بمثالين:
    /// 1) عميل: «مطعم الحسون» يريد ماء 500 مل بلاصق باسمه ← لاصقه يحل محل اللاصق العام.
    /// 2) مناسبة: «ملصق رمضان» بلا عميل ← يحل محل اللاصق العام، ويُضاف شريط ذهبي على الغطاء (مكوّن إضافي).
    /// ثم إنتاج اليوم بكل وصفة، وأرصدة مخزن المواد بعده. صورة بعد كل خطوة في مجلد tutorial-recipes.
    /// </summary>
    [Fact]
    public async Task Illustrated_guide_custom_recipes()
    {
        var server = Environment.GetEnvironmentVariable("ERP_UI_SQL_CONNECTION");
        if (server is null) { _out.WriteLine("ERP_UI_SQL_CONNECTION غير معيّن — تخطي"); return; }
        const string recDir = "tutorial-recipes";

        var suffix = Guid.NewGuid().ToString("N")[..6];
        var controlCs = new SqlConnectionStringBuilder(server) { InitialCatalog = $"ERP_TutRcCtl_{suffix}" }.ConnectionString;
        var install = await new ProvisioningService().InstallAsync(new InstallRequest(
            controlCs, "مصنع مياه البصرة", $"ERP_TutRcPrj_{suffix}", "المدير العام", "admin", "Admin@123", DemoData: true));
        Assert.True(install.Success, install.ErrorMessage);
        await using (var db = new ERP.Data.ProjectDb.ProjectDbContext(new DbContextOptionsBuilder<ERP.Data.ProjectDb.ProjectDbContext>()
                         .UseSqlServer(install.ProjectConnectionString).Options))
        {
            db.Customers.Add(new Customer { Name = "مطعم الحسون", CustomerType = CustomerType.Direct, Province = "البصرة" });
            await db.SaveChangesAsync();
        }

        var dialogs = new TutorialDialogs();
        var shots = new List<string>();
        await UiThread.RunAsync(async () =>
        {
            var nav = new CapturingNavigator();
            var login = new LoginViewModel(new AuthService(controlCs), dialogs, nav) { Username = "admin" };
            await login.LoginCommand.ExecuteAsync("Admin@123");
            Assert.Null(login.ErrorMessage);
            await nav.ProjectSelection!.OpenCommand.ExecuteAsync(null);
            var shell = nav.Shell!;
            var main = new MainWindow { DataContext = shell, ShowActivated = false, Width = 1440, Height = 900,
                                        Left = 0, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual, WindowState = WindowState.Normal };
            main.Show();

            async Task Shot(string name, ViewModelBase? busy = null)
            {
                if (busy is not null) await busy.IdleAsync();
                await UiThread.SettleAsync();
                RefreshBindings(main);
                await UiThread.SettleAsync();
                Assert.True(dialogs.Errors.Count == 0, $"قبل الصورة {name}: " + string.Join(" | ", dialogs.Errors));
                shots.Add(UiThread.Save((FrameworkElement)main.Content, $"{recDir}/{name}.png"));
            }
            async Task Open(ModuleViewModel module, SectionViewModel section)
            {
                module.SelectedTab = section;
                await module.LastActivation;
                await section.IdleAsync();
                await UiThread.SettleAsync();
            }

            // ---------- التحضير: المواد الخاصة أصناف مخزنية تُشترى وتدخل مخزن المواد ----------
            var wh = shell.Open<WarehouseModuleViewModel>(ModuleCode.Warehouse);
            await wh.IdleAsync();
            var items = wh.Section<ItemsSectionViewModel>();
            await Open(wh, items);
            async Task NewItem(string code, string name, string? shot)
            {
                await items.NewCommand.ExecuteAsync();
                items.Editor!.ItemCode = code;
                items.Editor.ItemName = name;
                items.Editor.SourcingMethod = SourcingMethod.Purchased;
                if (shot is not null) await Shot(shot, items);
                await items.SaveCommand.ExecuteAsync();
                await items.IdleAsync();
            }
            await NewItem("RM-LBL-HSN", "لاصق مطعم الحسون 500 مل", "01-اللاصق-الخاص-صنف-مخزني");
            await NewItem("RM-LBL-RMD", "ملصق رمضان 500 مل", null);
            await NewItem("RM-GLD", "شريط ذهبي للغطاء", null);

            var raw = wh.Workspaces.First(w => w.WarehouseType == WarehouseType.RawMaterial);
            await Open(wh, raw);
            raw.Operation = raw.OperationOptions.Single(o => o.Value == StockDocumentType.Receipt);
            raw.PartyName = "مطبعة الفيحاء — فاتورة 912";
            async Task Line(string code, decimal qty, string batch)
            {
                raw.LineItem = raw.ItemsLookup.Single(i => i.ItemCode == code);
                await raw.IdleAsync();
                raw.LineQuantity = qty;
                raw.LineNewBatch = batch;
                raw.LineExpiry = DateTime.Today.AddYears(2);
                await raw.AddLineCommand.ExecuteAsync();
            }
            await Line("RM-LBL-HSN", 2000, "HSN-01");
            await Line("RM-LBL-RMD", 3000, "RMD-01");
            await Line("RM-GLD", 3000, "GLD-01");
            await Shot("02-استلام-المواد-الخاصة", raw);
            await raw.SaveCommand.ExecuteAsync();

            // ---------- المثال الأول: وصفة عميل ----------
            var prod = shell.Open<ProductionModuleViewModel>(ModuleCode.Production);
            await prod.IdleAsync();
            var recipes = prod.Section<CustomRecipesSectionViewModel>();
            await Open(prod, recipes);
            await Shot("03-الوصفات-المخصصة", recipes);

            await recipes.NewCommand.ExecuteAsync();
            recipes.Editor!.Name = "وصفة مطعم الحسون";
            recipes.Editor.FinishedItemId = recipes.FinishedItems.Single(i => i.ItemCode == "W-500").Id;
            recipes.Editor.CustomerId = recipes.Customers.Single(c => c.Name == "مطعم الحسون").Id;
            await Shot("04-وصفة-جديدة-لعميل", recipes);
            await recipes.SaveCommand.ExecuteAsync();
            await recipes.IdleAsync();

            Item Raw(string code) => recipes.RawItems.Single(i => i.ItemCode == code);
            recipes.SelectedRecipe = recipes.Items.Single(r => r.Name == "وصفة مطعم الحسون");
            await recipes.IdleAsync();
            recipes.NewComponent = Raw("RM-LBL-HSN");
            recipes.NewLabel = "لاصق أمامي";
            recipes.NewQuantity = 1;
            recipes.NewReplaces = Raw("RM-LBL");
            await Shot("05-اللاصق-الخاص-يحل-محل-العام", recipes);
            await recipes.AddLineCommand.ExecuteAsync();
            await Shot("06-وصفة-العميل-جاهزة", recipes);

            // ---------- المثال الثاني: ملصق مناسبة بلا عميل + مكوّن إضافي ----------
            await recipes.NewCommand.ExecuteAsync();
            recipes.Editor!.Name = "ملصق رمضان";
            recipes.Editor.FinishedItemId = recipes.FinishedItems.Single(i => i.ItemCode == "W-500").Id;
            await recipes.SaveCommand.ExecuteAsync();
            await recipes.IdleAsync();
            recipes.SelectedRecipe = recipes.Items.Single(r => r.Name == "ملصق رمضان");
            await recipes.IdleAsync();
            recipes.NewComponent = Raw("RM-LBL-RMD");
            recipes.NewLabel = "لاصق أمامي";
            recipes.NewQuantity = 1;
            recipes.NewReplaces = Raw("RM-LBL");
            await recipes.AddLineCommand.ExecuteAsync();
            recipes.NewComponent = Raw("RM-GLD");
            recipes.NewLabel = "شريط ذهبي على الغطاء";
            recipes.NewQuantity = 1;
            recipes.NewReplaces = null;
            await Shot("07-مكوّن-إضافي-بلا-استبدال", recipes);
            await recipes.AddLineCommand.ExecuteAsync();
            await Shot("08-ملصق-المناسبة-جاهز", recipes);

            // ---------- الإنتاج بكل وصفة ----------
            var daily = prod.Daily;
            await Open(prod, daily);
            var l1 = daily.Lines[0];
            l1.Product = daily.Products.Single(i => i.ItemCode == "W-500");
            l1.Level = l1.Levels.Single(l => l.LevelName == "كارتون");
            l1.Recipe = l1.Recipes.Single(r => r.Name == "وصفة مطعم الحسون");
            l1.Packs = 10;
            await Shot("09-إنتاج-اليوم-بوصفة-العميل", daily);
            await daily.SaveCommand.ExecuteAsync();
            await Shot("10-بعد-الإنتاج-التشغيلة-باسم-الوصفة", daily);

            var l2 = daily.Lines[0];
            l2.Product = daily.Products.Single(i => i.ItemCode == "W-500");
            l2.Level = l2.Levels.Single(l => l.LevelName == "كارتون");
            l2.Recipe = l2.Recipes.Single(r => r.Name == "ملصق رمضان");
            l2.Packs = 20;
            await daily.SaveCommand.ExecuteAsync();
            await Shot("11-إنتاج-ملصق-رمضان", daily);

            shell.Open<WarehouseModuleViewModel>(ModuleCode.Warehouse);
            await Open(wh, raw);
            SelectInnerTab(main, "الأرصدة الحالية");
            await Shot("12-أرصدة-المواد-بعد-الإنتاج", raw);
            Assert.Contains(raw.Balances, b => b.ItemCode == "RM-LBL-HSN" && b.Quantity == 2000 - 120);
            Assert.Contains(raw.Balances, b => b.ItemCode == "RM-LBL-RMD" && b.Quantity == 3000 - 240);
            Assert.Contains(raw.Balances, b => b.ItemCode == "RM-GLD" && b.Quantity == 3000 - 240);

            main.Close();
        });

        _out.WriteLine($"دليل الوصفات المخصصة: {shots.Count} صورة → {Path.Combine(UiThread.OutputDir, recDir)}");
        Assert.Equal(12, shots.Count);
        Assert.Empty(dialogs.Errors);
        lock (UiThread.Unhandled) Assert.True(UiThread.Unhandled.Count == 0, string.Join("\n", UiThread.Unhandled.Select(e => e.ToString())));
    }

    /// <summary>
    /// دليل «أمر يومي واحد بكل الأصناف»: متغير مطعم ومناسبة من المعالج، ثم أمر اليوم بخمسة سطور للمنتج نفسه
    /// (شرنك وكارتون، أساسي ومطاعم ومناسبة)، وفحص المواد والقالب والتكرار، ثم المتغير في البيع والتحميل والأرصدة.
    /// </summary>
    [Fact]
    public async Task Illustrated_guide_production_variants()
    {
        var server = Environment.GetEnvironmentVariable("ERP_UI_SQL_CONNECTION");
        if (server is null) { _out.WriteLine("ERP_UI_SQL_CONNECTION غير معيّن — تخطي"); return; }
        const string dir = "tutorial-variants";

        var suffix = Guid.NewGuid().ToString("N")[..6];
        var controlCs = new SqlConnectionStringBuilder(server) { InitialCatalog = $"ERP_TutVrCtl_{suffix}" }.ConnectionString;
        var install = await new ProvisioningService().InstallAsync(new InstallRequest(
            controlCs, "مصنع مياه البصرة", $"ERP_TutVrPrj_{suffix}", "المدير العام", "admin", "Admin@123", DemoData: true));
        Assert.True(install.Success, install.ErrorMessage);
        await using (var db = new ERP.Data.ProjectDb.ProjectDbContext(new DbContextOptionsBuilder<ERP.Data.ProjectDb.ProjectDbContext>()
                         .UseSqlServer(install.ProjectConnectionString).Options))
        {
            db.Customers.AddRange(new Customer { Name = "مطعم الحسون", Province = "البصرة" }, new Customer { Name = "مطعم الياس", Province = "البصرة" });
            // أدوار قائمة مواد ماء 500 مل (كما يضعها قالب التعبئة)، ووحدة شرنك إلى جانب الكارتون
            var w500 = await db.Items.SingleAsync(i => i.ItemCode == "W-500");
            foreach (var (code, role) in new[] { ("RM-PRE", "امبولة"), ("RM-CAP", "غطاء"), ("RM-LBL", "لاصق") })
                await db.BOMLines.Where(l => l.BOM.FinishedItemId == w500.Id && l.RawMaterialItem.ItemCode == code)
                        .ExecuteUpdateAsync(u => u.SetProperty(l => l.ComponentRole, role));
            var piece = await db.ItemPackagingLevels.SingleAsync(l => l.ItemId == w500.Id && l.EquivalentBaseUnits == 1);
            db.ItemPackagingLevels.Add(new ItemPackagingLevel { ItemId = w500.Id, LevelName = "شرنك", ParentLevelId = piece.Id, ContainsQuantity = 6, EquivalentBaseUnits = 6 });
            await db.SaveChangesAsync();
        }

        var dialogs = new TutorialDialogs();
        var shots = new List<string>();
        var step = "الدخول";
        var run = UiThread.RunAsync(async () =>
        {
            var nav = new CapturingNavigator();
            var login = new LoginViewModel(new AuthService(controlCs), dialogs, nav) { Username = "admin" };
            await login.LoginCommand.ExecuteAsync("Admin@123");
            Assert.Null(login.ErrorMessage);
            await nav.ProjectSelection!.OpenCommand.ExecuteAsync(null);
            var shell = nav.Shell!;
            var main = new MainWindow { DataContext = shell, ShowActivated = false, Width = 1440, Height = 900,
                                        Left = 0, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual, WindowState = WindowState.Normal };
            main.Show();

            async Task Shot(string name, ViewModelBase? busy = null)
            {
                if (busy is not null) await busy.IdleAsync();
                await UiThread.SettleAsync();
                RefreshBindings(main);
                await UiThread.SettleAsync();
                Assert.True(dialogs.Errors.Count == 0, $"قبل الصورة {name}: " + string.Join(" | ", dialogs.Errors));
                shots.Add(UiThread.Save((FrameworkElement)main.Content, $"{dir}/{name}.png"));
                step = $"بعد الصورة {name}";
            }
            async Task Open(ModuleViewModel module, SectionViewModel section)
            {
                module.SelectedTab = section;
                await module.LastActivation;
                await section.IdleAsync();
                await UiThread.SettleAsync();
            }

            step = "المعالج";
            // ---------- 1) متغير جديد بخطوة واحدة: مطعم، ثم مناسبة ----------
            var prod = shell.Open<ProductionModuleViewModel>(ModuleCode.Production);
            await prod.IdleAsync();
            var recipes = prod.Section<CustomRecipesSectionViewModel>();
            await Open(prod, recipes);
            async Task Variant(string? customer, string name, string? shot)
            {
                recipes.OpenWizardCommand.Execute(null);
                recipes.WizardProduct = recipes.FinishedItems.Single(i => i.ItemCode == "W-500");
                await recipes.IdleAsync();
                recipes.WizardCustomer = customer is null ? CustomRecipesSectionViewModel.NoCustomer : recipes.WizardCustomers.Single(c => c.Name == customer);
                recipes.WizardName = name;
                recipes.WizardRoles.Single(r => r.Role == "لاصق").CreateNew = true;
                if (customer == "مطعم الحسون") recipes.WizardRoles.Single(r => r.Role == "غطاء").CreateNew = true;
                if (shot is not null) await Shot(shot, recipes);
                await recipes.CreateVariantCommand.ExecuteAsync();
                await recipes.IdleAsync();
            }
            await Variant("مطعم الحسون", "مطعم الحسون", "01-متغير-جديد-لمطعم");
            await Shot("02-المتغير-جاهز-غطاء-وليبل-خاص", recipes);
            await Variant("مطعم الياس", "مطعم الياس", null);
            await Variant(null, "زواج سعيد", "03-متغير-مناسبة-بلا-عميل");

            step = "استلام المواد";
            // المواد الخاصة الجديدة تُستلم كأي مادة أولية
            var wh = shell.Open<WarehouseModuleViewModel>(ModuleCode.Warehouse);
            await wh.IdleAsync();
            var raw = wh.Workspaces.First(w => w.WarehouseType == WarehouseType.RawMaterial);
            await Open(wh, raw);
            raw.Operation = raw.OperationOptions.Single(o => o.Value == StockDocumentType.Receipt);
            raw.PartyName = "مطبعة الفيحاء — فاتورة 1204";
            foreach (var name in new[] { "لاصق مطعم الحسون", "غطاء مطعم الحسون", "لاصق مطعم الياس", "لاصق زواج سعيد" })
            {
                raw.LineItem = raw.ItemsLookup.Single(i => i.ItemName == name);
                await raw.IdleAsync();
                raw.LineQuantity = 2000;
                raw.LineNewBatch = "V-01";
                raw.LineExpiry = DateTime.Today.AddYears(2);
                await raw.AddLineCommand.ExecuteAsync();
            }
            await Shot("04-استلام-الأغطية-والليبلات-الخاصة", raw);
            await raw.SaveCommand.ExecuteAsync();

            step = "أمر اليوم";
            // ---------- 2) أمر اليوم: المنتج نفسه خمس مرات ----------
            shell.Open<ProductionModuleViewModel>(ModuleCode.Production);
            var daily = prod.Daily;
            await Open(prod, daily);
            await daily.LoadAsync();
            var lines = new[] { ("شرنك", (string?)null, 40m), ("كارتون", null, 30m), ("كارتون", "مطعم الحسون", 15m), ("كارتون", "مطعم الياس", 10m), ("شرنك", "زواج سعيد", 20m) };
            daily.Lines.Clear();
            foreach (var (level, variant, packs) in lines)
            {
                daily.AddLineCommand.Execute(null);
                var l = daily.Lines[^1];
                l.Product = daily.Products.Single(i => i.ItemCode == "W-500");
                l.Level = l.Levels.Single(x => x.LevelName == level);
                l.Recipe = variant is null ? DailyProductionSectionViewModel.Basic : l.Recipes.Single(r => r.Name == variant);
                l.Packs = packs;
            }
            await Shot("05-أمر-يوم-واحد-بكل-الأصناف", daily);
            step = "فحص المواد";
            await daily.PreviewCommand.ExecuteAsync();
            Assert.False(daily.HasShortage);
            await Shot("06-فحص-المواد-قبل-التسجيل", daily);
            daily.TemplateName = "أمر اليوم المعتاد";
            await daily.SaveTemplateCommand.ExecuteAsync();
            step = "تسجيل الإنتاج";
            await daily.SaveCommand.ExecuteAsync();
            await Shot("07-بعد-التسجيل-تشغيلة-لكل-متغير", daily);
            Assert.Equal(5, daily.LastResult.Count);
            step = "تكرار آخر إنتاج";
            await daily.RepeatLastCommand.ExecuteAsync();
            await Shot("08-تكرار-آخر-إنتاج-أو-القالب", daily);
            daily.Lines.Clear();
            daily.AddLineCommand.Execute(null);

            step = "متغيرات المنتج";
            // ---------- 3) متغيرات المنتج ----------
            var variants = prod.Section<VariantStockSectionViewModel>();
            await Open(prod, variants);
            await Shot("09-متغيرات-المنتج-الرصيد-والكلفة", variants);

            step = "البيع";
            // ---------- 4) البيع: المتغير بالاسم، والمتاح يحترم الحجز ----------
            var sales = shell.Open<ERP.Presentation.ViewModels.Sales.SalesModuleViewModel>(ModuleCode.Sales);
            await sales.IdleAsync();
            var inv = sales.Invoice;
            await Open(sales, inv);
            inv.Customer = inv.Customers.First(c => c.CustomerType == CustomerType.Direct && !c.Name.StartsWith("مطعم"));
            inv.Warehouse = inv.Warehouses.First(w => w.WarehouseType == WarehouseType.FinishedGoods);
            inv.LineItem = inv.ItemsLookup.Single(i => i.ItemCode == "W-500");
            await inv.IdleAsync();
            inv.LineBatch = inv.BatchOptions.Single(b => b.RecipeId != null && b.Label.Contains("زواج سعيد"));
            await inv.IdleAsync();
            inv.LineLevel = inv.LevelOptions.Single(l => l.LevelName == "شرنك");
            inv.LineQuantity = 5;
            await inv.AddLineCommand.ExecuteAsync();
            inv.LineItem = inv.ItemsLookup.Single(i => i.ItemCode == "W-500");
            await inv.IdleAsync();
            await Shot("10-البيع-بمتغير-المناسبة-والمتاح-للزبون", inv);

            step = "طلب التحميل";
            // ---------- 5) طلب التحميل: سطر عام وسطر باسم المطعم ----------
            var reps = shell.Open<ERP.Presentation.ViewModels.Reps.RepsModuleViewModel>(ModuleCode.Reps);
            await reps.IdleAsync();
            var loads = reps.Section<ERP.Presentation.ViewModels.Reps.LoadOrdersSectionViewModel>();
            await Open(reps, loads);
            loads.Lines.Clear();
            foreach (var (variant, qty) in new[] { ((string?)null, 40m), ("مطعم الحسون", 15m) })
            {
                loads.AddLineCommand.Execute(null);
                var l = loads.Lines[^1];
                l.Product = loads.Products.Single(i => i.ItemCode == "W-500");
                l.Recipe = variant is null ? l.Recipes[0] : l.Recipes.Single(r => r.Name == variant);
                l.Level = l.Levels.Single(x => x.LevelName == "كارتون");
                l.Quantity = qty;
            }
            await Shot("11-طلب-تحميل-بمتغير-المطعم", loads);
            loads.Lines.Clear();

            step = "الأرصدة";
            // ---------- 6) أرصدة مخزن المنتج التام بعمود المتغير ----------
            shell.Open<WarehouseModuleViewModel>(ModuleCode.Warehouse);
            var fg = wh.Workspaces.First(w => w.WarehouseType == WarehouseType.FinishedGoods);
            await Open(wh, fg);
            SelectInnerTab(main, "الأرصدة الحالية");
            await Shot("12-أرصدة-المنتج-التام-بالمتغير", fg);
            Assert.Contains(fg.Balances, b => b.ItemCode == "W-500" && b.Variant == "مطعم الحسون" && b.Quantity == 180);
            Assert.Contains(fg.Balances, b => b.ItemCode == "W-500" && b.Variant == "زواج سعيد" && b.Quantity == 120);   // الفاتورة مسودة لم تُرحَّل

            main.Close();
        });
        // تعليق أي خطوة يظهر باسمها بدل انتظار مهلة CI كاملة
        if (await Task.WhenAny(run, Task.Delay(TimeSpan.FromMinutes(5))) != run)
            Assert.Fail($"توقف دليل المتغيرات عند: {step} — أخطاء: {string.Join(" | ", dialogs.Errors)}");
        await run;

        _out.WriteLine($"دليل المتغيرات: {shots.Count} صورة → {Path.Combine(UiThread.OutputDir, dir)}");
        Assert.Equal(12, shots.Count);
        Assert.Empty(dialogs.Errors);
        lock (UiThread.Unhandled) Assert.True(UiThread.Unhandled.Count == 0, string.Join("\n", UiThread.Unhandled.Select(e => e.ToString())));
    }

    /// <summary>
    /// نماذج التحرير مرتبطة بالكيان نفسه (بلا إشعار تغيير)، فما يكتبه المستخدم يصل للكيان مباشرة،
    /// أما ما يضبطه الاختبار برمجيًا فلا يظهر في الحقول. نعيد قراءة كل الربطات قبل التصوير لتطابق الصورة ما سيُحفَظ.
    /// </summary>
    private static void RefreshBindings(DependencyObject root)
    {
        foreach (var d in Descendants<FrameworkElement>(root).Prepend((FrameworkElement)root))
        {
            var values = d.GetLocalValueEnumerator();
            while (values.MoveNext())
                if (BindingOperations.GetBindingExpressionBase(d, values.Current.Property) is { } expression)
                    expression.UpdateTarget();
        }
    }

    /// <summary>يختار تبويبًا داخليًا (مثل "الأرصدة الحالية") في الشاشة الظاهرة.</summary>
    private static void SelectInnerTab(DependencyObject root, string header)
    {
        foreach (var tab in Descendants<TabItem>(root))
            if (tab.Header as string == header && tab.IsVisible) { tab.IsSelected = true; return; }
        throw new InvalidOperationException($"لا يوجد تبويب ظاهر بعنوان {header}");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }
}
