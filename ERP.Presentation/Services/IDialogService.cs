namespace ERP.Presentation.Services;

/// <summary>رسائل المستخدم — تنفيذ WPF في ERP.Desktop، وتنفيذ مسجِّل في الاختبارات.</summary>
public interface IDialogService
{
    void Info(string message);
    void Error(string message);
    bool Confirm(string message);
}

/// <summary>التنقل بين نوافذ التدفق الرئيسي (دخول ← مشروع ← الواجهة الرئيسية).</summary>
public interface INavigator
{
    void ShowProjectSelection(ViewModels.Shell.ProjectSelectionViewModel vm);
    void ShowMainShell(ViewModels.Shell.MainShellViewModel vm);
    void ShowLogin();
}
