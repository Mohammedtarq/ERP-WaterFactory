using System.Collections.ObjectModel;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;

namespace ERP.Presentation.ViewModels.HR;

/// <summary>سطر «المستحقون للعلاوة»: المبلغ المقترح قابل للتعديل قبل المنح.</summary>
public class AnnualRaiseEditRow : ObservableObject
{
    private decimal? _amount;
    public AnnualRaiseEditRow(AnnualRaiseRow row) { Row = row; _amount = row.SuggestedAmount; }
    public AnnualRaiseRow Row { get; }
    public decimal? Amount { get => _amount; set => SetProperty(ref _amount, value); }
}

/// <summary>
/// «المستحقون للعلاوة السنوية»: من مرّت سنة على آخر علاوة/ترقية (أو تاريخه المكتوب، أو التعيين) ومن يستحقها خلال 30 يومًا.
/// «منح» يسجّل علاوة سنوية دائمة بتاريخ اليوم تدخل الرواتب؛ «تأجيل» بسبب حتى تاريخ يُختار. للمدير ومسؤول الموارد البشرية.
/// </summary>
public class AnnualRaisesSectionViewModel : SectionViewModel
{
    private string? _snoozeReason;
    private int _snoozeDays = 30;

    public AnnualRaisesSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.HR, "المستحقون للعلاوة", Icons.Up, "#8B5CF6", "سنة كاملة من آخر علاوة أو ترقية — مع تنبيه قبل 30 يومًا")
    {
        GrantCommand = new AsyncRelayCommand(p => p is AnnualRaiseEditRow r ? GrantAsync(r) : Task.CompletedTask);
        PrintLastLetterCommand = new AsyncRelayCommand(() => LastRaiseId is int id ? PrintLetterAsync(id) : Task.CompletedTask);
        SnoozeCommand = new AsyncRelayCommand(p => p is AnnualRaiseEditRow r ? SnoozeAsync(r) : Task.CompletedTask);
    }

    protected override bool ReloadOnActivate => true;
    public ObservableCollection<AnnualRaiseEditRow> Rows { get; } = new();
    public int DueCount => Rows.Count(r => r.Row.IsDue);
    public int SoonCount => Rows.Count - DueCount;
    public string? SnoozeReason { get => _snoozeReason; set => SetProperty(ref _snoozeReason, value); }
    public int SnoozeDays { get => _snoozeDays; set => SetProperty(ref _snoozeDays, value); }
    public AsyncRelayCommand GrantCommand { get; }
    public AsyncRelayCommand SnoozeCommand { get; }
    public AsyncRelayCommand PrintLastLetterCommand { get; }
    /// <summary>آخر علاوة مُنحت من هذه الشاشة (لإعادة طباعة كتابها).</summary>
    public int? LastRaiseId { get; private set; }

    private async Task PrintLetterAsync(int raiseId)
    {
        await using var db = Session.NewDb();
        if (await DocumentReports.RaiseLetterAsync(Session, db, raiseId) is { } r) Dialogs.ShowReport(r);
    }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var rows = await new AnnualRaiseService(db).UpcomingAsync();
        Rows.Clear();
        foreach (var r in rows) Rows.Add(new AnnualRaiseEditRow(r));
        OnPropertyChanged(nameof(DueCount));
        OnPropertyChanged(nameof(SoonCount));
        StatusMessage = Rows.Count == 0 ? "لا أحد مستحق للعلاوة الآن ولا خلال 30 يومًا"
            : $"مستحقون: {DueCount} — خلال 30 يومًا: {SoonCount}" + (Rows.Any(r => r.Amount is null) ? " (حدّد مبلغ العلاوة للقسم أو للموظف لمن بلا مبلغ)" : "");
    }

    private async Task GrantAsync(AnnualRaiseEditRow row)
    {
        if (!Require(CanAdd, "منح العلاوة")) return;
        if (row.Amount is not > 0) { Dialogs.Error("اكتب مبلغ العلاوة في السطر"); return; }
        if (!row.Row.IsDue && !Dialogs.Confirm($"علاوة {row.Row.EmployeeName} مستحقة {row.Row.StatusText} — منحها الآن مبكرًا؟")) return;
        if (!Dialogs.Confirm($"منح {row.Row.EmployeeName} علاوة سنوية {row.Amount:N0} تُضاف لراتبه من اليوم؟")) return;
        int? raiseId = null;
        await using (var db = Session.NewDb())
        {
            if (!await RunOperationAsync(async () =>
                {
                    var (result, id) = await new AnnualRaiseService(db).GrantWithIdAsync(row.Row.EmployeeId, row.Amount!.Value, Session.UserId);
                    raiseId = id;
                    return result;
                }, $"مُنحت العلاوة لـ {row.Row.EmployeeName}")) return;
        }
        LastRaiseId = raiseId;
        var message = StatusMessage;
        await LoadAsync();
        StatusMessage = message;
        if (raiseId is int rid && Dialogs.Confirm($"مُنحت العلاوة لـ {row.Row.EmployeeName}.\n\nطباعة كتاب العلاوة الآن؟")) await PrintLetterAsync(rid);
    }

    private async Task SnoozeAsync(AnnualRaiseEditRow row)
    {
        if (!Require(CanAdd, "تأجيل العلاوة")) return;
        if (string.IsNullOrWhiteSpace(SnoozeReason)) { Dialogs.Error("اكتب سبب التأجيل في الخانة أعلى القائمة أولًا"); return; }
        if (SnoozeDays <= 0) { Dialogs.Error("مدة التأجيل بالأيام يجب أن تكون أكبر من صفر"); return; }
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new AnnualRaiseService(db).SnoozeAsync(row.Row.EmployeeId, DateTime.Today.AddDays(SnoozeDays), SnoozeReason!, Session.UserId),
                                    $"أُجّلت علاوة {row.Row.EmployeeName} حتى {DateTime.Today.AddDays(SnoozeDays):yyyy/MM/dd}"))
        {
            var message = StatusMessage;
            SnoozeReason = null;
            await LoadAsync();
            StatusMessage = message;
        }
    }
}
