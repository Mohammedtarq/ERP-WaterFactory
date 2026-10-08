using System.Collections.ObjectModel;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.Production;

// ============================ الماكينات ============================
public class MachinesSectionViewModel : CrudSectionViewModel<Machine>
{
    public MachinesSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Production, "الماكينات", Icons.Factory, "#64748B", "ماكينات الإنتاج: الاسم والنوع والخط — لكل ماكينة رصيد تحت تصنيع خاص بها") { }

    protected override int GetId(Machine e) => e.Id;
    protected override string Describe(Machine e) => e.Name;
    protected override bool Matches(Machine e, string text) =>
        e.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || e.MachineType.Contains(text, StringComparison.OrdinalIgnoreCase)
        || (e.ProductionLine?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false);

    protected override Task<List<Machine>> QueryAsync(ProjectDbContext db) =>
        db.Machines.AsNoTracking().OrderBy(m => m.Name).ToListAsync();

    protected override string? Validate(Machine e)
    {
        if (string.IsNullOrWhiteSpace(e.Name)) return "أدخل اسم الماكينة";
        if (string.IsNullOrWhiteSpace(e.MachineType)) return "أدخل نوع الماكينة (نفخ، تعبئة، تغليف...)";
        return null;
    }

    protected override async Task BeforeSaveAsync(ProjectDbContext db, Machine e)
    {
        e.Name = e.Name.Trim();
        e.MachineType = e.MachineType.Trim();
        e.ProductionLine = string.IsNullOrWhiteSpace(e.ProductionLine) ? null : e.ProductionLine.Trim();
        // مخزن تحت التصنيع: يُنشأ مع الماكينة الجديدة ويتبع اسمها
        var error = await MachineService.EnsureWipWarehouseAsync(db, e);
        if (error is not null) throw new InvalidOperationException(error);
    }
}

// ============================ تحت التصنيع لكل ماكينة ============================
public class MachineWipSectionViewModel : SectionViewModel
{
    private Machine? _machine;
    private DateTime _from = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _to = DateTime.Today;
    private Machine? _actionMachine;
    private MachineWipBalance? _actionItem;
    private decimal _actionQuantity;
    private ProductionOrderRow? _actionOrder;
    private Data.ProjectDb.Entities.Warehouse? _returnWarehouse;
    private string _alertText = "";

