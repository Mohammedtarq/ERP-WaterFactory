using System.Collections.ObjectModel;
using ERP.Data.Import;
using ERP.Data.ProjectDb.Entities;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.Settings;

/// <summary>عميل يُختار لصاحب الملصق الخاص؛ المفتاح 0 = ملصق مناسبة عام.</summary>
public record RahmaCustomerOption(int Key, string Name);

public record RahmaSummaryCard(string Label, string Value);

/// <summary>سطر ملصق خاص في المراجعة: الربط بالعميل قابل للتغيير.</summary>
public class RahmaRecipeRow : ObservableObject
{
    private readonly IReadOnlyDictionary<int, string> _names;
    public RahmaRecipeRow(RahmaRecipePlan plan, string productName, IReadOnlyDictionary<int, string> names)
    {
        Plan = plan;
        ProductName = productName;
        _names = names;
    }

    public RahmaRecipePlan Plan { get; }
    public string ProductName { get; }
    public string SpecialName => Plan.SpecialName;
    public int CustomerKey
    {
        get => Plan.CustomerLegacyId ?? 0;
        set
        {
            Plan.CustomerLegacyId = value == 0 ? null : value;
            Plan.CustomerName = value == 0 ? null : _names.GetValueOrDefault(value);
            OnPropertyChanged();
        }
    }
}

/// <summary>
/// النقل من نظام الرحمة: اختيار القاعدة القديمة على نفس السيرفر ← تحليل (قراءة فقط) ← مراجعة وتعديل بعد الجرد
/// (النقد، الكميات، ربط الملصقات، أقساط السلف) ← تجربة ومطابقة لا تحفظ شيئًا ← تنفيذ مرة واحدة.
/// </summary>
public class RahmaImportSectionViewModel : SectionViewModel
{
    private RahmaDatabaseCandidate? _source;
    private RahmaImportPlan? _plan;
    private DateTime _cutoverDate = DateTime.Today;
    private bool _dryRunPassed;
    private string? _importedSummary;
    private string _sourceMessage = "";

