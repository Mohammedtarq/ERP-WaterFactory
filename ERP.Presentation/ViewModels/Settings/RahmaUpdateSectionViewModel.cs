using System.Collections.ObjectModel;
using ERP.Data.Import;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.Settings;

/// <summary>سطر في معاينة التحديث بنصّه ولونه.</summary>
public record RahmaUpdateRowView(string Section, string Name, string ActionText, string ActionColor, string Details)
{
    public static RahmaUpdateRowView From(RahmaUpdateRow r) => r.Action switch
    {
        RahmaUpdateAction.New => new(r.Section, r.Name, "جديد", "#047857", r.Details),
        RahmaUpdateAction.Changed => new(r.Section, r.Name, "سيتغير", "#B45309", r.Details),
        RahmaUpdateAction.SkippedByName => new(r.Section, r.Name, "يُتخطّى", "#475569", r.Details),
        _ => new(r.Section, r.Name, "بلا تغيير", "#94A3B8", r.Details)
    };
}

public record RahmaUpdateSummaryRow(string Section, int New, int Changed, int Same, int Skipped);

public record RahmaUpdateHistoryRow(int Id, string When, string Source, string Summary, string State);

/// <summary>
/// «تحديث من الرحمة» (الرحمة هي الأساس): اختيار قاعدة الرحمة ← «فحص الفروق» (قراءة فقط) ← تقرير بما سيحدث ← «اعتماد التحديث» بنسخة احتياطية سابقة
/// ← «التراجع عن آخر تحديث» عند الندم. يشمل العملاء والموردين وأسعار المنتجات؛ الأرصدة تُعرض للمقارنة فقط.
/// </summary>
public class RahmaUpdateSectionViewModel : SectionViewModel
{
    private RahmaDatabaseCandidate? _source;
    private RahmaImportPlan? _plan;
    private RahmaUpdatePreview? _preview;
    private bool _skipBackup;
    private string _sourceMessage = "";
    private string? _undoReason;
    private string _lastRunText = "";

