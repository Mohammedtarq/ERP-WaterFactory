using System.Collections.ObjectModel;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;

namespace ERP.Presentation.ViewModels.Finance;

/// <summary>
/// «طلبات الصرف» للقسم المالي: سلف ومسحوبات الموظفين وصرف الرواتب وسحب أرباح الشركاء المنتظرة اعتمادًا.
/// عند الاعتماد يُنفَّذ الصرف نفسه (قيد وحركة صندوق)؛ والرفض بسبب يصل مقدّم الطلب. لا يعتمد مقدّم الطلب طلبه بنفسه
/// إلا إن لم يوجد مسؤول مالي آخر. يسحب مقدّم الطلب طلبه ما دام منتظرًا.
/// </summary>
public class PaymentRequestsSectionViewModel : SectionViewModel
{
    private PaymentRequestRow? _selected;
    private bool _pendingOnly = true;
    private string? _rejectReason;

    public PaymentRequestsSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Finance, "طلبات الصرف", Icons.Currency, "#DC2626",
               "سلف ومسحوبات الموظفين وصرف الرواتب وسحب الشركاء — اعتماد المالية قبل خروج النقد")
    {
        ApproveCommand = new AsyncRelayCommand(ApproveAsync);
        RejectCommand = new AsyncRelayCommand(RejectAsync);
        WithdrawCommand = new AsyncRelayCommand(WithdrawAsync);
    }

    protected override bool ReloadOnActivate => true;
    public ObservableCollection<PaymentRequestRow> Rows { get; } = new();
    public bool CanApprove => Has(SpecialPermission.PaymentApproval);
    public bool PendingOnly { get => _pendingOnly; set { if (SetProperty(ref _pendingOnly, value)) Background(LoadAsync()); } }
    public string? RejectReason { get => _rejectReason; set => SetProperty(ref _rejectReason, value); }
    public int PendingCount { get; private set; }
    public decimal PendingTotal { get; private set; }
    public PaymentRequestRow? Selected
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value)) { OnPropertyChanged(nameof(CanDecide)); OnPropertyChanged(nameof(CanWithdraw)); } }
    }
    public bool CanDecide => CanApprove && Selected?.Status == PaymentRequestStatus.Pending;
    public bool CanWithdraw => Selected?.Status == PaymentRequestStatus.Pending;
    public AsyncRelayCommand ApproveCommand { get; }
    public AsyncRelayCommand RejectCommand { get; }
    public AsyncRelayCommand WithdrawCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var keep = Selected?.Id;
        var svc = new PaymentApprovalService(db);
        Rows.Clear();
        foreach (var r in await svc.ListAsync(PendingOnly)) Rows.Add(r);
        var pending = (await svc.ListAsync(true));
        (PendingCount, PendingTotal) = (pending.Count, pending.Sum(p => p.Amount));
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(PendingTotal));
        Selected = Rows.FirstOrDefault(r => r.Id == keep) ?? Rows.FirstOrDefault();
        StatusMessage = PendingCount == 0 ? "لا طلبات صرف بانتظار الاعتماد"
            : $"بانتظار الاعتماد: {PendingCount} بمبلغ {PendingTotal:N0} د.ع" + (CanApprove ? "" : $" — الاعتماد يحتاج صلاحية «{SpecialPermission.NameOf(SpecialPermission.PaymentApproval)}»");
    }

    private async Task ApproveAsync()
    {
        if (!Require(CanApprove, "اعتماد الصرف")) return;
        if (Selected is not { Status: PaymentRequestStatus.Pending } row) { Dialogs.Error("اختر طلبًا بانتظار الاعتماد"); return; }
        if (!Dialogs.Confirm($"اعتماد {row.RequestNumber}؟ يخرج النقد من الصندوق ويُقيَّد الآن.\n{row.Summary}")) return;
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new PaymentApprovalService(db).ApproveAsync(row.Id, Session.UserId), $"اعتُمد وصُرف {row.RequestNumber}"))
        {
            await LoadAsync();
            // سند الصرف يُطبع عند الاعتماد (يوم خروج النقد فعلًا)
            var done = (await new PaymentApprovalService(db).ListAsync(false)).FirstOrDefault(r => r.Id == row.Id);
            if (done?.ResultId is int id)
            {
                if (done.ResultTable == "EmployeeDeductions") await PrintAsync(d => DocumentReports.EmployeeDeductionAsync(Session, d, id));
                else if (done.ResultTable == "PartnerTransactions") await PrintAsync(d => DocumentReports.PartnerWithdrawalAsync(Session, d, id));
            }
        }
    }

    private async Task RejectAsync()
    {
        if (!Require(CanApprove, "رفض الصرف")) return;
        if (Selected is not { Status: PaymentRequestStatus.Pending } row) { Dialogs.Error("اختر طلبًا بانتظار الاعتماد"); return; }
        if (string.IsNullOrWhiteSpace(RejectReason)) { Dialogs.Error("اكتب سبب الرفض في خانة «سبب الرفض» ليصل لمقدّم الطلب"); return; }
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new PaymentApprovalService(db).RejectAsync(row.Id, RejectReason!, Session.UserId), $"رُفض {row.RequestNumber}"))
        {
            RejectReason = null;
            await LoadAsync();
        }
    }

    private async Task WithdrawAsync()
    {
        if (Selected is not { Status: PaymentRequestStatus.Pending } row) { Dialogs.Error("اختر طلبًا بانتظار الاعتماد"); return; }
        if (!Dialogs.Confirm($"سحب طلبك {row.RequestNumber}؟")) return;
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new PaymentApprovalService(db).WithdrawRequestAsync(row.Id, Session.UserId), $"سُحب {row.RequestNumber}"))
            await LoadAsync();
    }
}
