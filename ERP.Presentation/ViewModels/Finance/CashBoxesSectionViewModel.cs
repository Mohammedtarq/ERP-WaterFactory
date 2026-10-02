using System.Collections.ObjectModel;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.Finance;

/// <summary>
/// الصناديق المالية: رصيد كل صندوق وكشفه، إيداع وسحب ومناقلة، وطباعة كل حركة وكل كشف.
/// - الأدمن يرى كل الصناديق ويُنشئها، ويعدّل الحركات اليدوية أو يلغيها (مع سبب، ويبقى أثرها في السجل).
/// - غير الأدمن يرى صندوقه فقط (نفس الشاشة تظهر كـ "صندوقي" في المبيعات).
/// </summary>
public class CashBoxesSectionViewModel : SectionViewModel
{
    private CashBoxRow? _selectedBox;
    private DateTime _from = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _to = DateTime.Today;
    private decimal _opening;
    private bool _isAdmin;
    private Option<CashBoxTxType> _action;
    private decimal _amount;
    private DateTime _txDate = DateTime.Today;
    private string? _party;
    private string? _description;
    private CashBox? _targetBox;
    private CashBoxTxRow? _editing;
    private decimal _editAmount;
    private DateTime _editDate;
    private string? _editDescription;
    private string? _voidReason;
    private string _newBoxName = "";
    private Option<CashBoxType> _newBoxType;
    private User? _newBoxOwner;
    private bool _newBoxDefault;

    public CashBoxesSectionViewModel(AppSession s, IDialogService d, string moduleCode = ModuleCode.Finance, string title = "الصناديق")
        : base(s, d, moduleCode, title, Icons.Currency, "#0EA5E9",
               title == "الصناديق" ? "صناديق النقد: إيداع، سحب، مناقلة، وكشف كل صندوق" : "رصيد صندوقك وحركاته، وتسليم النقد لصندوق آخر")
    {
        ActionOptions = new[]
        {
            new Option<CashBoxTxType>(CashBoxTxType.Deposit, "إيداع"),
            new Option<CashBoxTxType>(CashBoxTxType.Withdrawal, "سحب / صرف"),
            new Option<CashBoxTxType>(CashBoxTxType.TransferOut, "مناقلة إلى صندوق آخر")
        };
        _action = ActionOptions[0];
        _newBoxType = BoxTypeOptions[1];
        SubmitCommand = new AsyncRelayCommand(SubmitAsync);
        PrintTxCommand = new AsyncRelayCommand(p => p is CashBoxTxRow r ? PrintTxAsync(r.Id) : Task.CompletedTask);
        EditCommand = new RelayCommand(p => { if (p is CashBoxTxRow r) BeginEdit(r); });
        SaveEditCommand = new AsyncRelayCommand(SaveEditAsync);
        VoidCommand = new AsyncRelayCommand(VoidAsync);
        CancelEditCommand = new RelayCommand(() => Editing = null);
        PrintStatementCommand = new RelayCommand(() => { if (SelectedBox is null) Dialogs.Error("اختر الصندوق"); else Dialogs.ShowReport(BuildStatementReport()); });
        LoadStatementCommand = new AsyncRelayCommand(LoadStatementAsync);
        CreateBoxCommand = new AsyncRelayCommand(CreateBoxAsync);
    }

    protected override bool ReloadOnActivate => true;

    public ObservableCollection<CashBoxRow> Boxes { get; } = new();
    public ObservableCollection<CashBox> TransferTargets { get; } = new();
    public ObservableCollection<CashBoxTxRow> Rows { get; } = new();
    public ObservableCollection<User> Users { get; } = new();

    public bool IsAdmin { get => _isAdmin; private set => SetProperty(ref _isAdmin, value); }
    public bool HasBoxes => Boxes.Count > 0;
    public string NoBoxText => IsAdmin ? "لا توجد صناديق بعد — أنشئ صندوقًا من اللوحة الجانبية"
                                       : "لا يوجد صندوق باسمك. يطلبه المدير من المالية ← الصناديق (صندوق مستخدم).";

