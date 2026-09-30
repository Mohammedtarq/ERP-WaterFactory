using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;

namespace ERP.Presentation.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    private bool _isBusy;
    private string? _statusMessage;
    private readonly List<Task> _background = new();

    /// <summary>
    /// عمل خلفي يُطلق من تغيير خاصية (مثل جلب السعر المقترح عند اختيار الصنف).
    /// يُتتبَّع حتى يمكن انتظاره (IdleAsync)، وأي خطأ فيه يُعرض بدل أن يضيع بصمت.
    /// </summary>
    protected void Background(Task task)
    {
        lock (_background) _background.Add(task);
        _ = task.ContinueWith(t =>
        {
            if (t.Exception is { } ex) AsyncRelayCommand.UnhandledErrorHandler?.Invoke(ex.GetBaseException());
        }, TaskScheduler.Current);
    }

    /// <summary>ينتظر انتهاء كل الأعمال الخلفية الجارية (بما فيها ما تُطلقه أثناء الانتظار).</summary>
    public async Task IdleAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_background)
            {
                _background.RemoveAll(t => t.IsCompleted);
                pending = _background.ToArray();
            }
            if (pending.Length == 0) return;
            await Task.WhenAll(pending);
        }
    }

    public bool IsBusy { get => _isBusy; protected set => SetProperty(ref _isBusy, value); }

    /// <summary>سطر حالة أسفل الشاشة (آخر عملية ناجحة) — لا يحتاج نافذة منبثقة.</summary>
    public string? StatusMessage { get => _statusMessage; protected set => SetProperty(ref _statusMessage, value); }
}

/// <summary>قاعدة كل شاشات الوحدات: الجلسة + الرسائل + صلاحيات وحدتها.</summary>
public abstract class SessionViewModel : ViewModelBase
{
    protected SessionViewModel(AppSession session, IDialogService dialogs, string moduleCode)
    {
        Session = session;
        Dialogs = dialogs;
        Module = moduleCode;
    }

    protected AppSession Session { get; }
    protected IDialogService Dialogs { get; }
    public string Module { get; }

    public bool CanAdd => Session.Permissions.CanAdd(Module);
    public bool CanEdit => Session.Permissions.CanEdit(Module);
    public bool CanDelete => Session.Permissions.CanDelete(Module);
    public bool CanPost => Session.Permissions.CanPost(Module);

    /// <summary>ينفّذ عملية تعيد FinanceOperationResult ويعرض النتيجة. يعيد true عند النجاح.</summary>
    protected async Task<bool> RunOperationAsync(Func<Task<FinanceOperationResult>> op, string successMessage)
    {
        IsBusy = true;
        try
        {
            var r = await op();
            if (!r.Success)
            {
                Dialogs.Error(r.ErrorMessage ?? "تعذّر تنفيذ العملية");
                return false;
            }
            StatusMessage = successMessage;
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected bool Require(bool allowed, string action)
    {
        if (allowed) return true;
        Dialogs.Error($"لا تملك صلاحية {action} في هذه الوحدة");
        return false;
    }
}
