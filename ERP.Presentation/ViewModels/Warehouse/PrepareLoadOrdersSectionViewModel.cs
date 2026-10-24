using System.Collections.ObjectModel;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Reps;
using ERP.Presentation.ViewModels.Shell;

namespace ERP.Presentation.ViewModels.Warehouse;

/// <summary>
/// «طلبات التجهيز» لأمين المخزن داخل وحدة المخازن: طلبات تحميل سيارات المندوبين المنتظرة (كل التواريخ)
/// بالمطلوب والمتاح، والتجهيز يُخرج الكميات المجهَّزة للسيارة بمستند إسناد — دون الحاجة لصلاحية وحدة المندوبين.
/// </summary>
public class PrepareLoadOrdersSectionViewModel : SectionViewModel
{
    private RepLoadOrderRow? _selectedOrder;

    public PrepareLoadOrdersSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Warehouse, "طلبات التجهيز", Icons.Truck, "#2563EB",
               "طلبات تحميل سيارات المندوبين: المطلوب والمتاح في المخزن، والتجهيز يُخرج البضاعة للسيارة")
    {
        PrepareCommand = new AsyncRelayCommand(PrepareAsync);
        PrintCommand = new AsyncRelayCommand(p => p is RepLoadOrderRow r ? PrintAsync(db => LoadOrderTools.ReportAsync(Session, db, r.Id)) : Task.CompletedTask);
    }

    protected override bool ReloadOnActivate => true;
    protected override bool HasPendingInput => PrepareLines.Any(l => l.Prepared != l.Requested);

    protected override void ResetInput()
    {
        _selectedOrder = null;
        PrepareLines.Clear();
    }

    public ObservableCollection<RepLoadOrderRow> Orders { get; } = new();
    public ObservableCollection<PrepareLineDraft> PrepareLines { get; } = new();
    public int PendingCount => Orders.Count(o => o.Status == RepLoadOrderStatus.Pending);
    public bool CanPrepare => SelectedOrder?.Status == RepLoadOrderStatus.Pending;
    public RepLoadOrderRow? SelectedOrder
    {
        get => _selectedOrder;
        set { if (SetProperty(ref _selectedOrder, value)) { OnPropertyChanged(nameof(CanPrepare)); Background(LoadLinesAsync()); } }
    }
    public AsyncRelayCommand PrepareCommand { get; }
    public AsyncRelayCommand PrintCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var keep = SelectedOrder?.Id;
        var svc = new RepOperationsService(db);
        Orders.Clear();
        // المنتظر من أي تاريخ أولًا، ثم ما جُهّز اليوم
        foreach (var o in await svc.GetLoadOrdersAsync(DateTime.Today.AddYears(-1), DateTime.Today.AddDays(30), RepLoadOrderStatus.Pending)) Orders.Add(o);
        foreach (var o in (await svc.GetLoadOrdersAsync(DateTime.Today, DateTime.Today, RepLoadOrderStatus.Prepared))) Orders.Add(o);
        OnPropertyChanged(nameof(PendingCount));
        SelectedOrder = Orders.FirstOrDefault(o => o.Id == keep) ?? Orders.FirstOrDefault(o => o.Status == RepLoadOrderStatus.Pending);
        StatusMessage = PendingCount == 0 ? "لا طلبات بانتظار التجهيز" : $"بانتظار التجهيز: {PendingCount}";
    }

    private async Task LoadLinesAsync()
    {
        PrepareLines.Clear();
        if (SelectedOrder is null) return;
        await using var db = Session.NewDb();
        foreach (var l in await LoadOrderTools.PrepareLinesAsync(db, SelectedOrder.Id)) PrepareLines.Add(l);
    }

    private async Task PrepareAsync()
    {
        if (!Require(CanAdd, "تجهيز الحمولة")) return;
        if (SelectedOrder is not { Status: RepLoadOrderStatus.Pending } order) { Dialogs.Error("اختر طلبًا بانتظار التجهيز"); return; }
        if (PrepareLines.Any(l => l.Prepared < 0)) { Dialogs.Error("الكمية المجهَّزة لا تكون سالبة"); return; }
        if (PrepareLines.FirstOrDefault(l => l.IsShort) is { } shortLine)
        {
            Dialogs.Error($"المتاح من {shortLine.ItemName} لا يكفي ({shortLine.AvailableText}) — جهّز بالمتاح أو انتظر الإنتاج");
            return;
        }
        var less = PrepareLines.Count(l => l.Prepared < l.Requested);
        if (!Dialogs.Confirm($"تجهيز {order.OrderNumber} لـ {order.RepName}؟ تخرج الكميات من المخزن للسيارة الآن."
                             + (less > 0 ? $"\n{less} سطر مجهَّز بأقل من المطلوب." : "")))
            return;
        await using var db = Session.NewDb();
        StockDocument? doc = null;
        if (await RunOperationAsync(async () =>
            {
                var (r, d) = await new RepOperationsService(db).PrepareLoadOrderAsync(order.Id, PrepareLines.ToDictionary(l => l.LineId, l => l.Prepared), Session.UserId);
                doc = d;
                return r;
            }, $"جُهّزت الحمولة {order.OrderNumber}"))
        {
            PrepareLines.Clear();
            await LoadAsync();
            StatusMessage = $"جُهّزت {order.OrderNumber} — مستند الإسناد {doc!.DocumentNumber}";
        }
    }
}
