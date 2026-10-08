using System.Collections.ObjectModel;
using System.IO;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.HR;

/// <summary>رقم في الجهاز غير مربوط: يختار المستخدم الموظف ليُربط مباشرة من الشاشة.</summary>
public class UnmappedCodeRow : ObservableObject
{
    private Employee? _employee;
    public FingerprintUnmapped Info { get; init; } = null!;
    public string Code => Info.Code;
    public Employee? Employee { get => _employee; set => SetProperty(ref _employee, value); }
}

/// <summary>
/// استيراد ملف بصمة ZKTeco (attlog.dat): اختيار الملف ← معاينة لكل موظف (حاضر، متأخر، غائب، بلا خروج)
/// وربط الأرقام غير المعروفة ← تسجيل الحضور. الحالة تُحسب من شفت كل موظف كما في الإدخال اليدوي.
/// </summary>
public class FingerprintImportSectionViewModel : SectionViewModel
{
    private string? _filePath;
    private FingerprintPreview? _preview;
    private List<FingerprintPunch> _punches = new();
    private int _skipped;

    public FingerprintImportSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.HR, "استيراد البصمة", Icons.Import, "#0891B2",
               "ملف الحضور من جهاز البصمة (attlog.dat): معاينة لكل موظف ثم تسجيل الحضور والتأخير والغياب تلقائيًا")
    {
        PickCommand = new AsyncRelayCommand(PickAsync);
        LinkCommand = new AsyncRelayCommand(LinkAsync);
        ApplyCommand = new AsyncRelayCommand(ApplyAsync);
    }

    protected override bool ReloadOnActivate => true;
    public string? FilePath { get => _filePath; private set { if (SetProperty(ref _filePath, value)) OnPropertyChanged(nameof(FileName)); } }
    public string FileName => FilePath is null ? "لم يُختر ملف" : Path.GetFileName(FilePath);
    public FingerprintPreview? Preview { get => _preview; private set { if (SetProperty(ref _preview, value)) { OnPropertyChanged(nameof(HasPreview)); OnPropertyChanged(nameof(SummaryText)); } } }
    public bool HasPreview => Preview is { Plan.Count: > 0 };
    public string SummaryText => Preview is null ? "اختر ملف الحضور من جهاز البصمة"
        : $"الفترة {Preview.PeriodFrom:yyyy/MM/dd} – {Preview.PeriodTo:yyyy/MM/dd} · {Preview.Punches:N0} بصمة ({Preview.Duplicates:N0} مكررة دُمجت)"
          + $" · {Preview.Employees.Count} موظف مربوط · {Preview.Plan.Count:N0} يوم سيُسجَّل";
    public ObservableCollection<FingerprintEmployeeSummary> Employees { get; } = new();
    public ObservableCollection<UnmappedCodeRow> Unmapped { get; } = new();
    public ObservableCollection<string> Warnings { get; } = new();
    public ObservableCollection<Employee> AllEmployees { get; } = new();

    public AsyncRelayCommand PickCommand { get; }
    public AsyncRelayCommand LinkCommand { get; }
    public AsyncRelayCommand ApplyCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        AllEmployees.Clear();
        foreach (var e in await db.Employees.AsNoTracking().Where(e => e.IsActive && !e.IsTemporary).OrderBy(e => e.FullName).ToListAsync())
            AllEmployees.Add(e);
        if (_punches.Count > 0) await RefreshPreviewAsync();
    }

    /// <summary>للاختبارات وللفتح من مسار معروف.</summary>
    public async Task LoadFileAsync(string path)
    {
        string text;
        try { text = await File.ReadAllTextAsync(path); }
        catch (Exception ex) { Dialogs.Error($"تعذّرت قراءة الملف: {ex.Message}"); return; }
        (_punches, _skipped) = FingerprintImportService.Parse(text);
        FilePath = path;
        await RefreshPreviewAsync();
    }

    private async Task PickAsync()
    {
        var path = Dialogs.PickFile("اختر ملف الحضور من جهاز البصمة", "ملف البصمة (*.dat;*.txt;*.csv)|*.dat;*.txt;*.csv|كل الملفات (*.*)|*.*");
        if (path is not null) await LoadFileAsync(path);
    }

    private async Task RefreshPreviewAsync()
    {
        await using var db = Session.NewDb();
        var p = await new FingerprintImportService(db).PreviewAsync(_punches, _skipped);
        Employees.Clear();
        foreach (var e in p.Employees) Employees.Add(e);
        Unmapped.Clear();
        foreach (var u in p.Unmapped) Unmapped.Add(new UnmappedCodeRow { Info = u });
        Warnings.Clear();
        foreach (var w in p.Warnings) Warnings.Add(w);
        if (p.SkippedLines > 0) Warnings.Add($"{p.SkippedLines} سطر غير مفهوم في الملف تُجوهل");
        Preview = p;
        StatusMessage = null;
    }

    private async Task LinkAsync()
    {
        if (!Require(CanEdit, "ربط أرقام البصمة")) return;
        var rows = Unmapped.Where(r => r.Employee is not null).ToList();
        if (rows.Count == 0) { Dialogs.Error("اختر الموظف أمام كل رقم تريد ربطه"); return; }
        await using var db = Session.NewDb();
        var svc = new FingerprintImportService(db);
        foreach (var r in rows)
        {
            var result = await svc.LinkAsync(r.Code, r.Employee!.Id, Session.UserId);
            if (!result.Success) { Dialogs.Error($"الرقم {r.Code}: {result.ErrorMessage}"); return; }
        }
        await RefreshPreviewAsync();
        StatusMessage = $"رُبط {rows.Count} رقم — راجع المعاينة ثم سجّل الحضور";
    }

    private async Task ApplyAsync()
    {
        if (!Require(CanAdd, "استيراد البصمة")) return;
        if (Preview is not { Plan.Count: > 0 } p) { Dialogs.Error("لا شيء لتسجيله — اختر الملف واربط أرقام البصمة أولًا"); return; }
        if (!Dialogs.Confirm($"تسجيل حضور {p.Employees.Count} موظف من {p.PeriodFrom:yyyy/MM/dd} إلى {p.PeriodTo:yyyy/MM/dd} ({p.Plan.Count:N0} يوم)؟ "
                             + "يُستبدل ما سُجّل يدويًا لهذه الأيام، عدا الإجازات المعتمدة."))
            return;
        await using var db = Session.NewDb();
        var days = 0;
        if (await RunOperationAsync(async () =>
            {
                var (r, n) = await new FingerprintImportService(db).ApplyAsync(p, FileName, Session.UserId);
                days = n;
                return r;
            }, "سُجّل الحضور من ملف البصمة"))
            StatusMessage = $"{StatusMessage}: {days:N0} يوم لـ {p.Employees.Count} موظف — راجعه من «الحضور اليومي»";
    }
}
