using System.Collections.ObjectModel;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;

namespace ERP.Presentation.ViewModels.Finance;

/// <summary>
/// المطابقة الدورية: حساب الموجودات (المنتج التام بالكلفة أو بسعر البيع حسب الاختيار)، مقارنتها بالسابقة،
/// معاينة توزيع الفائض على الشركاء، ثم الاعتماد والطباعة. سجل المطابقات السابقة للطباعة.
/// </summary>
public class ReconciliationSectionViewModel : SectionViewModel
{
    private DateTime _date = DateTime.Today;
    private Option<FinishedGoodsValuation> _valuation;
    private ReconciliationSnapshot? _snapshot;
    private string? _notes;

    public ReconciliationSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Finance, "المطابقة الدورية", Icons.Statement, "#0F766E", "موجودات المعمل والفائض عن المطابقة السابقة وتوزيعه على الشركاء")
    {
        _valuation = Valuations[0];
        ComputeCommand = new AsyncRelayCommand(ComputeAsync);
        PostCommand = new AsyncRelayCommand(PostAsync);
        PrintPreviewCommand = new RelayCommand(() =>
        {
            if (Snapshot is null) { Dialogs.Error("احسب المطابقة أولًا"); return; }
            Dialogs.ShowReport(DocumentReports.ReconciliationPreview(Session, Snapshot));
        });
        PrintCommand = new AsyncRelayCommand(p => p is ReconciliationListRow r ? PrintAsync(db => DocumentReports.ReconciliationAsync(Session, db, r.Id)) : Task.CompletedTask);
    }

    protected override bool ReloadOnActivate => true;

    public IReadOnlyList<Option<FinishedGoodsValuation>> Valuations { get; } = ArabicLabels.OptionsOf<FinishedGoodsValuation>();
    public ObservableCollection<ReconciliationLineDto> Lines { get; } = new();
    public ObservableCollection<PartnerSharePreview> Shares { get; } = new();
    public ObservableCollection<string> Warnings { get; } = new();
    public ObservableCollection<ReconciliationListRow> History { get; } = new();

    public AsyncRelayCommand ComputeCommand { get; }
    public AsyncRelayCommand PostCommand { get; }
    public RelayCommand PrintPreviewCommand { get; }
    public AsyncRelayCommand PrintCommand { get; }

    public DateTime Date { get => _date; set { if (SetProperty(ref _date, value)) Snapshot = null; } }
    public Option<FinishedGoodsValuation> Valuation { get => _valuation; set { if (SetProperty(ref _valuation, value)) Background(ComputeAsync()); } }
    public string? Notes { get => _notes; set => SetProperty(ref _notes, value); }

    public ReconciliationSnapshot? Snapshot
    {
        get => _snapshot;
        private set
        {
            if (!SetProperty(ref _snapshot, value)) return;
            Lines.Clear(); Shares.Clear(); Warnings.Clear();
            if (value is not null)
            {
                foreach (var l in value.Lines) Lines.Add(l);
                foreach (var sh in value.Shares) Shares.Add(sh);
                foreach (var w in value.Warnings) Warnings.Add(w);
            }
            foreach (var n in new[] { nameof(HasSnapshot), nameof(SurplusText), nameof(SurplusLabel), nameof(HasWarnings), nameof(SharesHint) }) OnPropertyChanged(n);
        }
    }
    public bool HasSnapshot => Snapshot is not null;
    public bool HasWarnings => Warnings.Count > 0;
    public string SurplusLabel => Snapshot is null ? "" : Snapshot.IsBaseline ? "مطابقة أساس" : Snapshot.Surplus >= 0 ? "الفائض (الربح)" : "الخسارة";
    public string SurplusText => Snapshot is null ? "" : Snapshot.IsBaseline
        ? "أول مطابقة — تُحفظ أساسًا للمقارنة دون توزيع"
        : $"{Snapshot.Surplus:N0} د.ع";
    public string SharesHint => Snapshot is null || Snapshot.IsBaseline ? "" :
        Shares.Count == 0 ? "لا يوجد شركاء بنسب — أضفهم من شاشة «الشركاء والأرباح»"
        : Shares.Sum(x => x.SharePercent) != 100m ? $"مجموع النسب {Shares.Sum(x => x.SharePercent):0.##}% — يجب أن يكون 100% قبل الاعتماد" : "";

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        History.Clear();
        foreach (var r in await new ReconciliationService(db).GetReconciliationsAsync()) History.Add(r);
    }

    public async Task ComputeAsync()
    {
        await using var db = Session.NewDb();
        Snapshot = await new ReconciliationService(db).ComputeAsync(Date, Valuation.Value);
        StatusMessage = $"حُسبت المطابقة ({Valuation.Label} للمنتج التام)";
    }

    private async Task PostAsync()
    {
        if (Snapshot is null) await ComputeAsync();
        var s = Snapshot!;
        var question = s.IsBaseline
            ? $"اعتماد مطابقة الأساس بصافي موجودات {s.NetAssets:N0} د.ع؟"
            : $"اعتماد المطابقة وتوزيع {(s.Surplus >= 0 ? "فائض" : "خسارة")} {s.Surplus:N0} د.ع على الشركاء؟ لا يمكن التراجع.";
        if (!Dialogs.Confirm(question)) return;
        await using var db = Session.NewDb();
        var (result, recon) = await new ReconciliationService(db).PostAsync(Date, Valuation.Value, Notes, Session.UserId);
        if (!result.Success) { Dialogs.Error(result.ErrorMessage ?? "تعذّر الاعتماد"); return; }
        StatusMessage = $"اعتُمدت المطابقة {recon!.ReconNumber}";
        Notes = null;
        Snapshot = null;
        await LoadAsync();
        await PrintAsync(d => DocumentReports.ReconciliationAsync(Session, d, recon.Id));
    }
}

