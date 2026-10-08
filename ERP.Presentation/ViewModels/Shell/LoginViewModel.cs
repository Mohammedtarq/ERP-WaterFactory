using System.Collections.ObjectModel;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;

namespace ERP.Presentation.ViewModels.Shell;

public class LoginViewModel : ViewModelBase
{
    private readonly AuthService _auth;
    private readonly IDialogService _dialogs;
    private readonly INavigator _navigator;
    private string _username = "";
    private string? _errorMessage;

    public LoginViewModel(AuthService auth, IDialogService dialogs, INavigator navigator)
    {
        _auth = auth;
        _dialogs = dialogs;
        _navigator = navigator;
        LoginCommand = new AsyncRelayCommand(LoginAsync);
    }

    public string Username { get => _username; set => SetProperty(ref _username, value); }

    /// <summary>كلمة المرور لا تُربط بـ Binding (PasswordBox)، تُمرَّر كمعامل للأمر.</summary>
    public string Password { get; set; } = "";

    public string? ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }

    public AsyncRelayCommand LoginCommand { get; }

    private async Task LoginAsync(object? parameter)
    {
        if (parameter is string pwd) Password = pwd;
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            var result = await _auth.LoginAsync(Username, Password);
            if (!result.Success)
            {
                ErrorMessage = result.ErrorMessage;
                return;
            }
            _navigator.ShowProjectSelection(new ProjectSelectionViewModel(_auth, _dialogs, _navigator, result));
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
        {
            ErrorMessage = "تعذّر الاتصال بقاعدة التحكم. تأكد من تشغيل SQL Server ومن ملف appsettings.json";
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public class ProjectSelectionViewModel : ViewModelBase
{
    private readonly AuthService _auth;
    private readonly IDialogService _dialogs;
    private readonly INavigator _navigator;
    private readonly LoginResult _login;
    private ProjectOption? _selectedProject;
    private string? _errorMessage;

    public ProjectSelectionViewModel(AuthService auth, IDialogService dialogs, INavigator navigator, LoginResult login)
    {
        _auth = auth;
        _dialogs = dialogs;
        _navigator = navigator;
        _login = login;
        FullName = login.FullName;
        Projects = new ObservableCollection<ProjectOption>(login.Projects);
        _selectedProject = Projects.FirstOrDefault();
        OpenCommand = new AsyncRelayCommand(OpenAsync, _ => SelectedProject is not null);
        BackCommand = new RelayCommand(() => _navigator.ShowLogin());
        BeginDeleteCommand = new RelayCommand(p => { if (p is ProjectOption o) { DeleteConfirmText = ""; DeleteTarget = o; } });
        CancelDeleteCommand = new RelayCommand(() => DeleteTarget = null);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync);
    }

    public string FullName { get; }
    public string Greeting => $"أهلًا {FullName}، اختر المشروع";
    public ObservableCollection<ProjectOption> Projects { get; }

    // ---- حذف مشروع (ملاحظة التجربة 16): للمدير، بكتابة اسمه، وبعد نسخة احتياطية تلقائية ----
    private ProjectOption? _deleteTarget;
    private string _deleteConfirmText = "";
    public ProjectOption? DeleteTarget
    {
        get => _deleteTarget;
        private set { if (SetProperty(ref _deleteTarget, value)) { OnPropertyChanged(nameof(IsDeleting)); OnPropertyChanged(nameof(DeletePrompt)); } }
    }
    public bool IsDeleting => DeleteTarget is not null;
    public string DeletePrompt => DeleteTarget is null ? ""
        : $"حذف «{DeleteTarget.ProjectName}» نهائيًا: تُؤخذ نسخة احتياطية تلقائيًا ثم تُحذف قاعدته ({DeleteTarget.DatabaseName}). للتأكيد اكتب اسم المشروع:";
    public string DeleteConfirmText { get => _deleteConfirmText; set => SetProperty(ref _deleteConfirmText, value ?? ""); }
    public RelayCommand BeginDeleteCommand { get; }
    public RelayCommand CancelDeleteCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }

    private async Task DeleteAsync()
    {
        if (DeleteTarget is not { } target) return;
        if (DeleteConfirmText.Trim() != target.ProjectName.Trim()) { ErrorMessage = $"اكتب اسم المشروع كما هو: «{target.ProjectName}»"; return; }
        if (!_dialogs.Confirm($"تأكيد أخير: حذف مشروع «{target.ProjectName}» وقاعدة بياناته نهائيًا؟ (تبقى نسخته الاحتياطية على السيرفر)")) return;
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            var r = await new ProjectDeletionService(_auth).DeleteAsync(_login.GlobalUserId, target, DeleteConfirmText);
            if (!r.Success) { ErrorMessage = r.ErrorMessage; return; }
            Projects.Remove(target);
            if (ReferenceEquals(SelectedProject, target)) SelectedProject = Projects.FirstOrDefault();
            DeleteTarget = null;
            _dialogs.Info(r.DatabaseDropped
                ? $"حُذف المشروع «{target.ProjectName}» وقاعدته.\nالنسخة الاحتياطية: {r.BackupFile}"
                : $"أُزيل المشروع «{target.ProjectName}» من القائمة (قاعدته غير موجودة أو مشتركة مع سجل آخر فلم تُحذف).");
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            ErrorMessage = $"تعذّر الحذف: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public ProjectOption? SelectedProject
    {
        get => _selectedProject;
        set { if (SetProperty(ref _selectedProject, value)) OpenCommand.RaiseCanExecuteChanged(); }
    }

    public string? ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }

    public AsyncRelayCommand OpenCommand { get; }
    public RelayCommand BackCommand { get; }

    private async Task OpenAsync(object? parameter)
    {
        if (parameter is ProjectOption p) SelectedProject = p;
        if (SelectedProject is null) return;

        ErrorMessage = null;
        IsBusy = true;
        try
        {
            var (info, error) = await _auth.OpenProjectAsync(SelectedProject);
            if (info is null)
            {
                ErrorMessage = error;
                return;
            }
            var session = new AppSession(SelectedProject.ProjectName, FullName, info,
                                         _auth.ControlConnectionString, _login.Username, SelectedProject.ProjectId);
            _navigator.ShowMainShell(new MainShellViewModel(session, _dialogs, _navigator));
        }
        finally
        {
            IsBusy = false;
        }
    }
}
