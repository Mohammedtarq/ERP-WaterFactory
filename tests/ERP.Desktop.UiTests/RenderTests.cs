using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using ERP.Data.Services;
using ERP.Data.Setup;
using ERP.Desktop.Printing;
using ERP.Desktop.Views.Shell;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace ERP.Desktop.UiTests;

public class NullDialogs : IDialogService
{
    public List<string> Messages { get; } = new();
    public void Info(string message) => Messages.Add(message);
    public void Error(string message) => Messages.Add("خطأ: " + message);
    public bool Confirm(string message) => false;      // لا ترحيل ولا حذف أثناء التصوير
    public void ShowReport(ReportDocument report) { }
    public string? PickImageFile() => null;
}

public class CapturingNavigator : INavigator
{
    public ProjectSelectionViewModel? ProjectSelection;
    public MainShellViewModel? Shell;
    public void ShowProjectSelection(ProjectSelectionViewModel vm) => ProjectSelection = vm;
    public void ShowMainShell(MainShellViewModel vm) => Shell = vm;
    public void ShowLogin() { }
    public void ShowSetup(string? reason) { }
    public void UseControlConnection(string controlConnectionString) { }
}

public class MemoryConfig : IConfigStore
{
    public string? Value;
    public string ConfigPath => "memory";
    public string? LoadControlConnectionString() => Value;
    public void SaveControlConnectionString(string connectionString) => Value = connectionString;
}

[CollectionDefinition("ui", DisableParallelization = true)] public class UiCollection { }

/// <summary>
/// يرسم كل الشاشات فعليًا عبر WPF (لا مجرد تحليل XAML): أي StaticResource مفقود أو خطأ XAML
/// أو خطأ ربط (خاصية غير موجودة) يُفشل الاختبار، وتُحفظ صورة كل شاشة للمراجعة البصرية.
/// </summary>
[Collection("ui")]
public class RenderTests
{
    private readonly ITestOutputHelper _out;
    public RenderTests(ITestOutputHelper output) => _out = output;

    /// <summary>أخطاء الربط الحقيقية: خاصية غير موجودة في الـ ViewModel أو تحويل فاشل.</summary>
    private static List<string> RealBindingErrors()
    {
        lock (UiThread.BindingErrors)
            return UiThread.BindingErrors
                .Where(m => m.Contains("BindingExpression path error") || m.Contains("Cannot convert") || m.Contains("ConvertBack cannot"))
                .Distinct().ToList();
    }

    private static void ClearCollected()
    {
        lock (UiThread.BindingErrors) UiThread.BindingErrors.Clear();
        lock (UiThread.Unhandled) UiThread.Unhandled.Clear();
    }

    [Fact]
    public async Task Every_view_and_window_loads_and_renders_without_data()
    {
        ClearCollected();
        var failures = new List<string>();
        var count = 0;
        await UiThread.RunAsync(async () =>
        {
            var types = typeof(App).Assembly.GetTypes()
                .Where(t => !t.IsAbstract && typeof(FrameworkElement).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null)
                .OrderBy(t => t.FullName).ToList();
            foreach (var t in types)
            {
                try
                {
                    var el = (FrameworkElement)Activator.CreateInstance(t)!;
                    if (el is Window w)
                    {
                        w.WindowState = WindowState.Normal;   // المكبَّرة لا تُعرض مع ShowActivated=false
                        w.ShowActivated = false;
                        w.ShowInTaskbar = false;
                        w.WindowStartupLocation = WindowStartupLocation.Manual;
                        w.Left = 0; w.Top = 0;
                        w.Show();
                        await UiThread.SettleAsync();
                        UiThread.Save((FrameworkElement)w.Content, $"00-empty/{t.Name}.png");
                        w.Close();
                    }
                    else
                    {
                        var host = new Window { Width = 1280, Height = 800, Content = el, ShowActivated = false, ShowInTaskbar = false,
                                                FlowDirection = FlowDirection.RightToLeft, Left = 0, Top = 0,
                                                WindowStartupLocation = WindowStartupLocation.Manual };
                        host.Show();
                        await UiThread.SettleAsync();
                        UiThread.Save(el, $"00-empty/{t.Name}.png");
                        host.Close();
                    }
                    count++;
                }
                catch (Exception ex)
                {
                    failures.Add($"{t.FullName}: {ex.GetBaseException().Message}");
                }
            }

            // معالج الإعداد كما يراه المستخدم أول مرة
            var setup = new SetupWindow { DataContext = new SetupViewModel(new CapturingNavigator(), new MemoryConfig(), null),
                                          ShowActivated = false, ShowInTaskbar = false, Left = 0, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual };
            setup.Show();
            await UiThread.SettleAsync();
            UiThread.Save((FrameworkElement)setup.Content, "00-empty/SetupWindow_first_run.png");
            setup.Close();

            // معاينة الطباعة بمستند فعلي
            var report = new ReportDocument { CompanyName = "مصنع مياه البصرة", Title = "فاتورة مبيعات", Stamp = "مسودة — غير مرحّلة", PrintedBy = "المدير" };
            report.Field("رقم الفاتورة", "INV-000123").Field("العميل", "محل أبو حيدر").Field("التاريخ", "2026/09/30");
            report.Columns.AddRange(new[] { "#", "الصنف", "الوحدة", "الكمية", "القطع", "السعر", "المبلغ" });
            report.Rows.Add(new[] { "1", "ماء 500 مل (W-500)", "كارتون", "5", "60", "2,400", "12,000" });
            report.Total("الإجمالي", "14,280 د.ع", true);
            report.Signatures.AddRange(new[] { "المستلم", "المحاسب" });
            var preview = new ReportPreviewWindow(report) { ShowActivated = false, ShowInTaskbar = false, Left = 0, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual };
            preview.Show();
            await UiThread.SettleAsync();
            UiThread.Save((FrameworkElement)preview.Content, "00-empty/ReportPreview_with_invoice.png");
            preview.Close();
        });

        _out.WriteLine($"رُسمت {count} شاشة → {UiThread.OutputDir}");
        Assert.True(count > 40, $"عدد الشاشات المرسومة قليل: {count}");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        lock (UiThread.Unhandled) Assert.True(UiThread.Unhandled.Count == 0, string.Join("\n", UiThread.Unhandled.Select(e => e.ToString())));
    }

