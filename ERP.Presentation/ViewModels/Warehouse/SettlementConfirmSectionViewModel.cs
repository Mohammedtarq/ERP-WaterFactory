using System.Collections.ObjectModel;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;

namespace ERP.Presentation.ViewModels.Warehouse;

/// <summary>سطر عدّ مرتجع: المسجّل عند أمين الصندوق والمعدود فعلًا (قابل للتعديل).</summary>
public class CountedReturnLine : ObservableObject
{
    private decimal _counted;
    public CountedReturnLine(RepOperationsService.PendingReturnLine recorded) { Recorded = recorded; _counted = recorded.RecordedQuantity; }
    public RepOperationsService.PendingReturnLine Recorded { get; }
    public string ItemName => Recorded.ItemName;
    public string LevelName => Recorded.LevelName;
    public string Kind => Recorded.IsDamaged ? "تالف" : "سليم";
    public decimal RecordedQuantity => Recorded.RecordedQuantity;
    public decimal Counted { get => _counted; set { if (SetProperty(ref _counted, value)) OnPropertyChanged(nameof(Difference)); } }
    public decimal Difference => Counted - RecordedQuantity;
}

/// <summary>
/// «تأكيد تسويات المندوبين» لأمين المخزن: النقد رُحّل عند أمين الصندوق، وهنا يعدّ المرتجع والتالف فعلًا ويؤكد،
/// فيُرحَّل المرتجع والمجاني وفاتورة ما بقي في السيارة (إن طُلبت) في عملية واحدة.
/// </summary>
public class SettlementConfirmSectionViewModel : SectionViewModel
{
    private RepSettlementRow? _selected;
    private RepOperationsService.PendingSettlementDetail? _detail;

    public SettlementConfirmSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Warehouse, "تأكيد تسويات المندوبين", Icons.Truck, "#16A34A",
               "عدّ مرتجع المندوب وتأكيد الجانب المخزني من تسويته بعد أن استلم أمين الصندوق نقده")
    {
        ConfirmCommand = new AsyncRelayCommand(ConfirmAsync);
    }

    protected override bool ReloadOnActivate => true;
    protected override bool HasPendingInput => Lines.Any(l => l.Difference != 0);
    protected override void ResetInput() { _selected = null; _detail = null; Lines.Clear(); OnPropertyChanged(nameof(Detail)); }

    public ObservableCollection<RepSettlementRow> Pending { get; } = new();
    public ObservableCollection<CountedReturnLine> Lines { get; } = new();
    public bool CanConfirm => Has(SpecialPermission.SettlementConfirm) && Selected is not null;
    public RepOperationsService.PendingSettlementDetail? Detail { get => _detail; private set { if (SetProperty(ref _detail, value)) OnPropertyChanged(nameof(DetailText)); } }
    public string DetailText => Detail is null ? "اختر تسوية من القائمة"
        : $"المندوب: {Detail.RepName} · السيارة: {Detail.VanName} · {Detail.Date:yyyy/MM/dd}"
          + (Detail.FreeGoods.Count > 0 ? $"\nالمجاني: {string.Join("، ", Detail.FreeGoods)}" : "")
          + (Detail.InvoiceRemaining ? "\nسيُفوتر ما بقي في السيارة نقدًا بعد التأكيد (نقده يدخل محفظة المندوب)" : "")
          + (string.IsNullOrEmpty(Detail.Notes) ? "" : $"\nملاحظات: {Detail.Notes}");
    public RepSettlementRow? Selected
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value)) { OnPropertyChanged(nameof(CanConfirm)); Background(LoadDetailAsync()); } }
    }
    public AsyncRelayCommand ConfirmCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var keep = Selected?.Id;
        Pending.Clear();
        foreach (var r in await new RepOperationsService(db).PendingSettlementsAsync()) Pending.Add(r);
        Selected = Pending.FirstOrDefault(r => r.Id == keep) ?? Pending.FirstOrDefault();
        StatusMessage = Pending.Count == 0 ? "لا تسويات بانتظار التأكيد" : $"بانتظار تأكيد المخازن: {Pending.Count}"
            + (Has(SpecialPermission.SettlementConfirm) ? "" : $" — التأكيد يحتاج صلاحية «{SpecialPermission.NameOf(SpecialPermission.SettlementConfirm)}»");
    }

    private async Task LoadDetailAsync()
    {
        Lines.Clear();
        Detail = null;
        if (Selected is null) return;
        await using var db = Session.NewDb();
        Detail = await new RepOperationsService(db).GetPendingDetailAsync(Selected.Id);
        if (Detail is not null) foreach (var l in Detail.Returns) Lines.Add(new CountedReturnLine(l));
    }

    private async Task ConfirmAsync()
    {
        if (!Require(Has(SpecialPermission.SettlementConfirm), "تأكيد تسوية المندوب")) return;
        if (Selected is null || Detail is null) { Dialogs.Error("اختر تسوية بانتظار التأكيد"); return; }
        if (Lines.Any(l => l.Counted < 0)) { Dialogs.Error("الكمية المعدودة لا تكون سالبة"); return; }
        var diffs = Lines.Where(l => l.Difference != 0).ToList();
        var warn = diffs.Count == 0 ? "" : $"\n\nفرق العدّ: {string.Join("، ", diffs.Select(l => $"{l.ItemName} {l.Kind} ({l.Difference:+#,0.##;-#,0.##})"))} — يُسجَّل في ملاحظات التسوية، وما نقص يبقى في السيارة.";
        if (!Dialogs.Confirm($"تأكيد تسوية {Selected.SettlementNumber} للمندوب {Selected.RepName}؟ يُرحَّل المرتجع والمجاني.{warn}")) return;
        var counted = Lines.Where(l => l.Counted > 0)
            .Select(l => new StockDocumentLineInput(l.Recorded.ItemId, l.Recorded.PackagingLevelId, l.Counted, IsDamaged: l.Recorded.IsDamaged)).ToList();
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new RepOperationsService(db).ConfirmSettlementAsync(Selected.Id, counted, Session.UserId), $"أُكدت {Selected.SettlementNumber}"))
            await LoadAsync();
    }
}
