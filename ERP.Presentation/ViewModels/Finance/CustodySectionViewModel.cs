using System.Collections.ObjectModel;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.Finance;

/// <summary>
/// «العهد»: جدول العهد الفعّالة والمسوّاة بفلاتر، وصرف عهدة لموظف من صندوق، وتجديدها وإعادة متبقيها وتسويتها،
/// وتعديل أي حركة فيها أو إلغاؤها بسبب (تبقى ظاهرة بقيد عكسي)، وطباعة كشفها بتفاصيله. شاشة واحدة بلا نوافذ إضافية.
/// </summary>
public class CustodySectionViewModel : SectionViewModel
{
    private int _filter;                 // 0 فعّالة، 1 مسوّاة، 2 الكل
    private Employee? _filterEmployee;
    private DateTime? _from, _to;
    private CustodyRow? _selected;
    private CustodyTxRow? _selectedTx;
    // صرف عهدة جديدة
    private Employee? _issueEmployee;
    private decimal _issueAmount;
    private string? _issuePurpose;
    private DateTime _issueDate = DateTime.Today;
    private CashBox? _issueBox;
    // إجراءات العهدة المحددة
    private decimal _topUpAmount, _returnAmount, _expenseAmount, _editAmount;
    private string? _expenseDescription, _expenseReceipt, _editDescription, _editReceipt, _reason;
    private FinanceCategory? _expenseCategory, _editCategory;
    private DateTime _editDate = DateTime.Today;
    // الإعدادات
    private int _alertDays = 7, _lowPercent = 20;
    private decimal _receiptAbove = 25_000;

