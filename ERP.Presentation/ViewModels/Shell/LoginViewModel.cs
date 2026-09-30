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
    private ProjectOption? _selectedProject;
    private string? _errorMessage;

    public ProjectSelectionViewModel(AuthService auth, IDialogService dialogs, INavigator navigator, LoginResult login)
    {
        _auth = auth;
        _dialogs = dialogs;
        _navigator = navigator;
        FullName = login.FullName;
        Projects = login.Projects;
        _selectedProject = Projects.FirstOrDefault();
        OpenCommand = new AsyncRelayCommand(OpenAsync, _ => SelectedProject is not null);
        BackCommand = new RelayCommand(() => _navigator.ShowLogin());
    }

    public string FullName { get; }
    public string Greeting => $"أهلًا {FullName}، اختر المشروع";
    public IReadOnlyList<ProjectOption> Projects { get; }

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
            var session = new AppSession(SelectedProject.ProjectName, FullName, info);
            _navigator.ShowMainShell(new MainShellViewModel(session, _dialogs, _navigator));
        }
        finally
        {
            IsBusy = false;
        }
    }
}
