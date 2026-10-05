namespace ERP.Presentation.Services;

/// <summary>رسائل المستخدم — تنفيذ WPF في ERP.Desktop، وتنفيذ مسجِّل في الاختبارات.</summary>
public interface IDialogService
{
    void Info(string message);
    void Error(string message);
    bool Confirm(string message);

    /// <summary>معاينة مستند قبل طباعته (فاتورة، كشف حساب...).</summary>
    void ShowReport(ReportDocument report);

    /// <summary>اختيار صورة (PNG/JPG) من الجهاز — يعيد مسارها أو null عند الإلغاء.</summary>
    string? PickImageFile();

    /// <summary>اختيار ملف من الجهاز بعنوان ومرشّح (مثل ملف البصمة) — يعيد مساره أو null عند الإلغاء.</summary>
    string? PickFile(string title, string filter) => null;
}

/// <summary>التنقل بين نوافذ التدفق الرئيسي (دخول ← مشروع ← الواجهة الرئيسية).</summary>
public interface INavigator
{
    void ShowProjectSelection(ViewModels.Shell.ProjectSelectionViewModel vm);
    void ShowMainShell(ViewModels.Shell.MainShellViewModel vm);
    void ShowLogin();

    /// <summary>معالج الإعداد (أول تشغيل، أو تعذّر الاتصال بقاعدة التحكم).</summary>
    void ShowSetup(string? reason);

    /// <summary>بعد نجاح الإعداد: يُحفظ الاتصال الجديد ويُعاد فتح شاشة الدخول عليه.</summary>
    void UseControlConnection(string controlConnectionString);
}

/// <summary>حفظ/قراءة سلسلة اتصال قاعدة التحكم (ملف إعدادات المستخدم).</summary>
public interface IConfigStore
{
    string? LoadControlConnectionString();
    void SaveControlConnectionString(string connectionString);
    string ConfigPath { get; }
}
