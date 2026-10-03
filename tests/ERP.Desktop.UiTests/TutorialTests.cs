using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Data.Setup;
using ERP.Desktop.Views.Shell;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels;
using ERP.Presentation.ViewModels.Production;
using ERP.Presentation.ViewModels.Shell;
using ERP.Presentation.ViewModels.Warehouse;
using Microsoft.Data.SqlClient;
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
            bom.RoleChoices.Single(c => c.Role == "شرنك").Item = Pick("RM-SHR20");
            bom.RoleChoices.Single(c => c.Role == "غطاء").Item = Pick("RM-CAP330");
            bom.RoleChoices.Single(c => c.Role == "لاصق").Item = Pick("RM-LBL330");
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
