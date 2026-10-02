using System.Collections.ObjectModel;
using ERP.Data.Setup;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using Microsoft.Data.SqlClient;

namespace ERP.Presentation.ViewModels.Shell;

/// <summary>
/// معالج الإعداد عند أول تشغيل: الاتصال بـ SQL Server ← (تثبيت جديد كامل) أو (الاتصال بنظام قائم) ←
/// حفظ الإعداد ← شاشة الدخول. لا حاجة لـ SSMS ولا لتعديل أي ملف يدويًا.
/// </summary>
public class SetupViewModel : ViewModelBase
{
    private readonly INavigator _navigator;
    private readonly IConfigStore _config;
    private string _server = @"localhost";
    private bool _useWindowsAuth = true;
    private string _sqlUser = "sa";
    private string _controlDatabase = "ERP_ControlDB";
    private bool _isNewInstall = true;
    private string _projectName = "مصنع المياه - البصرة";
    private string _projectDatabase = "ERP_Project_WaterFactory";
    private string _adminFullName = "مدير النظام";
    private string _adminUsername = "admin";
    private bool _demoData;
    private string? _connectionMessage;
    private bool _connectionOk;
    private string? _errorMessage;

    public SetupViewModel(INavigator navigator, IConfigStore config, string? reason)
    {
        _navigator = navigator;
        _config = config;
        Reason = reason;
        TestConnectionCommand = new AsyncRelayCommand(TestAsync);
        FinishCommand = new AsyncRelayCommand(FinishAsync);

        // إعداد سابق (مثلًا السيرفر توقف): نملأ الحقول منه
        if (config.LoadControlConnectionString() is { } existing)
        {
            try
            {
                var b = new SqlConnectionStringBuilder(existing);
                _server = b.DataSource;
                _controlDatabase = b.InitialCatalog;
                _useWindowsAuth = b.IntegratedSecurity;
                _sqlUser = b.UserID;
                _isNewInstall = false;
            }
            catch (ArgumentException) { /* ملف تالف: نبدأ بالقيم الافتراضية */ }
        }
    }

    public string? Reason { get; }
    public string ConfigPath => _config.ConfigPath;
    public ObservableCollection<string> Log { get; } = new();

    public string Server { get => _server; set { if (SetProperty(ref _server, value)) ConnectionOk = false; } }
    public bool UseWindowsAuth { get => _useWindowsAuth; set { if (SetProperty(ref _useWindowsAuth, value)) { ConnectionOk = false; OnPropertyChanged(nameof(UseSqlAuth)); } } }
    public bool UseSqlAuth => !UseWindowsAuth;
    public string SqlUser { get => _sqlUser; set { if (SetProperty(ref _sqlUser, value)) ConnectionOk = false; } }
    /// <summary>من PasswordBox (لا يُربط).</summary>
    public string SqlPassword { get; set; } = "";
    public string ControlDatabase { get => _controlDatabase; set => SetProperty(ref _controlDatabase, value); }

    public bool IsNewInstall { get => _isNewInstall; set { if (SetProperty(ref _isNewInstall, value)) OnPropertyChanged(nameof(IsConnectExisting)); } }
    public bool IsConnectExisting { get => !_isNewInstall; set => IsNewInstall = !value; }

    public string ProjectName { get => _projectName; set => SetProperty(ref _projectName, value); }
    public string ProjectDatabase { get => _projectDatabase; set => SetProperty(ref _projectDatabase, value); }
    public string AdminFullName { get => _adminFullName; set => SetProperty(ref _adminFullName, value); }
    public string AdminUsername { get => _adminUsername; set => SetProperty(ref _adminUsername, value); }
    public string AdminPassword { get; set; } = "";
    public string AdminPasswordConfirm { get; set; } = "";
    public bool DemoData { get => _demoData; set => SetProperty(ref _demoData, value); }

    /// <summary>
    /// إن كان اسم الدخول موجودًا من تثبيت سابق في نفس قاعدة التحكم: تُستبدل كلمة مروره بالمدخلة.
    /// بدونه يُرفض التثبيت إن اختلفت كلمة المرور (بدل أن ينجح بحساب لا يمكن الدخول به).
    /// </summary>
    public bool ResetExistingAdminPassword { get; set; }

    public string? ConnectionMessage { get => _connectionMessage; private set => SetProperty(ref _connectionMessage, value); }
    public bool ConnectionOk { get => _connectionOk; private set => SetProperty(ref _connectionOk, value); }
    public string? ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }

    public AsyncRelayCommand TestConnectionCommand { get; }
    public AsyncRelayCommand FinishCommand { get; }

    public string BuildControlConnectionString()
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = Server.Trim(),
            InitialCatalog = ControlDatabase.Trim(),
            TrustServerCertificate = true,
            MultipleActiveResultSets = false,
            ConnectTimeout = 15
        };
        if (UseWindowsAuth) b.IntegratedSecurity = true;
        else { b.UserID = SqlUser.Trim(); b.Password = SqlPassword; }
        return b.ConnectionString;
    }

    private async Task TestAsync()
    {
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            var (ok, message) = await ProvisioningService.TestConnectionAsync(BuildControlConnectionString());
            ConnectionOk = ok;
            ConnectionMessage = message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task FinishAsync()
    {
        ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(Server)) { ErrorMessage = "أدخل اسم السيرفر (مثل localhost أو .\\SQLEXPRESS)"; return; }
        if (!ConnectionOk) await TestAsync();
        if (!ConnectionOk) { ErrorMessage = ConnectionMessage; return; }

        var controlCs = BuildControlConnectionString();
        IsBusy = true;
        lock (Log) Log.Clear();
        try
        {
            if (IsNewInstall)
            {
                if (AdminPassword != AdminPasswordConfirm) { ErrorMessage = "كلمتا مرور المدير غير متطابقتين"; return; }
                // Progress<T> بلا سياق واجهة (اختبارات/خدمة) يستدعي من خيوط متعددة متزامنة: الإضافة تحت قفل حتى لا يضيع سطر
                var progress = new Progress<string>(m => { lock (Log) Log.Add(m); });
                var result = await new ProvisioningService().InstallAsync(new InstallRequest(
                    controlCs, ProjectName, ProjectDatabase.Trim(), AdminFullName, AdminUsername, AdminPassword, DemoData,
                    ResetExistingAdminPassword ? ExistingAdminPolicy.ResetPassword : ExistingAdminPolicy.RequireSamePassword), progress);
                if (!result.Success) { ErrorMessage = result.ErrorMessage; return; }
            }
            else
            {
                // الاتصال بنظام قائم: يجب أن تكون قاعدة التحكم مثبّتة
                await using var conn = new SqlConnection(controlCs);
                await conn.OpenAsync();
                await using var cmd = new SqlCommand("SELECT OBJECT_ID('Projects', 'U')", conn);
                if (await cmd.ExecuteScalarAsync() is DBNull or null)
                {
                    ErrorMessage = $"القاعدة {ControlDatabase} لا تحتوي نظامًا مثبّتًا. اختر \"تثبيت جديد\".";
                    return;
                }
            }
            _config.SaveControlConnectionString(controlCs);
            _navigator.UseControlConnection(controlCs);
        }
        catch (SqlException ex)
        {
            ErrorMessage = $"تعذّر الاتصال بقاعدة التحكم: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