    public CustodySectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Finance, "العهد", Icons.Currency, "#2563EB",
               "عهد نقدية لمسؤول المشتريات والحالات الطارئة: صرف وتجديد وتسوية وتعديل وإلغاء وطباعة")
    {
        IssueCommand = new AsyncRelayCommand(IssueAsync);
        RenewCommand = new AsyncRelayCommand(RenewAsync);
        TopUpCommand = new AsyncRelayCommand(TopUpAsync);
        ReturnCommand = new AsyncRelayCommand(() => ReturnAsync(settle: false));
        SettleCommand = new AsyncRelayCommand(() => ReturnAsync(settle: true));
        AddExpenseCommand = new AsyncRelayCommand(AddExpenseAsync);
        SaveEditCommand = new AsyncRelayCommand(SaveEditAsync);
        VoidTxCommand = new AsyncRelayCommand(VoidTxAsync);
        VoidCustodyCommand = new AsyncRelayCommand(VoidCustodyAsync);
        PrintCommand = new AsyncRelayCommand(() => Selected is null ? Task.CompletedTask : PrintAsync(db => DocumentReports.CustodyAsync(Session, db, Selected.Id)));
        PrintListCommand = new RelayCommand(() => Dialogs.ShowReport(DocumentReports.CustodyList(Session, Rows.ToList(), FilterText)));
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync);
    }

    protected override bool ReloadOnActivate => true;
    protected override bool HasPendingInput => IssueAmount > 0 || ExpenseAmount > 0;
    protected override void ResetInput()
    {
        IssueAmount = 0; IssuePurpose = null; IssueDate = DateTime.Today; TopUpAmount = 0; ReturnAmount = 0; ExpenseAmount = 0; ExpenseDescription = null; ExpenseReceipt = null; Reason = null;
    }

    public ObservableCollection<CustodyRow> Rows { get; } = new();
    public ObservableCollection<CustodyTxRow> TxRows { get; } = new();
    public ObservableCollection<Employee> Employees { get; } = new();
    public ObservableCollection<CashBox> Boxes { get; } = new();
    public ObservableCollection<FinanceCategory> Categories { get; } = new();

    public bool CanManage => Has(SpecialPermission.Custody);
    public int Filter { get => _filter; set { if (SetProperty(ref _filter, value)) { OnPropertyChanged(nameof(FilterText)); Background(LoadListAsync()); } } }
    public bool ShowActive { get => Filter == 0; set { if (value) Filter = 0; } }
    public bool ShowSettled { get => Filter == 1; set { if (value) Filter = 1; } }
    public bool ShowAll { get => Filter == 2; set { if (value) Filter = 2; } }
    public Employee? FilterEmployee { get => _filterEmployee; set { if (SetProperty(ref _filterEmployee, value)) Background(LoadListAsync()); } }
    public DateTime? From { get => _from; set { if (SetProperty(ref _from, value)) Background(LoadListAsync()); } }
    public DateTime? To { get => _to; set { if (SetProperty(ref _to, value)) Background(LoadListAsync()); } }
    public string FilterText => (Filter switch { 0 => "الفعّالة", 1 => "المسوّاة", _ => "الكل" }) + (FilterEmployee is null ? "" : $" — {FilterEmployee.FullName}");
    public string Summary { get; private set; } = "";

    public CustodyRow? Selected
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value)) { OnPropertyChanged(nameof(HasSelected)); OnPropertyChanged(nameof(CanAct)); Background(LoadDetailAsync()); } }
    }
    public bool HasSelected => Selected is not null;
    public bool CanAct => CanManage && Selected is { Status: not CustodyStatus.Voided };
    public CustodyTxRow? SelectedTx
    {
        get => _selectedTx;
        set
        {
            if (!SetProperty(ref _selectedTx, value)) return;
            OnPropertyChanged(nameof(CanEditTx));
            if (value is null) return;
            EditAmount = value.Amount; EditDescription = value.Description; EditReceipt = value.ReceiptNumber; EditDate = value.Date;
            EditCategory = Categories.FirstOrDefault(c => c.Name == value.Category);
        }
    }
    public bool CanEditTx => CanManage && SelectedTx is { IsVoided: false } t && t.Kind != CustodyTxKind.Return && Selected is { Status: not CustodyStatus.Voided };

    public Employee? IssueEmployee { get => _issueEmployee; set => SetProperty(ref _issueEmployee, value); }
    public decimal IssueAmount { get => _issueAmount; set => SetProperty(ref _issueAmount, value); }
    public string? IssuePurpose { get => _issuePurpose; set => SetProperty(ref _issuePurpose, value); }
    public DateTime IssueDate { get => _issueDate; set => SetProperty(ref _issueDate, value); }
    public CashBox? IssueBox { get => _issueBox; set => SetProperty(ref _issueBox, value); }

    public decimal TopUpAmount { get => _topUpAmount; set => SetProperty(ref _topUpAmount, value); }
    /// <summary>صفر = كل المتبقي.</summary>
    public decimal ReturnAmount { get => _returnAmount; set => SetProperty(ref _returnAmount, value); }
    public decimal ExpenseAmount { get => _expenseAmount; set => SetProperty(ref _expenseAmount, value); }
    public FinanceCategory? ExpenseCategory { get => _expenseCategory; set => SetProperty(ref _expenseCategory, value); }
    public string? ExpenseDescription { get => _expenseDescription; set => SetProperty(ref _expenseDescription, value); }
    public string? ExpenseReceipt { get => _expenseReceipt; set => SetProperty(ref _expenseReceipt, value); }
    public decimal EditAmount { get => _editAmount; set => SetProperty(ref _editAmount, value); }
    public FinanceCategory? EditCategory { get => _editCategory; set => SetProperty(ref _editCategory, value); }
    public string? EditDescription { get => _editDescription; set => SetProperty(ref _editDescription, value); }
    public string? EditReceipt { get => _editReceipt; set => SetProperty(ref _editReceipt, value); }
    public DateTime EditDate { get => _editDate; set => SetProperty(ref _editDate, value); }
    public string? Reason { get => _reason; set => SetProperty(ref _reason, value); }

    public int AlertDays { get => _alertDays; set => SetProperty(ref _alertDays, value); }
    public int LowPercent { get => _lowPercent; set => SetProperty(ref _lowPercent, value); }
    public decimal ReceiptAbove { get => _receiptAbove; set => SetProperty(ref _receiptAbove, value); }

    public AsyncRelayCommand IssueCommand { get; }
    public AsyncRelayCommand RenewCommand { get; }
    public AsyncRelayCommand TopUpCommand { get; }
    public AsyncRelayCommand ReturnCommand { get; }
    public AsyncRelayCommand SettleCommand { get; }
    public AsyncRelayCommand AddExpenseCommand { get; }
    public AsyncRelayCommand SaveEditCommand { get; }
    public AsyncRelayCommand VoidTxCommand { get; }
    public AsyncRelayCommand VoidCustodyCommand { get; }
    public AsyncRelayCommand PrintCommand { get; }
    public RelayCommand PrintListCommand { get; }
    public AsyncRelayCommand SaveSettingsCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var keepEmployee = FilterEmployee?.Id;
        Employees.Clear();
        foreach (var e in await db.Employees.AsNoTracking().Where(e => e.IsActive).OrderBy(e => e.FullName).ToListAsync()) Employees.Add(e);
        _filterEmployee = Employees.FirstOrDefault(e => e.Id == keepEmployee);
        OnPropertyChanged(nameof(FilterEmployee));
        Boxes.Clear();
        foreach (var b in await db.CashBoxes.AsNoTracking().Where(b => b.IsActive).OrderBy(b => b.Name).ToListAsync()) Boxes.Add(b);
        IssueBox ??= Boxes.FirstOrDefault(b => b.IsDefault) ?? Boxes.FirstOrDefault();
        Categories.Clear();
        foreach (var c in await new FinanceEntryService(db).GetCategoriesAsync()) if (c.Kind != FinanceCategoryKind.OtherIncome) Categories.Add(c);
        ExpenseCategory ??= Categories.FirstOrDefault();
        var st = await new CustodyService(db).SettingsAsync();
        (AlertDays, LowPercent, ReceiptAbove) = (st.CustodyAlertDays, st.CustodyLowPercent, st.CustodyReceiptAbove);
        await LoadListAsync();
    }

    private async Task LoadListAsync()
    {
        await using var db = Session.NewDb();
        var keep = Selected?.Id;
        var status = Filter switch { 0 => CustodyStatus.Active, 1 => CustodyStatus.Settled, _ => (CustodyStatus?)null };
        var list = await new CustodyService(db).ListAsync(status, FilterEmployee?.Id, From, To);
        Rows.Clear();
        foreach (var r in list) Rows.Add(r);
        var open = Rows.Where(r => r.Status == CustodyStatus.Active).ToList();
        var late = open.Count(r => r.IsLate);
        Summary = $"{Rows.Count} عهدة · المصروف لها {Rows.Sum(r => r.Issued):N0} · المصروف منها {Rows.Sum(r => r.Spent):N0} · الرصيد المفتوح {open.Sum(r => r.Balance):N0} د.ع"
                  + (late > 0 ? $" · ⚠ {late} متأخرة بلا تسوية" : "");
        OnPropertyChanged(nameof(Summary));
        _selected = Rows.FirstOrDefault(r => r.Id == keep) ?? Rows.FirstOrDefault();
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(HasSelected));
        OnPropertyChanged(nameof(CanAct));
        await LoadDetailAsync();
    }

    private async Task LoadDetailAsync()
    {
        TxRows.Clear();
        _selectedTx = null;
        OnPropertyChanged(nameof(SelectedTx));
        OnPropertyChanged(nameof(CanEditTx));
        if (Selected is null) return;
        await using var db = Session.NewDb();
        if (await new CustodyService(db).DetailAsync(Selected.Id) is { } d) foreach (var t in d.Rows) TxRows.Add(t);
    }

    private async Task<bool> RunAsync(Func<CustodyService, Task<FinanceOperationResult>> op, string success)
    {
        await using var db = Session.NewDb();
        if (!await RunOperationAsync(() => op(new CustodyService(db)), success)) return false;
        await LoadListAsync();
        return true;
    }

    private async Task IssueAsync()
    {
        if (!Require(CanManage, "إدارة العهد")) return;
        if (IssueEmployee is null) { Dialogs.Error("اختر الموظف"); return; }
        if (IssueAmount <= 0) { Dialogs.Error("اكتب مبلغ العهدة"); return; }
        if (!Dialogs.Confirm($"صرف {IssueAmount:N0} د.ع من «{IssueBox?.Name}» إلى {IssueEmployee.FullName}؟")) return;
        int? id = null;
        await using var db = Session.NewDb();
        if (await RunOperationAsync(async () =>
            {
                var (r, cid) = await new CustodyService(db).IssueAsync(IssueEmployee.Id, IssueAmount, IssuePurpose ?? "", IssueDate, Session.UserId, IssueBox?.Id);
                id = cid;
                return r;
            }, "صُرفت العهدة"))
        {
            IssueAmount = 0; IssuePurpose = null;
            await LoadListAsync();
            Selected = Rows.FirstOrDefault(r => r.Id == id) ?? Selected;
        }
    }

    private async Task RenewAsync()
    {
        if (Selected is null) { Dialogs.Error("اختر العهدة"); return; }
        var s = Selected;
        if (!Dialogs.Confirm($"تجديد {s.Number} إلى {s.TargetAmount:N0} د.ع؟ يُصرف الفرق {Math.Max(0, s.TargetAmount - s.Balance):N0} د.ع من الصندوق.")) return;
        await RunAsync(c => c.RenewToTargetAsync(s.Id, DateTime.Today, Session.UserId, IssueBox?.Id), $"جُدّدت {s.Number}");
    }

    private async Task TopUpAsync()
    {
        if (Selected is null) { Dialogs.Error("اختر العهدة"); return; }
        if (TopUpAmount <= 0) { Dialogs.Error("اكتب مبلغ التعبئة"); return; }
        var s = Selected;
        if (!Dialogs.Confirm($"تعبئة {s.Number} بمبلغ {TopUpAmount:N0} د.ع؟")) return;
        if (await RunAsync(c => c.TopUpAsync(s.Id, TopUpAmount, DateTime.Today, Session.UserId, IssueBox?.Id), $"عُبّئت {s.Number}")) TopUpAmount = 0;
    }

    private async Task ReturnAsync(bool settle)
    {
        if (Selected is null) { Dialogs.Error("اختر العهدة"); return; }
        var s = Selected;
        var amount = ReturnAmount > 0 ? ReturnAmount : (decimal?)null;
        var text = settle ? $"تسوية {s.Number}: إعادة المتبقي {s.Balance:N0} د.ع للصندوق وإغلاقها؟" : $"إعادة {(amount ?? s.Balance):N0} د.ع من {s.Number} للصندوق؟";
        if (!Dialogs.Confirm(text)) return;
        if (await RunAsync(c => c.ReturnAsync(s.Id, settle ? null : amount, settle, DateTime.Today, Session.UserId, IssueBox?.Id), settle ? $"سُوّيت {s.Number}" : "أُعيد المبلغ للصندوق")) ReturnAmount = 0;
    }

    private async Task AddExpenseAsync()
    {
        if (Selected is null) { Dialogs.Error("اختر العهدة"); return; }
        if (ExpenseCategory is null) { Dialogs.Error("اختر باب المصروف"); return; }
        if (ExpenseAmount <= 0) { Dialogs.Error("اكتب مبلغ المصروف"); return; }
        var s = Selected;
        var input = new CustodyExpenseInput(s.Id, ExpenseAmount, ExpenseCategory.Id, ExpenseDescription, ExpenseReceipt, DateTime.Today);
        await using var db = Session.NewDb();
        if (await RunOperationAsync(async () => (await new CustodyService(db).AddExpenseAsync(input, Session.UserId)).result, "سُجّل المصروف على العهدة"))
        {
            ExpenseAmount = 0; ExpenseDescription = null; ExpenseReceipt = null;
            await LoadListAsync();
        }
    }

    private async Task SaveEditAsync()
    {
        if (SelectedTx is not { } t) { Dialogs.Error("اختر الحركة من الكشف"); return; }
        if (!Dialogs.Confirm($"تعديل {t.KindText} إلى {EditAmount:N0} د.ع؟")) return;
        if (await RunAsync(c => c.EditAsync(t.Id, EditAmount, EditCategory?.Id, EditDescription, EditReceipt, EditDate, Reason ?? "", Session.UserId), "تم التعديل")) Reason = null;
    }

    private async Task VoidTxAsync()
    {
        if (SelectedTx is not { } t) { Dialogs.Error("اختر الحركة من الكشف"); return; }
        if (!Dialogs.Confirm($"إلغاء {t.KindText} {t.Amount:N0} د.ع؟ تبقى ظاهرة مشطوبة بقيد عكسي.")) return;
        if (await RunAsync(c => c.VoidTxAsync(t.Id, Reason ?? "", Session.UserId), "أُلغيت الحركة")) Reason = null;
    }

    private async Task VoidCustodyAsync()
    {
        if (Selected is null) { Dialogs.Error("اختر العهدة"); return; }
        var s = Selected;
        if (!Dialogs.Confirm($"إلغاء العهدة {s.Number} كاملة؟")) return;
        if (await RunAsync(c => c.VoidCustodyAsync(s.Id, Reason ?? "", Session.UserId), $"أُلغيت {s.Number}")) Reason = null;
    }

    private async Task SaveSettingsAsync()
    {
        await using var db = Session.NewDb();
        await RunOperationAsync(() => new CustodyService(db).SaveSettingsAsync(AlertDays, LowPercent, ReceiptAbove, Session.UserId), "حُفظت إعدادات التنبيه");
    }
}