    public RahmaUpdateSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.SystemSettings, "تحديث من الرحمة", Icons.Import, "#B45309",
               "تحديث العملاء والموردين وأسعار المنتجات من نسخة قاعدة الرحمة، مع تراجع عن آخر تحديث")
    {
        PreviewCommand = new AsyncRelayCommand(PreviewAsync);
        ApplyCommand = new AsyncRelayCommand(ApplyAsync);
        UndoCommand = new AsyncRelayCommand(UndoAsync);
    }

    protected override bool HasPendingInput => Preview is not null;
    protected override void ResetInput() { Preview = null; _plan = null; UndoReason = null; }

    public ObservableCollection<RahmaDatabaseCandidate> Candidates { get; } = new();
    public ObservableCollection<RahmaUpdateRowView> Rows { get; } = new();
    public ObservableCollection<RahmaUpdateSummaryRow> Summary { get; } = new();
    public ObservableCollection<string> Notes { get; } = new();
    public ObservableCollection<RahmaUpdateHistoryRow> History { get; } = new();

    public AsyncRelayCommand PreviewCommand { get; }
    public AsyncRelayCommand ApplyCommand { get; }
    public AsyncRelayCommand UndoCommand { get; }

    public RahmaDatabaseCandidate? Source { get => _source; set { if (SetProperty(ref _source, value)) Preview = null; } }
    public string SourceMessage { get => _sourceMessage; private set => SetProperty(ref _sourceMessage, value); }
    /// <summary>تخطّي النسخة الاحتياطية قبل التحديث (غير مستحسن): لمن لا يملك مجلد نسخ على السيرفر.</summary>
    public bool SkipBackup { get => _skipBackup; set => SetProperty(ref _skipBackup, value); }
    public string? UndoReason { get => _undoReason; set => SetProperty(ref _undoReason, value); }
    public string LastRunText { get => _lastRunText; private set { if (SetProperty(ref _lastRunText, value)) OnPropertyChanged(nameof(HasLastRun)); } }
    public bool HasLastRun => LastRunText.Length > 0;

    public RahmaUpdatePreview? Preview
    {
        get => _preview;
        private set
        {
            if (!SetProperty(ref _preview, value)) return;
            Rows.Clear(); Summary.Clear(); Notes.Clear();
            if (value is { Error: null })
            {
                foreach (var r in value.Rows.Where(r => r.Action != RahmaUpdateAction.Same)) Rows.Add(RahmaUpdateRowView.From(r));
                foreach (var sec in value.Sections)
                    Summary.Add(new(sec, value.Count(sec, RahmaUpdateAction.New), value.Count(sec, RahmaUpdateAction.Changed),
                                    value.Count(sec, RahmaUpdateAction.Same), value.Count(sec, RahmaUpdateAction.SkippedByName)));
                foreach (var n in value.Notes) Notes.Add(n);
            }
            OnPropertyChanged(nameof(HasPreview)); OnPropertyChanged(nameof(CanApply)); OnPropertyChanged(nameof(PreviewText));
        }
    }

    public bool HasPreview => Preview is { Error: null };
    public bool CanApply => Preview is { Error: null, ToApply: > 0 };
    public string PreviewText => Preview is null ? "" : Preview.Error ?? (Preview.ToApply == 0
        ? "النظام الجديد مطابق للرحمة في البيانات الأساسية — لا شيء يُحدَّث."
        : $"سيُطبَّق {Preview.ToApply:N0} تغيير. راجع الجدول ثم «اعتماد التحديث».");

    public override async Task LoadAsync()
    {
        Candidates.Clear();
        try
        {
            foreach (var c in await RahmaLegacyReader.FindCandidatesAsync(Session.ConnectionString)) Candidates.Add(c);
            SourceMessage = Candidates.Count == 0
                ? "لم أجد قاعدة الرحمة على هذا السيرفر. استعد نسخة الرحمة الاحتياطية كقاعدة على نفس SQL Server ثم أعد فتح الصفحة."
                : $"وُجدت {Candidates.Count} قاعدة. اختر الأحدث (آخر حركة) ثم «فحص الفروق».";
            Source ??= Candidates.OrderByDescending(c => c.LastActivity).FirstOrDefault();
        }
        catch (SqlException ex) { SourceMessage = "تعذّر البحث عن قواعد البيانات: " + ex.Message; }
        await RefreshHistoryAsync();
    }

    private async Task RefreshHistoryAsync()
    {
        await using var db = Session.NewDb();
        var svc = new RahmaUpdateService(db);
        History.Clear();
        foreach (var r in await svc.HistoryAsync())
            History.Add(new(r.Id, r.AppliedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm"), r.SourceDatabase, r.Summary ?? "",
                            r.UndoneAt is null ? "مُطبَّق" : $"أُلغي ({r.UndoReason})"));
        var last = await svc.LastActiveRunAsync();
        LastRunText = last is null ? "" : $"آخر تحديث: {last.AppliedAt.ToLocalTime():yyyy/MM/dd HH:mm} — {last.Summary}";
    }

    private async Task PreviewAsync()
    {
        if (!Require(CanEdit, "التحديث من الرحمة")) return;
        if (Source is null) { Dialogs.Error("اختر قاعدة الرحمة"); return; }
        IsBusy = true;
        try
        {
            var cs = new SqlConnectionStringBuilder(Session.ConnectionString) { InitialCatalog = Source.Name, ApplicationIntent = ApplicationIntent.ReadOnly }.ConnectionString;
            _plan = await RahmaLegacyReader.AnalyzeAsync(cs);
            await using var db = Session.NewDb();
            Preview = await new RahmaUpdateService(db).PreviewAsync(_plan);
            if (Preview.Error is not null) { Dialogs.Error(Preview.Error); return; }
            StatusMessage = "اكتمل الفحص (لم يُكتب شيء في أي قاعدة).";
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            Dialogs.Error("تعذّر فحص قاعدة الرحمة:\n" + ex.Message);
        }
        finally { IsBusy = false; }
    }

    private async Task ApplyAsync()
    {
        if (!Require(CanEdit, "التحديث من الرحمة")) return;
        if (_plan is null || Preview is not { Error: null, ToApply: > 0 }) { Dialogs.Error("افحص الفروق أولًا"); return; }
        if (!Dialogs.Confirm($"تطبيق {Preview.ToApply:N0} تغيير من الرحمة على النظام؟\n" +
                             (SkipBackup ? "تنبيه: بلا نسخة احتياطية سابقة.\n" : "ستؤخذ نسخة احتياطية قبل التحديث.\n") +
                             "يمكن التراجع عن آخر تحديث لاحقًا.")) return;
        IsBusy = true;
        try
        {
            await using var db = Session.NewDb();
            Func<Task<string?>>? backup = SkipBackup ? null : async () =>
            {
                var svc = new BackupService(Session.ConnectionString);
                var folder = await svc.GetDefaultFolderAsync() ?? throw new InvalidOperationException("لا يوجد مجلد نسخ على السيرفر");
                var name = new SqlConnectionStringBuilder(Session.ConnectionString).InitialCatalog;
                return await svc.BackupAsync(name, folder, "before-rahma-update");
            };
            var result = await new RahmaUpdateService(db).ApplyAsync(_plan, Session.UserId, backup);
            if (!result.Success) { Dialogs.Error(result.Error ?? "تعذّر التحديث"); return; }
            StatusMessage = "✓ " + result.Summary;
            Session.MarkDataChanged();
            Dialogs.Info("تم التحديث.\n\n" + result.Summary);
            Preview = null; _plan = null;
            await RefreshHistoryAsync();
        }
        finally { IsBusy = false; }
    }

    private async Task UndoAsync()
    {
        if (!Require(CanEdit, "التراجع عن التحديث")) return;
        if (!HasLastRun) { Dialogs.Error("لا يوجد تحديث للتراجع عنه"); return; }
        if (string.IsNullOrWhiteSpace(UndoReason)) { Dialogs.Error("اكتب سبب التراجع"); return; }
        if (!Dialogs.Confirm($"التراجع عن آخر تحديث؟\n{LastRunText}\nيُعاد ما عُدّل إلى قيمته السابقة ويُزال ما أُضيف. ما عدّلتَه أنت بعد التحديث لا يُستبدل.")) return;
        IsBusy = true;
        try
        {
            await using var db = Session.NewDb();
            var result = await new RahmaUpdateService(db).UndoLastAsync(Session.UserId, UndoReason);
            if (!result.Success) { Dialogs.Error(result.Error ?? "تعذّر التراجع"); return; }
            StatusMessage = "✓ " + result.Summary;
            Session.MarkDataChanged();
            Dialogs.Info(result.Summary + (result.Conflicts.Count == 0 ? "" : "\n\nلم يُستبدل (تعارض):\n- " + string.Join("\n- ", result.Conflicts)));
            UndoReason = null; Preview = null;
            await RefreshHistoryAsync();
        }
        finally { IsBusy = false; }
    }
}

/// <summary>صفحة «الرحمة» في الإعدادات: تحديث من الرحمة (الجديد) + النقل الأول (الشاشة الحالية كما هي).</summary>
public class RahmaHubSectionViewModel : HubSectionViewModel
{
    public RahmaHubSectionViewModel(AppSession s, IDialogService d) : base(s, d, ModuleCode.SystemSettings, "الرحمة", Icons.Import, "#B45309",
        "تحديث بيانات النظام من نسخة الرحمة، والنقل الأول منها — في صفحة واحدة",
        new HubTab("تحديث من الرحمة", "#B45309", new RahmaUpdateSectionViewModel(s, d)),
        new HubTab("النقل الأول", "#0F766E", new RahmaImportSectionViewModel(s, d))) { }
}