    public MachineWipSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Production, "تحت التصنيع", Icons.Layers, "#F97316",
               "لكل ماكينة: المصروف، المستهلك فعليًا، التالف، المتبقي، والمرحّل من الفترة السابقة — مع المطابقة")
    {
        LoadReportCommand = new AsyncRelayCommand(LoadAsync);
        AllMachinesCommand = new RelayCommand(() => Machine = null);
        DamageCommand = new AsyncRelayCommand(DamageAsync);
        ReturnCommand = new AsyncRelayCommand(ReturnAsync);
        IssueMoreCommand = new AsyncRelayCommand(IssueMoreAsync);
        PrintCommand = new RelayCommand(() => Dialogs.ShowReport(BuildReport()));
        AdjustCommand = new AsyncRelayCommand(AdjustAsync);
        PrintAdjustmentsCommand = new RelayCommand(() => Dialogs.ShowReport(BuildAdjustmentsReport()));
        _adjustKind = AdjustKindOptions[0];
    }

    // ---------------- تعديل المشرف (المتبقي / التالف) مع سجل التدقيق ----------------
    private bool _isSupervisor;
    private Option<WipAdjustmentKind> _adjustKind;
    private decimal? _adjustNewQuantity;
    private string _adjustReason = "";
    public bool IsSupervisor { get => _isSupervisor; private set => SetProperty(ref _isSupervisor, value); }
    public IReadOnlyList<Option<WipAdjustmentKind>> AdjustKindOptions { get; } = ArabicLabels.OptionsOf<WipAdjustmentKind>();
    public Option<WipAdjustmentKind> AdjustKind { get => _adjustKind; set => SetProperty(ref _adjustKind, value); }
    /// <summary>العدد الصحيح الجديد (المتبقي الفعلي بعد الجرد، أو إجمالي التالف الصحيح).</summary>
    public decimal? AdjustNewQuantity { get => _adjustNewQuantity; set => SetProperty(ref _adjustNewQuantity, value); }
    public string AdjustReason { get => _adjustReason; set => SetProperty(ref _adjustReason, value); }
    public ObservableCollection<WipAdjustment> Adjustments { get; } = new();
    public AsyncRelayCommand AdjustCommand { get; }
    public RelayCommand PrintAdjustmentsCommand { get; }

    private async Task AdjustAsync()
    {
        if (!IsSupervisor) { Dialogs.Error("تعديل الأعداد للمشرف أو الأدمن فقط"); return; }
        if (ActionMachine is null || ActionItem is null) { Dialogs.Error("اختر الماكينة والمادة"); return; }
        if (AdjustNewQuantity is null) { Dialogs.Error("أدخل العدد الصحيح الجديد"); return; }
        var (machine, item, qty, reason) = (ActionMachine, ActionItem, AdjustNewQuantity.Value, AdjustReason);
        var damaged = AdjustKind.Value == WipAdjustmentKind.Damaged;
        await using var db = Session.NewDb();
        var service = new MachineService(db);
        if (await RunOperationAsync(() => damaged
                ? service.AdjustDamagedAsync(machine.Id, item.ItemId, ActionOrder?.Id, qty, reason, Session.UserId)
                : service.AdjustRemainingAsync(machine.Id, item.ItemId, qty, reason, Session.UserId),
                $"عُدّل {AdjustKind.Label} لـ {item.ItemName} على {machine.Name} إلى {qty:N0} — سُجّل في سجل التدقيق"))
        {
            AdjustNewQuantity = null;
            AdjustReason = "";
            await LoadAsync();
        }
    }

    public ReportDocument BuildAdjustmentsReport()
    {
        var r = new ReportDocument { Key = "wip-adjustments", CompanyName = Session.ProjectName, Title = "سجل تعديلات المشرف — تحت التصنيع", PrintedBy = Session.FullName };
        r.Field("الماكينة", Machine?.Name ?? "كل الماكينات");
        r.Columns.AddRange(new[] { "التاريخ", "الماكينة", "المادة", "النوع", "الأمر", "قبل", "بعد", "الفرق", "السبب", "المستخدم" });
        foreach (var a in Adjustments)
            r.Rows.Add(new[] { a.ChangedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm"), a.Machine.Name, a.Item.ItemName, ArabicLabels.Of(a.Kind),
                               a.ProductionOrder?.MONumber ?? "", $"{a.BeforeQuantity:N0}", $"{a.AfterQuantity:N0}", $"{a.AfterQuantity - a.BeforeQuantity:+#,0;-#,0;0}",
                               a.Reason, a.ChangedByUser.Username });
        r.Total("عدد التعديلات", Adjustments.Count.ToString(), true);
        r.Signatures.AddRange(new[] { "المشرف", "المدقق" });
        return r;
    }

    protected override bool ReloadOnActivate => true;
    protected override bool HasPendingInput => ActionQuantity != 0 || AdjustNewQuantity is not null;

    protected override void ResetInput()
    {
        ActionItem = null;
        ActionQuantity = 0;
        ActionOrder = null;
        AdjustNewQuantity = null;
        AdjustReason = "";
    }

    public ObservableCollection<Machine> Machines { get; } = new();
    public ObservableCollection<MachineWipRow> Rows { get; } = new();
    public ObservableCollection<MachineWipBalance> ActionItems { get; } = new();
    public ObservableCollection<ProductionOrderRow> ActiveOrders { get; } = new();
    public ObservableCollection<Data.ProjectDb.Entities.Warehouse> RawWarehouses { get; } = new();

    public Machine? Machine { get => _machine; set { if (SetProperty(ref _machine, value)) Background(LoadAsync()); } }
    public DateTime From { get => _from; set => SetProperty(ref _from, value); }
    public DateTime To { get => _to; set => SetProperty(ref _to, value); }
    public string AlertText { get => _alertText; private set { if (SetProperty(ref _alertText, value)) OnPropertyChanged(nameof(HasAlert)); } }
    public bool HasAlert => AlertText.Length > 0;

    public Machine? ActionMachine { get => _actionMachine; set { if (SetProperty(ref _actionMachine, value)) Background(LoadActionItemsAsync()); } }
    public MachineWipBalance? ActionItem { get => _actionItem; set => SetProperty(ref _actionItem, value); }
    public decimal ActionQuantity { get => _actionQuantity; set => SetProperty(ref _actionQuantity, value); }
    /// <summary>أمر الإنتاج المرتبط بالتالف (اختياري) أو المطلوب له صرف إضافي.</summary>
    public ProductionOrderRow? ActionOrder { get => _actionOrder; set => SetProperty(ref _actionOrder, value); }
    public Data.ProjectDb.Entities.Warehouse? ReturnWarehouse { get => _returnWarehouse; set => SetProperty(ref _returnWarehouse, value); }

    public AsyncRelayCommand LoadReportCommand { get; }
    public RelayCommand AllMachinesCommand { get; }
    public AsyncRelayCommand DamageCommand { get; }
    public AsyncRelayCommand ReturnCommand { get; }
    public AsyncRelayCommand IssueMoreCommand { get; }
    public RelayCommand PrintCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var machineId = Machine?.Id;
        var actionId = ActionMachine?.Id;
        Machines.Clear();
        foreach (var m in await new MachineService(db).GetAllAsync()) Machines.Add(m);
        _machine = Machines.FirstOrDefault(m => m.Id == machineId);
        OnPropertyChanged(nameof(Machine));
        _actionMachine = Machines.FirstOrDefault(m => m.Id == actionId) ?? Machines.FirstOrDefault(m => m.IsActive);
        OnPropertyChanged(nameof(ActionMachine));

        if (RawWarehouses.Count == 0)
        {
            foreach (var w in await new MaterialAvailabilityService(db).SourceWarehousesAsync()) RawWarehouses.Add(w);
            ReturnWarehouse = RawWarehouses.FirstOrDefault();
        }
        var orderId = ActionOrder?.Id;
        ActiveOrders.Clear();
        foreach (var o in (await new ProductionService(db).GetOrdersAsync()).Where(o => o.Status == ProductionOrderStatus.InProgress && o.MachineName != null))
            ActiveOrders.Add(o);
        _actionOrder = ActiveOrders.FirstOrDefault(o => o.Id == orderId);
        OnPropertyChanged(nameof(ActionOrder));

        IsSupervisor = await new MachineService(db).IsSupervisorAsync(Session.UserId);
        Adjustments.Clear();
        foreach (var a in await new MachineService(db).GetAdjustmentsAsync(Machine?.Id)) Adjustments.Add(a);
        Rows.Clear();
        foreach (var r in await new MachineService(db).GetWipSummaryAsync(From, To, Machine?.Id)) Rows.Add(r);
        var bad = Rows.Where(r => !r.IsReconciled).ToList();
        AlertText = bad.Count == 0 ? ""
            : "تنبيه مطابقة: المصروف لا يساوي المستهلك + التالف + المتبقي في: "
              + string.Join("، ", bad.Select(r => $"{r.MachineName} / {r.RawItemName} (فرق {r.Difference:N0})"));
        StatusMessage = Rows.Count == 0 ? "لا حركة تحت التصنيع في الفترة" : $"{Rows.Count} سطر — المتبقي الكلي {Rows.Sum(r => r.Remaining):N0}";
        await LoadActionItemsAsync();
    }

    private async Task LoadActionItemsAsync()
    {
        var itemId = ActionItem?.ItemId;
        ActionItems.Clear();
        if (ActionMachine is null) return;
        await using var db = Session.NewDb();
        var balances = await new MachineService(db).BalancesAsync(ActionMachine.Id, includeZero: true);
        // مكونات الأوامر القائمة على الماكينة تظهر أيضًا (للصرف الإضافي) حتى لو كان رصيدها صفرًا
        var orderItems = await db.ProductionOrderConsumptions.Where(c => c.ProductionOrder.MachineId == ActionMachine.Id
                                                                       && c.ProductionOrder.Status == ProductionOrderStatus.InProgress)
                                 .Select(c => c.RawMaterialItemId).Distinct().ToListAsync();
        var ids = balances.Keys.Union(orderItems).ToList();
        var names = await db.Items.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.ItemName);
        foreach (var id in ids.OrderBy(i => names.GetValueOrDefault(i)))
            ActionItems.Add(new MachineWipBalance(id, names.GetValueOrDefault(id, ""), balances.GetValueOrDefault(id)));
        _actionItem = ActionItems.FirstOrDefault(i => i.ItemId == itemId) ?? ActionItems.FirstOrDefault();
        OnPropertyChanged(nameof(ActionItem));
    }

    private bool ValidateAction()
    {
        if (ActionMachine is null || ActionItem is null) { Dialogs.Error("اختر الماكينة والمادة"); return false; }
        if (ActionQuantity <= 0) { Dialogs.Error("أدخل كمية أكبر من صفر"); return false; }
        return true;
    }

    private async Task DamageAsync()
    {
        if (!Require(CanAdd || CanEdit, "تسجيل تالف الإنتاج") || !ValidateAction()) return;
        if (ActionOrder is not null && ActionOrder.MachineName != ActionMachine!.Name) { Dialogs.Error("أمر الإنتاج المختار يعمل على ماكينة أخرى"); return; }
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new MachineService(db).RecordDamageAsync(ActionMachine!.Id, ActionItem!.ItemId, ActionQuantity, ActionOrder?.Id, Session.UserId),
                                    $"سُجّل تالف {ActionQuantity:N0} من {ActionItem!.ItemName} على {ActionMachine!.Name}"))
        {
            ActionQuantity = 0;
            await LoadAsync();
        }
    }

    private async Task ReturnAsync()
    {
        if (!Require(CanAdd || CanEdit, "إرجاع المتبقي للمخزن") || !ValidateAction()) return;
        if (ReturnWarehouse is null) { Dialogs.Error("اختر مخزن المواد الأولية المستلم"); return; }
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new MachineService(db).ReturnToWarehouseAsync(ActionMachine!.Id, ActionItem!.ItemId, ActionQuantity, ReturnWarehouse.Id, Session.UserId),
                                    $"أُرجع {ActionQuantity:N0} من {ActionItem!.ItemName} إلى {ReturnWarehouse.Name}"))
        {
            ActionQuantity = 0;
            await LoadAsync();
        }
    }

    private async Task IssueMoreAsync()
    {
        if (!Require(CanEdit, "الصرف الإضافي لأمر إنتاج") || !ValidateAction()) return;
        if (ActionOrder is null) { Dialogs.Error("اختر أمر الإنتاج قيد التشغيل المطلوب الصرف له"); return; }
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new ProductionService(db).IssueAdditionalAsync(ActionOrder.Id, ActionItem!.ItemId, ActionQuantity, Session.UserId),
                                    $"صُرف {ActionQuantity:N0} من {ActionItem!.ItemName} إضافيًا لـ {ActionOrder.MONumber}"))
        {
            ActionQuantity = 0;
            await LoadAsync();
        }
    }

    public ReportDocument BuildReport()
    {
        var r = new ReportDocument { Key = "machine-wip", CompanyName = Session.ProjectName, Title = "تقرير تحت التصنيع حسب الماكينة", PrintedBy = Session.FullName };
        r.Field("الماكينة", Machine?.Name ?? "كل الماكينات").Field("الفترة", $"{From:yyyy/MM/dd} — {To:yyyy/MM/dd}");
        r.Columns.AddRange(new[] { "الماكينة", "النوع", "المادة الأولية", "المنتج", "المرحّل", "المصروف", "المستهلك فعليًا", "التالف", "المُرجَع", "تعديل مشرف", "المتبقي", "المطابقة" });
        foreach (var x in Rows)
            r.Rows.Add(new[] { x.MachineName, x.MachineType, x.RawItemName, x.ProductsText, $"{x.CarriedOver:N0}", $"{x.Issued:N0}", $"{x.Consumed:N0}",
                               $"{x.Damaged:N0}", $"{x.Returned:N0}", $"{x.Adjusted:+#,0;-#,0;0}", $"{x.Remaining:N0}", x.IsReconciled ? "مطابق" : $"فرق {x.Difference:N0}" });
        r.Total("إجمالي المصروف", $"{Rows.Sum(x => x.Issued):N0}")
         .Total("إجمالي المستهلك", $"{Rows.Sum(x => x.Consumed):N0}")
         .Total("إجمالي التالف", $"{Rows.Sum(x => x.Damaged):N0}")
         .Total("المتبقي تحت التصنيع", $"{Rows.Sum(x => x.Remaining):N0}", true);
        r.Signatures.AddRange(new[] { "مسؤول الإنتاج", "أمين مخزن المواد", "المدقق" });
        return r;
    }
}

public record MachineWipBalance(int ItemId, string ItemName, decimal Balance)
{
    public string Display => $"{ItemName} — المتبقي {Balance:N0}";
}
