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
        DeleteCommand = new AsyncRelayCommand(p => p is ReconciliationListRow r ? DeleteAsync(r) : Task.CompletedTask);
        ApplyEditCommand = new AsyncRelayCommand(() => AdjustSelectedAsync(AdjustmentKind.Edit));
        ExcludeCommand = new AsyncRelayCommand(() => AdjustSelectedAsync(AdjustmentKind.Exclude));
        RevertLineCommand = new AsyncRelayCommand(RevertSelectedAsync);
        ResetAdjustmentsCommand = new AsyncRelayCommand(async () => { _adjustments.Clear(); await ComputeAsync(); StatusMessage = "أُلغيت كل التعديلات اليدوية"; });
        AddManualCommand = new AsyncRelayCommand(AddManualAsync);
    }

    protected override bool ReloadOnActivate => true;

    // ======== تعديل البنود قبل الاعتماد (للأدمن) ========
    private readonly List<ReconciliationAdjustment> _adjustments = new();
    private bool _canAdjust;
    private ReconciliationLineDto? _selectedLine;
    private decimal _editValue;
    private string? _editReason;
    private bool _manualIsLiability;
    private string? _manualDescription;
    private decimal _manualAmount;

    public AsyncRelayCommand ApplyEditCommand { get; }
    public AsyncRelayCommand ExcludeCommand { get; }
    public AsyncRelayCommand RevertLineCommand { get; }
    public AsyncRelayCommand ResetAdjustmentsCommand { get; }
    public AsyncRelayCommand AddManualCommand { get; }

    /// <summary>أدوات التعديل تظهر للأدمن فقط (نفس شرط الاعتماد).</summary>
    public bool CanAdjust { get => _canAdjust; private set { if (SetProperty(ref _canAdjust, value)) OnPropertyChanged(nameof(ShowAdjustTools)); } }
    public bool ShowAdjustTools => CanAdjust && HasSnapshot;
    public ReconciliationLineDto? SelectedLine
    {
        get => _selectedLine;
        set
        {
            if (!SetProperty(ref _selectedLine, value)) return;
            if (value is { IsManual: false }) EditValue = value.Value;
            OnPropertyChanged(nameof(SelectedLineTitle));
            OnPropertyChanged(nameof(CanEditSelected));
        }
    }
    public bool CanEditSelected => SelectedLine is not null;
    public string SelectedLineTitle => SelectedLine is null ? "اختر بندًا من الجدول لتعديله أو استبعاده" : $"{SelectedLine.Section} — {SelectedLine.Description}";
    public decimal EditValue { get => _editValue; set => SetProperty(ref _editValue, value); }
    /// <summary>سبب التعديل — إلزامي، ويُطبع بجانب البند ويُسجَّل في التدقيق.</summary>
    public string? EditReason { get => _editReason; set => SetProperty(ref _editReason, value); }
    public bool ManualIsLiability { get => _manualIsLiability; set => SetProperty(ref _manualIsLiability, value); }
    public string? ManualDescription { get => _manualDescription; set => SetProperty(ref _manualDescription, value); }
    public decimal ManualAmount { get => _manualAmount; set => SetProperty(ref _manualAmount, value); }
    public bool HasAdjustments => Snapshot?.HasAdjustments == true;
    public string AdjustmentBanner => Snapshot is not { HasAdjustments: true } s ? "" :
        s.IsBaseline ? $"تعديلات يدوية مطبّقة — أثرها على صافي الموجودات {s.AdjustmentsDelta:+#,0;-#,0;0} د.ع"
                     : $"تعديلات يدوية مطبّقة — الفائض كان {s.SurplusBeforeAdjustments:N0} وصار {s.Surplus:N0} د.ع (الأثر {s.AdjustmentsDelta:+#,0;-#,0;0})";

    private async Task AdjustSelectedAsync(AdjustmentKind kind)
    {
        if (SelectedLine is null) { Dialogs.Error("اختر بندًا من الجدول أولًا"); return; }
        if (SelectedLine.IsManual) { Dialogs.Error("البند اليدوي يُلغى بزر «إلغاء التعديل» ثم يُضاف من جديد"); return; }
        if (string.IsNullOrWhiteSpace(EditReason)) { Dialogs.Error("اكتب سبب التعديل أولًا"); return; }
        var line = SelectedLine;
        var original = line.OriginalValue ?? line.Value;
        _adjustments.RemoveAll(a => a.Kind is AdjustmentKind.Edit or AdjustmentKind.Exclude && a.Section == line.Section && a.Description == line.Description);
        if (kind == AdjustmentKind.Edit && EditValue == original) { await ComputeAsync(); return; }   // رجوع للقيمة الأصلية = لا تعديل
        _adjustments.Add(new ReconciliationAdjustment(kind, line.Section, line.Description, original, kind == AdjustmentKind.Exclude ? 0 : EditValue, EditReason!));
        EditReason = null;
        await ComputeAsync();
        StatusMessage = kind == AdjustmentKind.Exclude ? "استُبعد البند من الحساب" : "عُدّلت قيمة البند — راجع أثرها على الفائض ثم اعتمد";
    }

    private async Task RevertSelectedAsync()
    {
        if (SelectedLine is null) { Dialogs.Error("اختر البند المعدّل أولًا"); return; }
        var line = SelectedLine;
        var n = _adjustments.RemoveAll(a => a.Description == line.Description && (a.Section == line.Section || (line.IsManual && a.Kind is AdjustmentKind.ManualAsset or AdjustmentKind.ManualLiability)));
        if (n == 0) { Dialogs.Error("هذا البند غير معدّل"); return; }
        await ComputeAsync();
        StatusMessage = "أُلغي التعديل ورجع البند لقيمته المحسوبة";
    }

    private async Task AddManualAsync()
    {
        if (string.IsNullOrWhiteSpace(ManualDescription)) { Dialogs.Error("اكتب وصف البند اليدوي"); return; }
        if (ManualAmount <= 0) { Dialogs.Error("اكتب مبلغًا أكبر من صفر"); return; }
        if (string.IsNullOrWhiteSpace(EditReason)) { Dialogs.Error("اكتب سبب إضافة البند"); return; }
        _adjustments.Add(new ReconciliationAdjustment(ManualIsLiability ? AdjustmentKind.ManualLiability : AdjustmentKind.ManualAsset,
            "", ManualDescription!.Trim(), 0, ManualAmount, EditReason!));
        ManualDescription = null; ManualAmount = 0; EditReason = null;
        await ComputeAsync();
        StatusMessage = "أُضيف البند اليدوي — راجع أثره على الفائض ثم اعتمد";
    }

    public IReadOnlyList<Option<FinishedGoodsValuation>> Valuations { get; } = ArabicLabels.OptionsOf<FinishedGoodsValuation>();
    public ObservableCollection<ReconciliationLineDto> Lines { get; } = new();
    public ObservableCollection<PartnerSharePreview> Shares { get; } = new();
    public ObservableCollection<string> Warnings { get; } = new();
    public ObservableCollection<ReconciliationListRow> History { get; } = new();

    public AsyncRelayCommand ComputeCommand { get; }
    public AsyncRelayCommand PostCommand { get; }
    public RelayCommand PrintPreviewCommand { get; }
    public AsyncRelayCommand PrintCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    private string? _deleteReason;
    /// <summary>سبب حذف آخر مطابقة (إلزامي، يُسجَّل في سجل التدقيق).</summary>
    public string? DeleteReason { get => _deleteReason; set => SetProperty(ref _deleteReason, value); }

    public DateTime Date { get => _date; set { if (SetProperty(ref _date, value)) { _adjustments.Clear(); Snapshot = null; } } }
    public Option<FinishedGoodsValuation> Valuation { get => _valuation; set { if (SetProperty(ref _valuation, value)) { _adjustments.Clear(); Background(ComputeAsync()); } } }
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
                foreach (var e in value.AdjustmentErrors) Warnings.Add("تعديل يدوي: " + e);
                foreach (var w in value.Warnings) Warnings.Add(w);
            }
            SelectedLine = null;
            foreach (var n in new[] { nameof(HasSnapshot), nameof(SurplusText), nameof(SurplusLabel), nameof(HasWarnings), nameof(SharesHint), nameof(ShowAdjustTools), nameof(HasAdjustments), nameof(AdjustmentBanner) }) OnPropertyChanged(n);
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
        var rows = await new ReconciliationService(db).GetReconciliationsAsync();
        if (rows.Count > 0) rows[0].IsLast = true;   // الأحدث أولًا — يُعيَّن قبل الإضافة كي يقرأه الربط عند بناء الصف
        foreach (var r in rows) History.Add(r);
        CanAdjust = await new CashBoxService(db).IsAdminAsync(Session.UserId);
    }

    private async Task DeleteAsync(ReconciliationListRow row)
    {
        if (!row.IsLast) { Dialogs.Error("تُحذف آخر مطابقة فقط"); return; }
        if (string.IsNullOrWhiteSpace(DeleteReason)) { Dialogs.Error("اكتب سبب الحذف في خانة «سبب الحذف» أولًا"); return; }
        var effect = row.IsBaseline ? "مطابقة أساس بلا توزيع" : $"سيُعكس توزيع {row.Surplus:N0} د.ع على الشركاء ويعود رصيد كل شريك كما كان";
        if (!Dialogs.Confirm($"حذف المطابقة {row.ReconNumber}؟\n{effect}.\nيبقى القيد وعكسه في الدفاتر (صافيهما صفر)، وتبقى حصص الشركاء وعكسها في كشوفهم.")) return;
        await using var db = Session.NewDb();
        var reason = DeleteReason!;
        if (await RunOperationAsync(() => new ReconciliationService(db).DeleteLastAsync(row.Id, reason, Session.UserId), $"حُذفت المطابقة {row.ReconNumber}"))
        {
            DeleteReason = null;
            Snapshot = null;
            await LoadAsync();
        }
    }

    public async Task ComputeAsync()
    {
        await using var db = Session.NewDb();
        Snapshot = await new ReconciliationService(db).ComputeAsync(Date, Valuation.Value, _adjustments.ToList());
        StatusMessage = $"حُسبت المطابقة ({Valuation.Label} للمنتج التام)";
    }

    private async Task PostAsync()
    {
        if (Snapshot is null) await ComputeAsync();
        var s = Snapshot!;
        if (s.AdjustmentErrors.Count > 0) { Dialogs.Error("تعديلات يدوية غير صالحة: " + string.Join(" — ", s.AdjustmentErrors)); return; }
        var question = s.IsBaseline
            ? $"اعتماد مطابقة الأساس بصافي موجودات {s.NetAssets:N0} د.ع؟"
            : $"اعتماد المطابقة وتوزيع {(s.Surplus >= 0 ? "فائض" : "خسارة")} {s.Surplus:N0} د.ع على الشركاء؟ يمكن للأدمن حذف آخر مطابقة لاحقًا إن لزم.";
        if (s.HasAdjustments) question += $"\n\n{AdjustmentBanner}. ستُطبع البنود المعدّلة بقيمتها الأصلية وسببها.";
        if (!Dialogs.Confirm(question)) return;
        await using var db = Session.NewDb();
        var (result, recon) = await new ReconciliationService(db).PostAsync(Date, Valuation.Value, Notes, Session.UserId, _adjustments.ToList());
        if (!result.Success) { Dialogs.Error(result.ErrorMessage ?? "تعذّر الاعتماد"); return; }
        _adjustments.Clear();
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
    private CashBoxRow? _withdrawBox;

    public PartnersSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Finance, "الشركاء والأرباح", Icons.People, "#7C3AED", "نسب الشركاء، أرصدة أرباحهم، السحب، وكشف كل شريك")
    {
        NewCommand = new RelayCommand(() => { Selected = null; ClearEditor(); });
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        WithdrawCommand = new AsyncRelayCommand(WithdrawAsync);
        PrintStatementCommand = new AsyncRelayCommand(() => Selected is null ? Fail("اختر الشريك") : PrintAsync(db => DocumentReports.PartnerStatementAsync(Session, db, Selected.Id)));
    }

    protected override bool HasPendingInput => Amount != 0;

    protected override void ResetInput()
    {
        Amount = 0;
        Date = DateTime.Today;
        WithdrawNotes = null;
    }
    protected override bool ReloadOnActivate => true;

    public ObservableCollection<PartnerBalanceRow> Partners { get; } = new();
    /// <summary>صناديق السحب: النقدية فقط (المنزل، الرئيسي، صناديق المستخدمين) — لا بنك ولا بطاقات.</summary>
    public ObservableCollection<CashBoxRow> WithdrawBoxes { get; } = new();
    public CashBoxRow? WithdrawBox { get => _withdrawBox; set => SetProperty(ref _withdrawBox, value); }
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
        var keepBox = WithdrawBox?.Id;
        WithdrawBoxes.Clear();
        var boxes = (await new CashBoxService(db).GetBoxesAsync(Session.UserId))
            .Where(b => b.BoxType is CashBoxType.Home or CashBoxType.Main or CashBoxType.User).ToList();
        foreach (var b in boxes.OrderBy(b => b.BoxType == CashBoxType.Home ? 0 : b.BoxType == CashBoxType.Main ? 1 : 2).ThenBy(b => b.Name)) WithdrawBoxes.Add(b);
        // الافتراضي صندوق المنزل (الأرباح تُسحب منه)؛ وإن لم يكن فيه رصيد فالرئيسي
        WithdrawBox = WithdrawBoxes.FirstOrDefault(b => b.Id == keepBox)
                      ?? WithdrawBoxes.FirstOrDefault(b => b.BoxType == CashBoxType.Home && b.Balance > 0)
                      ?? WithdrawBoxes.FirstOrDefault(b => b.BoxType == CashBoxType.Main) ?? WithdrawBoxes.FirstOrDefault();
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
        var box = WithdrawBox;
        // الافتراضي صندوق المنزل؛ إن لم يكفِ رصيده ويكفي الرئيسي يُعرض التحويل إليه (غير ذلك تتولى الخدمة الرسالة)
        if (box is { BoxType: CashBoxType.Home } && box.Balance < Amount
            && WithdrawBoxes.FirstOrDefault(b => b.BoxType == CashBoxType.Main && b.Balance >= Amount) is { } main)
        {
            if (!Dialogs.Confirm($"رصيد {box.Name} {box.Balance:N0} د.ع لا يكفي. هل يُسحب من «{main.Name}» ({main.Balance:N0} د.ع)؟")) return;
            box = main;
        }
        await using var db = Session.NewDb();
        // سحب الأرباح يخرج به نقد: طلب صرف ينتظر موافقة المالية، وسند السحب يُطبع عند الاعتماد
        var (result, request) = await new PaymentApprovalService(db).RequestPartnerWithdrawalAsync(Selected.Id, Amount, Date, WithdrawNotes, box?.Id, Session.UserId);
        if (!result.Success) { Dialogs.Error(result.ErrorMessage ?? "تعذّر تقديم الطلب"); return; }
        StatusMessage = $"أُرسل طلب سحب {Amount:N0} د.ع من أرباح {Selected.Name} ({request!.RequestNumber}) لموافقة القسم المالي — يُصرف بعد اعتماده من «طلبات الصرف»";
        Amount = 0;
        WithdrawNotes = null;
    }
}