/// <summary>الشركاء ونسبهم وأرصدة أرباحهم، وسحب الأرباح من الصندوق، وكشف كل شريك.</summary>
public class PartnersSectionViewModel : SectionViewModel
{
    private PartnerBalanceRow? _selected;
    private int? _editId;
    private string _name = "";
    private decimal _percent;
    private bool _isManager;
    private bool _isActive = true;
    private string? _partnerNotes;
    private decimal _amount;
    private DateTime _date = DateTime.Today;
    private string? _withdrawNotes;

    public PartnersSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Finance, "الشركاء والأرباح", Icons.People, "#7C3AED", "نسب الشركاء، أرصدة أرباحهم، السحب، وكشف كل شريك")
    {
        NewCommand = new RelayCommand(() => { Selected = null; ClearEditor(); });
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        WithdrawCommand = new AsyncRelayCommand(WithdrawAsync);
        PrintStatementCommand = new AsyncRelayCommand(() => Selected is null ? Fail("اختر الشريك") : PrintAsync(db => DocumentReports.PartnerStatementAsync(Session, db, Selected.Id)));
    }

    protected override bool HasPendingInput => Amount != 0;
    protected override bool ReloadOnActivate => true;

    public ObservableCollection<PartnerBalanceRow> Partners { get; } = new();
    public ObservableCollection<PartnerStatementRow> Statement { get; } = new();
    public RelayCommand NewCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand WithdrawCommand { get; }
    public AsyncRelayCommand PrintStatementCommand { get; }
    private Task Fail(string m) { Dialogs.Error(m); return Task.CompletedTask; }

    public PartnerBalanceRow? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            if (value is not null)
            {
                _editId = value.Id;
                Name = value.Name; SharePercent = value.SharePercent; IsManager = value.IsManager; IsActive = value.IsActive; PartnerNotes = value.Notes;
            }
            OnPropertyChanged(nameof(EditorTitle));
            OnPropertyChanged(nameof(HasSelection));
            Background(LoadStatementAsync());
        }
    }
    public bool HasSelection => Selected is not null;
    public string EditorTitle => _editId is null ? "شريك جديد" : $"تعديل: {Name}";
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public decimal SharePercent { get => _percent; set => SetProperty(ref _percent, value); }
    public bool IsManager { get => _isManager; set => SetProperty(ref _isManager, value); }
    public bool IsActive { get => _isActive; set => SetProperty(ref _isActive, value); }
    public string? PartnerNotes { get => _partnerNotes; set => SetProperty(ref _partnerNotes, value); }
    public decimal Amount { get => _amount; set => SetProperty(ref _amount, value); }
    public DateTime Date { get => _date; set => SetProperty(ref _date, value); }
    public string? WithdrawNotes { get => _withdrawNotes; set => SetProperty(ref _withdrawNotes, value); }

    public decimal TotalPercent => Partners.Where(p => p.IsActive).Sum(p => p.SharePercent);
    public string PercentHint => Partners.Count == 0 ? "لا يوجد شركاء بعد — أضفهم بأسمائهم ونسبهم" :
        TotalPercent == 100m ? "مجموع النسب 100% ✓" : $"مجموع النسب {TotalPercent:0.##}% — يجب أن يصل إلى 100% قبل توزيع الأرباح";

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var selectedId = Selected?.Id;
        Partners.Clear();
        foreach (var p in await new ReconciliationService(db).GetPartnersAsync()) Partners.Add(p);
        OnPropertyChanged(nameof(TotalPercent));
        OnPropertyChanged(nameof(PercentHint));
        _selected = Partners.FirstOrDefault(p => p.Id == selectedId);
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(HasSelection));
        await LoadStatementAsync();
    }

    private async Task LoadStatementAsync()
    {
        Statement.Clear();
        if (Selected is null) return;
        await using var db = Session.NewDb();
        foreach (var r in (await new ReconciliationService(db).GetPartnerStatementAsync(Selected.Id)).AsEnumerable().Reverse()) Statement.Add(r);
    }

    private void ClearEditor()
    {
        _editId = null;
        Name = ""; SharePercent = 0; IsManager = false; IsActive = true; PartnerNotes = null;
        OnPropertyChanged(nameof(EditorTitle));
    }

    private async Task SaveAsync()
    {
        await using var db = Session.NewDb();
        var (result, partner) = await new ReconciliationService(db).SavePartnerAsync(_editId, Name, SharePercent, IsManager, IsActive, PartnerNotes, Session.UserId);
        if (!result.Success) { Dialogs.Error(result.ErrorMessage ?? "تعذّر الحفظ"); return; }
        StatusMessage = $"حُفظ الشريك {partner!.Name} ({partner.SharePercent:0.##}%)";
        await LoadAsync();
        Selected = Partners.FirstOrDefault(p => p.Id == partner.Id);
    }

    private async Task WithdrawAsync()
    {
        if (Selected is null) { Dialogs.Error("اختر الشريك من الجدول"); return; }
        if (Amount <= 0) { Dialogs.Error("أدخل مبلغًا أكبر من صفر"); return; }
        await using var db = Session.NewDb();
        var (result, tx) = await new ReconciliationService(db).WithdrawAsync(Selected.Id, Amount, Date, WithdrawNotes, Session.UserId);
        if (!result.Success) { Dialogs.Error(result.ErrorMessage ?? "تعذّر السحب"); return; }
        StatusMessage = $"سُحب {Amount:N0} د.ع من أرباح {Selected.Name}";
        Amount = 0;
        WithdrawNotes = null;
        await LoadAsync();
        await PrintAsync(d => DocumentReports.PartnerWithdrawalAsync(Session, d, tx!.Id));
    }
}