    public CashBoxRow? SelectedBox
    {
        get => _selectedBox;
        set
        {
            if (!SetProperty(ref _selectedBox, value)) return;
            Editing = null;
            RefreshTargets();
            Background(LoadStatementAsync());
        }
    }

    public DateTime From { get => _from; set => SetProperty(ref _from, value); }
    public DateTime To { get => _to; set => SetProperty(ref _to, value); }
    public decimal Opening { get => _opening; private set => SetProperty(ref _opening, value); }
    public decimal TotalIn => Rows.Where(r => !r.IsVoided).Sum(r => r.In);
    public decimal TotalOut => Rows.Where(r => !r.IsVoided).Sum(r => r.Out);
    public decimal Closing => Opening + TotalIn - TotalOut;

    // ---------------- حركة جديدة ----------------
    public IReadOnlyList<Option<CashBoxTxType>> ActionOptions { get; }
    public Option<CashBoxTxType> Action
    {
        get => _action;
        set { if (SetProperty(ref _action, value)) { OnPropertyChanged(nameof(IsTransfer)); OnPropertyChanged(nameof(PartyLabel)); } }
    }
    public bool IsTransfer => Action.Value == CashBoxTxType.TransferOut;
    public string PartyLabel => Action.Value == CashBoxTxType.Deposit ? "المودِع / المصدر" : "المستلم / الجهة";
    public decimal Amount { get => _amount; set { if (SetProperty(ref _amount, value)) OnPropertyChanged(nameof(AmountWords)); } }
    public string AmountWords => Amount > 0 ? ArabicNumberWords.Amount(Amount) : "";
    public DateTime TxDate { get => _txDate; set => SetProperty(ref _txDate, value); }
    public string? Party { get => _party; set => SetProperty(ref _party, value); }
    public string? TxDescription { get => _description; set => SetProperty(ref _description, value); }
    public CashBox? TargetBox { get => _targetBox; set => SetProperty(ref _targetBox, value); }

    // ---------------- تعديل / إلغاء (أدمن) ----------------
    public CashBoxTxRow? Editing
    {
        get => _editing;
        private set { if (SetProperty(ref _editing, value)) OnPropertyChanged(nameof(IsEditing)); }
    }
    public bool IsEditing => Editing is not null;
    public decimal EditAmount { get => _editAmount; set => SetProperty(ref _editAmount, value); }
    public DateTime EditDate { get => _editDate; set => SetProperty(ref _editDate, value); }
    public string? EditDescription { get => _editDescription; set => SetProperty(ref _editDescription, value); }
    public string? VoidReason { get => _voidReason; set => SetProperty(ref _voidReason, value); }

    // ---------------- إنشاء صندوق (أدمن) ----------------
    public IReadOnlyList<Option<CashBoxType>> BoxTypeOptions { get; } = ArabicLabels.OptionsOf<CashBoxType>();
    public string NewBoxName { get => _newBoxName; set => SetProperty(ref _newBoxName, value); }
    public Option<CashBoxType> NewBoxType
    {
        get => _newBoxType;
        set { if (SetProperty(ref _newBoxType, value)) OnPropertyChanged(nameof(NewBoxIsUser)); }
    }
    public bool NewBoxIsUser => NewBoxType.Value == CashBoxType.User;
    public User? NewBoxOwner { get => _newBoxOwner; set => SetProperty(ref _newBoxOwner, value); }
    public bool NewBoxDefault { get => _newBoxDefault; set => SetProperty(ref _newBoxDefault, value); }

    public AsyncRelayCommand SubmitCommand { get; }
    public AsyncRelayCommand PrintTxCommand { get; }
    public RelayCommand EditCommand { get; }
    public AsyncRelayCommand SaveEditCommand { get; }
    public AsyncRelayCommand VoidCommand { get; }
    public RelayCommand CancelEditCommand { get; }
    public RelayCommand PrintStatementCommand { get; }
    public AsyncRelayCommand LoadStatementCommand { get; }
    public AsyncRelayCommand CreateBoxCommand { get; }

    /// <summary>آخر حركة سُجّلت من الشاشة (للاختبارات).</summary>
    public CashBoxTransaction? LastTransaction { get; private set; }