    public RahmaImportSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.SystemSettings, "النقل من نظام الرحمة", Icons.Import, "#B45309",
               "البيانات الأساسية والأرصدة الافتتاحية من النظام القديم، بمطابقة قبل الحفظ")
    {
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync);
        DryRunCommand = new AsyncRelayCommand(() => RunAsync(commit: false));
        ExecuteCommand = new AsyncRelayCommand(() => RunAsync(commit: true));
        PrintCommand = new RelayCommand(() => Print(executed: false));
    }

    protected override bool HasPendingInput => Plan is not null;

    protected override void ResetInput()
    {
        Source = null;
    }

    public ObservableCollection<RahmaDatabaseCandidate> Candidates { get; } = new();
    public ObservableCollection<string> Warnings { get; } = new();
    public ObservableCollection<RahmaRecipeRow> Recipes { get; } = new();
    public ObservableCollection<RahmaCustomerOption> CustomerOptions { get; } = new();
    public ObservableCollection<RahmaReconciliationRow> Reconciliation { get; } = new();

    public AsyncRelayCommand AnalyzeCommand { get; }
    public AsyncRelayCommand DryRunCommand { get; }
    public AsyncRelayCommand ExecuteCommand { get; }
    public RelayCommand PrintCommand { get; }

    public RahmaDatabaseCandidate? Source { get => _source; set { if (SetProperty(ref _source, value)) Plan = null; } }
    public string SourceMessage { get => _sourceMessage; private set => SetProperty(ref _sourceMessage, value); }
    public DateTime CutoverDate { get => _cutoverDate; set { if (SetProperty(ref _cutoverDate, value) && Plan is not null) Plan.CutoverDate = value; } }

    public RahmaImportPlan? Plan
    {
        get => _plan;
        private set
        {
            if (!SetProperty(ref _plan, value)) return;
            Warnings.Clear(); Recipes.Clear(); CustomerOptions.Clear(); Reconciliation.Clear();
            DryRunPassed = false;
            if (value is not null)
            {
                value.CutoverDate = CutoverDate;
                foreach (var w in value.Warnings) Warnings.Add(w);
                CustomerOptions.Add(new RahmaCustomerOption(0, "— عام (ملصق مناسبة) —"));
                foreach (var c in value.Customers.OrderBy(c => c.Name)) CustomerOptions.Add(new RahmaCustomerOption(c.LegacyId, c.Name));
                var names = value.Customers.ToDictionary(c => c.LegacyId, c => c.Name);
                foreach (var r in value.Recipes)
                    Recipes.Add(new RahmaRecipeRow(r, value.Products.FirstOrDefault(p => p.LegacyId == r.ProductLegacyId)?.LegacyName ?? "", names));
            }
            foreach (var n in new[] { nameof(HasPlan), nameof(CashBoxes), nameof(RawMaterials), nameof(FinishedStock), nameof(LoanEmployees),
                                      nameof(Products), nameof(SummaryCards), nameof(HasWarnings) })
                OnPropertyChanged(n);
        }
    }

    public bool HasPlan => Plan is not null;
    public bool HasWarnings => Warnings.Count > 0;
    public bool IsImported => ImportedSummary is not null;
    public bool CanImport => !IsImported;
    public string? ImportedSummary { get => _importedSummary; private set { if (SetProperty(ref _importedSummary, value)) { OnPropertyChanged(nameof(IsImported)); OnPropertyChanged(nameof(CanImport)); } } }
    public bool DryRunPassed { get => _dryRunPassed; private set => SetProperty(ref _dryRunPassed, value); }

    public IEnumerable<RahmaCashBoxPlan> CashBoxes => Plan?.CashBoxes ?? Enumerable.Empty<RahmaCashBoxPlan>();
    public IEnumerable<RahmaRawPlan> RawMaterials => Plan?.RawMaterials ?? Enumerable.Empty<RahmaRawPlan>();
    public IEnumerable<RahmaFinishedStockPlan> FinishedStock => Plan?.FinishedStock ?? Enumerable.Empty<RahmaFinishedStockPlan>();
    public IList<RahmaEmployeePlan> LoanEmployees => Plan?.Employees.Where(e => e.LoanBalance > 0).ToList() ?? new List<RahmaEmployeePlan>();
    public IEnumerable<RahmaProductPlan> Products => Plan?.Products ?? Enumerable.Empty<RahmaProductPlan>();

    /// <summary>بطاقات ملخص ما سيُنقل (أرقام النظام القديم).</summary>
    public IEnumerable<RahmaSummaryCard> SummaryCards => Plan is not { } p ? Enumerable.Empty<RahmaSummaryCard>() : new RahmaSummaryCard[]
    {
        new("العملاء", $"{p.Customers.Count:N0}"),
        new("ديون العملاء", $"{p.CustomersDebt:N0} د.ع"),
        new("تأمينات الستيكر", $"{p.DepositsTotal:N0} د.ع"),
        new("الموردون / ذممهم", $"{p.Suppliers.Count:N0} / {p.SuppliersDebt:N0} د.ع"),
        new("المواد الأولية", $"{p.RawMaterials.Count:N0} صنف"),
        new("الملصقات الخاصة", $"{p.Recipes.Count:N0} وصفة"),
        new("المنتج التام", $"{p.FinishedStock.Sum(f => f.Packs):N0} عبوة"),
        new("الموظفون / السلف", $"{p.Employees.Count:N0} / {p.LoansTotal:N0} د.ع"),
        new("الصناديق", $"{p.CashBoxes.Count:N0}"),
    };

    public override async Task LoadAsync()
    {
        await using (var db = Session.NewDb())
        {
            var done = await db.LegacyImports.AsNoTracking().FirstOrDefaultAsync(i => i.SourceSystem == RahmaImporter.SourceSystem);
            ImportedSummary = done is null ? null : $"تم النقل في {done.ImportedAt.ToLocalTime():yyyy/MM/dd HH:mm}\n{done.Summary}";
        }
        if (IsImported) return;
        Candidates.Clear();
        try
        {
            foreach (var c in await RahmaLegacyReader.FindCandidatesAsync(Session.ConnectionString)) Candidates.Add(c);
            SourceMessage = Candidates.Count == 0
                ? "لم أجد قاعدة نظام الرحمة على هذا السيرفر — يجب أن تكون على نفس SQL Server ولمستخدمك صلاحية قراءتها"
                : $"وُجدت {Candidates.Count} قاعدة. اختر الأحدث (آخر حركة) ثم «تحليل».";
            Source ??= Candidates.OrderByDescending(c => c.LastActivity).FirstOrDefault();
        }
        catch (SqlException ex)
        {
            SourceMessage = "تعذّر البحث عن قواعد البيانات: " + ex.Message;
        }
    }

    private async Task AnalyzeAsync()
    {
        if (!Require(CanEdit, "النقل من النظام السابق")) return;
        if (Source is null) { Dialogs.Error("اختر قاعدة نظام الرحمة"); return; }
        IsBusy = true;
        try
        {
            var cs = new SqlConnectionStringBuilder(Session.ConnectionString) { InitialCatalog = Source.Name, ApplicationIntent = ApplicationIntent.ReadOnly }.ConnectionString;
            Plan = await RahmaLegacyReader.AnalyzeAsync(cs);
            StatusMessage = "اكتمل التحليل (لم يُكتب شيء في أي قاعدة). راجع الجرد والكميات، ثم «تجربة ومطابقة».";
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            Dialogs.Error("تعذّر تحليل القاعدة القديمة:\n" + ex.Message);
        }
        finally { IsBusy = false; }
    }

    private async Task RunAsync(bool commit)
    {
        if (!Require(CanEdit, "النقل من النظام السابق")) return;
        if (Plan is null) { Dialogs.Error("حلّل القاعدة القديمة أولًا"); return; }
        if (commit)
        {
            if (!DryRunPassed) { Dialogs.Error("نفّذ «تجربة ومطابقة» أولًا وتأكد أن كل الأرقام متطابقة"); return; }
            if (!Dialogs.Confirm($"نقل بيانات نظام الرحمة إلى هذا المشروع بأرصدة افتتاحية بتاريخ {CutoverDate:yyyy/MM/dd}؟\n" +
                                 $"النقد المُدخل من الجرد: {Plan.CashTotal:N0} د.ع.\nلا يُنفَّذ النقل إلا مرة واحدة لكل مشروع.")) return;
        }
        IsBusy = true;
        try
        {
            Plan.CutoverDate = CutoverDate;
            await using var db = Session.NewDb();
            var result = await new RahmaImporter(db).ExecuteAsync(Plan, Session.UserId, commit);
            Reconciliation.Clear();
            foreach (var r in result.Reconciliation) Reconciliation.Add(r);
            if (!result.Success) { DryRunPassed = false; Dialogs.Error(result.Error ?? "تعذّر النقل"); return; }
            if (!commit)
            {
                DryRunPassed = result.AllMatch;
                StatusMessage = result.AllMatch
                    ? "✓ التجربة نجحت وكل الأرقام متطابقة — لم يُحفظ شيء. يمكنك الآن «تنفيذ النقل»."
                    : "✗ بعض الأرقام لا تتطابق — راجع البنود المعلَّمة قبل التنفيذ.";
                return;
            }
            StatusMessage = "✓ تم النقل وحُفظ كل شيء.";
            Session.MarkDataChanged();
            Dialogs.Info("تم النقل بنجاح.\n\n" + result.Summary +
                         "\n\nالخطوات التالية: أنشئ حسابات المستخدمين بكلمات مرور جديدة واربط كل صندوق بمستخدمه، ثم خذ «مطابقة الأساس» من المالية.");
            Print(executed: true);
            Plan = null;
            await LoadAsync();
        }
        finally { IsBusy = false; }
    }

    private void Print(bool executed)
    {
        if (Reconciliation.Count == 0 || Plan is null) { Dialogs.Error("نفّذ «تجربة ومطابقة» أولًا"); return; }
        Dialogs.ShowReport(DocumentReports.RahmaReconciliation(Session, Plan, Reconciliation.ToList(), executed));
    }
}