    /// <summary>البحث داخل القائمة المنسدلة: الكتابة تصفّي النتائج دون تغيير الاختيار، وEnter يعتمد أول نتيجة.</summary>
    [Fact]
    public async Task Dropdown_search_filters_and_commits_on_enter()
    {
        ClearCollected();
        await UiThread.RunAsync(async () =>
        {
            var items = new System.Collections.ObjectModel.ObservableCollection<Option>
            {
                new("W-500", "ماء 500 مل"), new("W-1500", "ماء 1.5 لتر"), new("RM-CAP", "أغطية زرقاء"), new("RM-LBL", "ملصق أمامي"), new("RM-PRE", "بريفورم")
            };
            var combo = new ComboBox { ItemsSource = items, DisplayMemberPath = "Name", Width = 260 };
            var other = new ListBox { ItemsSource = items };                    // نفس البيانات في عنصر آخر
            var panel = new StackPanel { FlowDirection = FlowDirection.RightToLeft };
            panel.Children.Add(combo);
            panel.Children.Add(other);
            var w = new Window { Content = panel, Width = 420, Height = 420, ShowActivated = true, Left = 0, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual };
            w.Show();
            await UiThread.SettleAsync();
            combo.SelectedItem = items[0];

            combo.IsDropDownOpen = true;
            await UiThread.SettleAsync();
            var search = (TextBox)combo.Template.FindName("PART_Search", combo);
            var results = (ListBox)combo.Template.FindName("PART_Results", combo);
            Assert.Equal(5, results.Items.Count);

            search.Text = "اغطيه";                                              // بدون همزة وبتاء مربوطة مختلفة
            await UiThread.SettleAsync();
            Assert.Equal("RM-CAP", Assert.Single(results.Items.Cast<Option>()).Code);
            Assert.Same(items[0], combo.SelectedItem);                          // الاختيار لم يتغير أثناء الكتابة
            Assert.Equal(5, other.Items.Count);                                 // ولا عنصر آخر تأثر بالتصفية
            UiThread.Save(w, "00-empty/ComboSearch_filtered.png");

            search.Text = "RM-";                                                // البحث بالكود أيضًا
            await UiThread.SettleAsync();
            Assert.Equal(3, results.Items.Count);
            search.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(search)!, 0, System.Windows.Input.Key.Enter) { RoutedEvent = UIElement.PreviewKeyDownEvent });
            await UiThread.SettleAsync();
            Assert.False(combo.IsDropDownOpen);
            Assert.Equal("RM-CAP", ((Option)combo.SelectedItem).Code);
            w.Close();
        });
        lock (UiThread.Unhandled) Assert.True(UiThread.Unhandled.Count == 0, string.Join("\n", UiThread.Unhandled.Select(e => e.ToString())));
    }

    public record Option(string Code, string Name);

    /// <summary>
    /// الرحلة الكاملة على SQL Server حقيقي (LocalDB في CI): تثبيت ببيانات تجريبية ← دخول ←
    /// النافذة الرئيسية ← كل وحدة ← كل تبويب بعد تحميل بياناته، مع صورة لكل واحد.
    /// </summary>
    [Fact]
    public async Task Every_module_and_section_renders_with_real_data()
    {
        var server = Environment.GetEnvironmentVariable("ERP_UI_SQL_CONNECTION");
        if (server is null) { _out.WriteLine("ERP_UI_SQL_CONNECTION غير معيّن — تخطي"); return; }

        var suffix = Guid.NewGuid().ToString("N")[..6];
        var controlCs = new SqlConnectionStringBuilder(server) { InitialCatalog = $"ERP_UiCtl_{suffix}" }.ConnectionString;
        var install = await new ProvisioningService().InstallAsync(new InstallRequest(
            controlCs, "مصنع مياه البصرة", $"ERP_UiPrj_{suffix}", "المدير العام", "admin", "Admin@123", DemoData: true));
        Assert.True(install.Success, install.ErrorMessage);

        ClearCollected();
        var dialogs = new NullDialogs();
        var shots = new List<string>();
        await UiThread.RunAsync(async () =>
        {
            var nav = new CapturingNavigator();
            var auth = new AuthService(controlCs);

            var login = new LoginViewModel(auth, dialogs, nav) { Username = "admin" };
            var loginWindow = new LoginWindow { DataContext = login, ShowActivated = false, Left = 0, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual };
            loginWindow.Show();
            await UiThread.SettleAsync();
            shots.Add(UiThread.Save((FrameworkElement)loginWindow.Content, "01-login.png"));
            await login.LoginCommand.ExecuteAsync("Admin@123");
            loginWindow.Close();
            Assert.Null(login.ErrorMessage);

            var ps = nav.ProjectSelection!;
            var psWindow = new ProjectSelectionWindow { DataContext = ps, ShowActivated = false, Left = 0, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual };
            psWindow.Show();
            await UiThread.SettleAsync();
            shots.Add(UiThread.Save((FrameworkElement)psWindow.Content, "02-project-selection.png"));
            await ps.OpenCommand.ExecuteAsync(null);
            psWindow.Close();
            Assert.Null(ps.ErrorMessage);

            var shell = nav.Shell!;
            var main = new MainWindow { DataContext = shell, ShowActivated = false, Width = 1440, Height = 900,
                                        Left = 0, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual, WindowState = WindowState.Normal };
            main.Show();
            var n = 3;
            foreach (var item in shell.NavItems)
            {
                shell.NavigateCommand.Execute(item);
                await UiThread.SettleAsync();
                var module = shell.CurrentModule;
                if (module is DashboardViewModel dash) await dash.IdleAsync();
                if (module is ModuleViewModel { Dashboard: { } md } mm) { await mm.IdleAsync(); await md.IdleAsync(); }
                await UiThread.SettleAsync();
                var folder = $"{n++:00}-{UiThread.SafeName(item.Title)}";
                shots.Add(UiThread.Save((FrameworkElement)main.Content, $"{folder}/00-الرئيسية.png"));

                if (module is not ModuleViewModel m) continue;
                var i = 1;
                foreach (var section in m.Tabs.OfType<SectionViewModel>().ToList())
                {
                    m.SelectedTab = section;
                    await m.LastActivation;
                    await section.IdleAsync();
                    await UiThread.SettleAsync();
                    shots.Add(UiThread.Save((FrameworkElement)main.Content, $"{folder}/{i++:00}-{UiThread.SafeName(section.Title)}.png"));
                }
            }
            main.Close();
        });

        var errors = RealBindingErrors();
        File.WriteAllLines(Path.Combine(UiThread.OutputDir, "binding-trace.txt"), UiThread.BindingErrors);
        File.WriteAllLines(Path.Combine(UiThread.OutputDir, "dialogs.txt"), dialogs.Messages);
        _out.WriteLine($"{shots.Count} صورة → {UiThread.OutputDir}");
        Assert.True(shots.Count > 50, $"عدد الصور قليل: {shots.Count}");
        Assert.True(errors.Count == 0, "أخطاء ربط:\n" + string.Join("\n", errors));
        Assert.DoesNotContain(dialogs.Messages, m => m.StartsWith("خطأ:"));
        lock (UiThread.Unhandled) Assert.True(UiThread.Unhandled.Count == 0, string.Join("\n", UiThread.Unhandled.Select(e => e.ToString())));
    }
}