    // ============================ التحميل ============================
    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var svc = new CashBoxService(db);
        IsAdmin = await svc.IsAdminAsync(Session.UserId);
        var selectedId = SelectedBox?.Id;
        Boxes.Clear();
        foreach (var b in await svc.GetBoxesAsync(Session.UserId)) Boxes.Add(b);
        TransferTargets.Clear();
        foreach (var t in await svc.GetTransferTargetsAsync()) TransferTargets.Add(t);
        if (IsAdmin && Users.Count == 0)
            foreach (var u in await db.Users.AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.Username).ToListAsync()) Users.Add(u);
        OnPropertyChanged(nameof(HasBoxes));
        OnPropertyChanged(nameof(NoBoxText));

        _selectedBox = Boxes.FirstOrDefault(b => b.Id == selectedId) ?? Boxes.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedBox));
        RefreshTargets();
        await LoadStatementAsync();
    }

    private void RefreshTargets()
    {
        if (TargetBox is not null && TargetBox.Id == SelectedBox?.Id) TargetBox = null;
        TargetBox ??= TransferTargets.FirstOrDefault(t => t.Id != SelectedBox?.Id && t.IsDefault)
                      ?? TransferTargets.FirstOrDefault(t => t.Id != SelectedBox?.Id);
    }

    public async Task LoadStatementAsync()
    {
        Rows.Clear();
        if (SelectedBox is not null)
        {
            await using var db = Session.NewDb();
            var (opening, rows) = await new CashBoxService(db).GetStatementAsync(SelectedBox.Id, From, To);
            Opening = opening;
            foreach (var r in rows) Rows.Add(r);
        }
        else Opening = 0;
        OnPropertyChanged(nameof(TotalIn));
        OnPropertyChanged(nameof(TotalOut));
        OnPropertyChanged(nameof(Closing));
    }

    private async Task ReloadBoxesKeepSelectionAsync()
    {
        var id = SelectedBox?.Id;
        await using var db = Session.NewDb();
        var boxes = await new CashBoxService(db).GetBoxesAsync(Session.UserId);
        Boxes.Clear();
        foreach (var b in boxes) Boxes.Add(b);
        _selectedBox = Boxes.FirstOrDefault(b => b.Id == id) ?? Boxes.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedBox));
        OnPropertyChanged(nameof(HasBoxes));
        await LoadStatementAsync();
    }

    // ============================ العمليات ============================
    private async Task SubmitAsync()
    {
        if (!Require(CanAdd, "تسجيل حركات الصندوق")) return;
        if (SelectedBox is null) { Dialogs.Error("اختر الصندوق"); return; }
        if (Amount <= 0) { Dialogs.Error("أدخل المبلغ"); return; }
        if (IsTransfer && TargetBox is null) { Dialogs.Error("اختر الصندوق المستلم"); return; }
        var confirm = Action.Value switch
        {
            CashBoxTxType.Deposit => $"إيداع {Amount:N0} د.ع في \"{SelectedBox.Name}\"؟",
            CashBoxTxType.Withdrawal => $"سحب {Amount:N0} د.ع من \"{SelectedBox.Name}\"؟",
            _ => $"مناقلة {Amount:N0} د.ع من \"{SelectedBox.Name}\" إلى \"{TargetBox!.Name}\"؟"
        };
        if (!Dialogs.Confirm(confirm)) return;

        IsBusy = true;
        try
        {
            await using var db = Session.NewDb();
            var svc = new CashBoxService(db);
            var (result, tx) = Action.Value switch
            {
                CashBoxTxType.Deposit => await svc.DepositAsync(SelectedBox.Id, Amount, TxDate, Party, TxDescription, Session.UserId),
                CashBoxTxType.Withdrawal => await svc.WithdrawAsync(SelectedBox.Id, Amount, TxDate, Party, TxDescription, Session.UserId),
                _ => await svc.TransferAsync(SelectedBox.Id, TargetBox!.Id, Amount, TxDate, TxDescription, Session.UserId)
            };
            if (!result.Success) { Dialogs.Error(result.ErrorMessage!); return; }
            LastTransaction = tx;
            StatusMessage = $"تم تسجيل {Action.Label}: {Amount:N0} د.ع ({tx!.TxNumber})";
            Amount = 0;
            Party = null;
            TxDescription = null;
            await ReloadBoxesKeepSelectionAsync();
            if (Dialogs.Confirm(StatusMessage + "\n\nطباعة الإيصال الآن؟")) await PrintTxAsync(tx.Id);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void BeginEdit(CashBoxTxRow row)
    {
        if (!IsAdmin) { Dialogs.Error("تعديل حركات الصندوق للأدمن فقط"); return; }
        if (!row.IsManual) { Dialogs.Error("هذه الحركة ناتجة عن مستند (فاتورة/سند) — تُعدَّل من مستندها الأصلي"); return; }
        if (row.IsVoided) { Dialogs.Error("الحركة ملغاة"); return; }
        EditAmount = row.In > 0 ? row.In : row.Out;
        EditDate = row.TxDate;
        EditDescription = row.Description;
        VoidReason = null;
        Editing = row;
    }

    private async Task SaveEditAsync()
    {
        if (Editing is null) return;
        if (!Dialogs.Confirm($"تعديل الحركة {Editing.TxNumber} إلى {EditAmount:N0} د.ع؟ يُحفظ المبلغ الأصلي واسمك ووقت التعديل.")) return;
        await using var db = Session.NewDb();
        var r = await new CashBoxService(db).UpdateAsync(Editing.Id, EditAmount, EditDate, EditDescription, Session.UserId);
        if (!r.Success) { Dialogs.Error(r.ErrorMessage!); return; }
        StatusMessage = $"تم تعديل الحركة {Editing.TxNumber}";
        Editing = null;
        await ReloadBoxesKeepSelectionAsync();
    }

    private async Task VoidAsync()
    {
        if (Editing is null) return;
        if (string.IsNullOrWhiteSpace(VoidReason)) { Dialogs.Error("اكتب سبب الإلغاء"); return; }
        if (!Dialogs.Confirm($"إلغاء الحركة {Editing.TxNumber}؟ تبقى ظاهرة في الكشف مشطوبة ولا تدخل الرصيد، ويُعكس قيدها.")) return;
        await using var db = Session.NewDb();
        var r = await new CashBoxService(db).VoidAsync(Editing.Id, VoidReason, Session.UserId);
        if (!r.Success) { Dialogs.Error(r.ErrorMessage!); return; }
        StatusMessage = $"تم إلغاء الحركة {Editing.TxNumber}";
        Editing = null;
        await ReloadBoxesKeepSelectionAsync();
    }

    private async Task CreateBoxAsync()
    {
        var box = new CashBox
        {
            Name = NewBoxName, BoxType = NewBoxType.Value, OwnerUserId = NewBoxIsUser ? NewBoxOwner?.Id : null, IsDefault = NewBoxDefault
        };
        await using var db = Session.NewDb();
        var r = await new CashBoxService(db).SaveBoxAsync(box, Session.UserId);
        if (!r.Success) { Dialogs.Error(r.ErrorMessage!); return; }
        StatusMessage = $"أُنشئ الصندوق \"{box.Name}\"";
        NewBoxName = "";
        NewBoxOwner = null;
        NewBoxDefault = false;
        await LoadAsync();
        SelectedBox = Boxes.FirstOrDefault(b => b.Id == box.Id);
    }

    // ============================ الطباعة ============================
    public async Task PrintTxAsync(int txId)
    {
        await using var db = Session.NewDb();
        var t = await new CashBoxService(db).GetTransactionAsync(txId);
        if (t is null) { Dialogs.Error("الحركة غير موجودة"); return; }
        Dialogs.ShowReport(BuildTxReport(t));
    }

    public ReportDocument BuildTxReport(CashBoxTransaction t)
    {
        var amount = Math.Abs(t.Amount);
        var title = t.TxType switch
        {
            CashBoxTxType.Deposit => "إيصال إيداع نقدي",
            CashBoxTxType.Withdrawal => "إيصال سحب / صرف نقدي",
            CashBoxTxType.TransferOut or CashBoxTxType.TransferIn => "مستند مناقلة بين الصناديق",
            _ => $"إيصال صندوق — {CashBoxService.TypeLabel(t.TxType)}"
        };
        var r = new ReportDocument
        {
            CompanyName = Session.ProjectName, Title = title, PrintedBy = Session.FullName,
            Stamp = t.IsVoided ? $"ملغاة — {t.VoidReason}" : t.ModifiedAt is not null ? "معدّلة" : null, Notes = t.Description
        };
        var transfer = t.TxType is CashBoxTxType.TransferOut or CashBoxTxType.TransferIn;
        r.Field("رقم الحركة", t.TxNumber)
         .Field("التاريخ", t.TxDate.ToString("yyyy/MM/dd"))
         .Field(transfer ? (t.TxType == CashBoxTxType.TransferOut ? "من صندوق" : "إلى صندوق") : "الصندوق", t.CashBox.Name)
         .Field(transfer ? (t.TxType == CashBoxTxType.TransferOut ? "إلى صندوق" : "من صندوق") : "الصندوق المقابل", t.CounterCashBox?.Name)
         .Field(t.Amount > 0 ? "المودِع / المصدر" : "المستلم / الجهة", t.PartyName)
         .Field("النوع", CashBoxService.TypeLabel(t.TxType))
         .Field("المستخدم", t.CreatedByUser.Username)
         .Field("عُدّلت بواسطة", t.ModifiedByUser is null ? null : $"{t.ModifiedByUser.Username} — {t.ModifiedAt!.Value.ToLocalTime():yyyy/MM/dd HH:mm}" +
                                                                    (t.OriginalAmount is { } o ? $" (الأصل {Math.Abs(o):N0})" : ""));
        r.Total("المبلغ", $"{amount:N0} د.ع", true).Total("المبلغ كتابةً", ArabicNumberWords.Amount(amount));
        r.Signatures.AddRange(t.Amount > 0 ? new[] { "المودِع", "أمين الصندوق" } : new[] { "المستلم", "أمين الصندوق", "المدير" });
        return r;
    }

    public ReportDocument BuildStatementReport()
    {
        var r = new ReportDocument { CompanyName = Session.ProjectName, Title = $"كشف صندوق — {SelectedBox!.Name}", PrintedBy = Session.FullName };
        r.Field("الصندوق", SelectedBox.Name).Field("النوع", ArabicLabels.Of(SelectedBox.BoxType)).Field("صاحب الصندوق", SelectedBox.OwnerUsername)
         .Field("الفترة", $"{From:yyyy/MM/dd} — {To:yyyy/MM/dd}");
        r.Columns.AddRange(new[] { "التاريخ", "رقم الحركة", "النوع", "البيان", "داخل", "خارج", "الرصيد" });
        r.Rows.Add(new[] { From.ToString("yyyy/MM/dd"), "", "رصيد أول المدة", "", "", "", $"{Opening:N0}" });
        foreach (var x in Rows)
            r.Rows.Add(new[]
            {
                x.TxDate.ToString("yyyy/MM/dd"), x.TxNumber, x.TypeLabel + (x.IsVoided ? " (ملغاة)" : ""),
                string.Join(" — ", new[] { x.PartyName, x.CounterBox, x.Description, x.Reference }.Where(s => !string.IsNullOrWhiteSpace(s))),
                x.In == 0 ? "" : $"{x.In:N0}", x.Out == 0 ? "" : $"{x.Out:N0}", x.IsVoided ? "" : $"{x.Balance:N0}"
            });
        r.Total("رصيد أول المدة", $"{Opening:N0} د.ع").Total("مجموع الداخل", $"{TotalIn:N0} د.ع").Total("مجموع الخارج", $"{TotalOut:N0} د.ع")
         .Total("رصيد آخر المدة", $"{Closing:N0} د.ع", true);
        r.Signatures.AddRange(new[] { "أمين الصندوق", "المحاسب", "المدير" });
        return r;
    }
}
