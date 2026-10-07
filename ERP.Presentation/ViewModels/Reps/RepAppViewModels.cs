using System.Collections.ObjectModel;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.Reps;

// ============================ طلبات المندوبين ============================
/// <summary>
/// «طلبات المندوبين»: ما وصل من هواتف المندوبين. بانتظار الاعتماد (المصروف ومرتجع الزبون): موافقة أو رفض بسبب؛
/// للاطلاع (الآجل والمجاني، رُحّلا تلقائيًا): «تمت المراجعة» بلا رفض؛ والكل للسجل. الاعتماد بالصلاحية الخاصة.
/// </summary>
public class RepRequestsSectionViewModel : SectionViewModel
{
    private Option<RepRequestFilter> _filter;
    private Option<int?>? _rep;
    private DateTime _from = DateTime.Today.AddDays(-7);
    private DateTime _to = DateTime.Today;
    private RepRequestRow? _selected;
    private string _rejectReason = "";

    public RepRequestsSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Reps, "طلبات المندوبين", Icons.Receive, "#7C3AED", "من هواتف المندوبين: اعتماد المصروف والمرتجع، ومراجعة الآجل والمجاني")
    {
        _filter = Filters[0];
        ApproveCommand = new AsyncRelayCommand(ApproveAsync);
        RejectCommand = new AsyncRelayCommand(RejectAsync);
        ReviewCommand = new AsyncRelayCommand(ReviewAsync);
        PhotoCommand = new AsyncRelayCommand(ShowPhotoAsync);
    }

    protected override bool ReloadOnActivate => true;

    public IReadOnlyList<Option<RepRequestFilter>> Filters { get; } = new[]
    {
        new Option<RepRequestFilter>(RepRequestFilter.Pending, "بانتظار الاعتماد"),
        new Option<RepRequestFilter>(RepRequestFilter.ToReview, "للاطلاع (آجل ومجاني)"),
        new Option<RepRequestFilter>(RepRequestFilter.All, "كل الحركات (بالتاريخ)"),
    };
    public Option<RepRequestFilter> Filter { get => _filter; set { if (SetProperty(ref _filter, value)) { OnPropertyChanged(nameof(IsHistory)); Background(LoadAsync()); } } }
    public bool IsHistory => Filter.Value == RepRequestFilter.All;
    public ObservableCollection<Option<int?>> RepOptions { get; } = new();
    public Option<int?>? Rep { get => _rep; set { if (SetProperty(ref _rep, value) && value is not null) Background(LoadAsync()); } }
    public DateTime From { get => _from; set => SetProperty(ref _from, value); }
    public DateTime To { get => _to; set => SetProperty(ref _to, value); }
    public ObservableCollection<RepRequestRow> Rows { get; } = new();
    public RepRequestRow? Selected { get => _selected; set { if (SetProperty(ref _selected, value)) RaiseSelection(); } }
    public string RejectReason { get => _rejectReason; set => SetProperty(ref _rejectReason, value); }
    public bool CanApprove => Has(SpecialPermission.RepApproval);
    public bool CanDecideSelected => CanApprove && Selected?.Status == RepRequestStatus.Pending;
    public bool CanReview => CanApprove && Rows.Any(r => r.NeedsReview && !r.Reviewed && r.Status == RepRequestStatus.Posted);
    public int PendingCount { get; private set; }
    public int ToReviewCount { get; private set; }
    public AsyncRelayCommand ApproveCommand { get; }
    public AsyncRelayCommand RejectCommand { get; }
    public AsyncRelayCommand ReviewCommand { get; }
    public AsyncRelayCommand PhotoCommand { get; }

    private void RaiseSelection()
    {
        OnPropertyChanged(nameof(CanDecideSelected));
        OnPropertyChanged(nameof(CanReview));
    }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        if (RepOptions.Count == 0)
        {
            RepOptions.Add(new Option<int?>(null, "كل المندوبين"));
            foreach (var e in await db.Employees.AsNoTracking().Where(e => e.IsSalesRep).OrderBy(e => e.FullName).ToListAsync())
                RepOptions.Add(new Option<int?>(e.Id, e.FullName));
            _rep = RepOptions[0];
            OnPropertyChanged(nameof(Rep));
        }
        var svc = new RepAppService(db);
        var keep = Selected?.Id;
        Rows.Clear();
        foreach (var r in await svc.ListAsync(Filter.Value, From, To, Rep?.Value)) Rows.Add(r);
        _selected = Rows.FirstOrDefault(r => r.Id == keep);
        OnPropertyChanged(nameof(Selected));
        PendingCount = await db.RepRequests.CountAsync(r => r.Status == RepRequestStatus.Pending);
        ToReviewCount = await db.RepRequests.CountAsync(r => r.Status == RepRequestStatus.Posted && r.NeedsReview && r.ReviewedAt == null);
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(ToReviewCount));
        RaiseSelection();
        StatusMessage = $"بانتظار الاعتماد: {PendingCount} — للاطلاع: {ToReviewCount}" + (CanApprove ? "" : " — الاعتماد يحتاج صلاحية «اعتماد طلبات المندوبين»");
    }

    private async Task ApproveAsync()
    {
        if (Selected is not { Status: RepRequestStatus.Pending } row) { Dialogs.Error("اختر طلبًا بانتظار الاعتماد"); return; }
        if (!Dialogs.Confirm($"اعتماد {row.KindText} للمندوب {row.RepName}: {row.Summary} — {row.Amount:N0} د.ع؟")) return;
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new RepAppService(db).ApproveAsync(row.Id, Session.UserId), $"اعتُمد {row.KindText}: {row.Summary}"))
            await LoadAsync();
    }

    private async Task RejectAsync()
    {
        if (Selected is not { Status: RepRequestStatus.Pending } row) { Dialogs.Error("اختر طلبًا بانتظار الاعتماد"); return; }
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new RepAppService(db).RejectAsync(row.Id, RejectReason, Session.UserId), $"رُفض {row.KindText}: {row.Summary}"))
        {
            RejectReason = "";
            await LoadAsync();
        }
    }

    /// <summary>«تمت المراجعة» للآجل والمجاني الظاهرة في الجدول (أو المختار فقط).</summary>
    private async Task ReviewAsync()
    {
        var ids = (Selected is { NeedsReview: true, Reviewed: false } one ? new[] { one } : Rows.Where(r => r.NeedsReview && !r.Reviewed && r.Status == RepRequestStatus.Posted))
                  .Select(r => r.Id).ToList();
        if (ids.Count == 0) { Dialogs.Error("لا توجد حركات آجل أو مجاني للمراجعة"); return; }
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new RepAppService(db).MarkReviewedAsync(ids, Session.UserId), $"تمت مراجعة {ids.Count} حركة"))
            await LoadAsync();
    }

    private async Task ShowPhotoAsync()
    {
        if (Selected is not { HasPhoto: true } row) { Dialogs.Error("الطلب المختار بلا صورة"); return; }
        await using var db = Session.NewDb();
        var bytes = await new RepAppService(db).PhotoAsync(row.Id);
        if (bytes is null) return;
        var path = Path.Combine(Path.GetTempPath(), $"erp-rep-request-{row.Id}.jpg");
        await File.WriteAllBytesAsync(path, bytes);
        Dialogs.OpenUrl(path);
    }
}

// ============================ أجهزة وإعدادات التطبيق ============================
/// <summary>هاتف مسجّل لكل مندوب بمفتاح سري يُعرض مرة واحدة، وإيقافه فورًا؛ وإعدادات التطبيق (تجاوز حد الدين، تنبيه النقد).</summary>
public class RepDevicesSectionViewModel : SectionViewModel
{
    private Option<int?>? _newRep;
    private string _newName = "";
    private string? _lastKey;
    private RepDeviceRow? _selected;
    private bool _allowOverLimit = true;
    private int _cashAlertDays = 2;

    public RepDevicesSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Reps, "أجهزة التطبيق", Icons.Lock, "#0F766E", "هاتف مسجّل لكل مندوب، وإيقافه فورًا، وإعدادات تطبيق المندوبين")
    {
        RegisterCommand = new AsyncRelayCommand(RegisterAsync);
        ToggleCommand = new AsyncRelayCommand(ToggleAsync);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync);
    }

    public ObservableCollection<RepDeviceRow> Devices { get; } = new();
    public ObservableCollection<Option<int?>> Reps { get; } = new();
    public Option<int?>? NewRep { get => _newRep; set => SetProperty(ref _newRep, value); }
    public string NewName { get => _newName; set => SetProperty(ref _newName, value); }
    /// <summary>المفتاح السري للجهاز الأخير (يُنسخ إلى الهاتف ولا يُعرض مرة أخرى).</summary>
    public string? LastKey { get => _lastKey; private set => SetProperty(ref _lastKey, value); }
    private byte[]? _linkQr;
    private string? _linkHint;
    /// <summary>رمز ربط الجهاز الأخير (عنوان الخادم + المفتاح): يصوّره المندوب من التطبيق. يُعرض مرة واحدة.</summary>
    public byte[]? LinkQr { get => _linkQr; private set { if (SetProperty(ref _linkQr, value)) OnPropertyChanged(nameof(HasLinkQr)); } }
    public bool HasLinkQr => LinkQr is not null;
    public string? LinkHint { get => _linkHint; private set => SetProperty(ref _linkHint, value); }
    public RepDeviceRow? Selected { get => _selected; set => SetProperty(ref _selected, value); }
    public bool AllowOverLimit { get => _allowOverLimit; set => SetProperty(ref _allowOverLimit, value); }
    public int CashAlertDays { get => _cashAlertDays; set => SetProperty(ref _cashAlertDays, value); }
    public AsyncRelayCommand RegisterCommand { get; }
    public AsyncRelayCommand ToggleCommand { get; }
    public AsyncRelayCommand SaveSettingsCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var svc = new RepAppService(db);
        if (Reps.Count == 0)
            foreach (var e in await db.Employees.AsNoTracking().Where(e => e.IsSalesRep).OrderBy(e => e.FullName).ToListAsync())
                Reps.Add(new Option<int?>(e.Id, e.FullName));
        Devices.Clear();
        foreach (var dvc in await svc.DevicesAsync()) Devices.Add(dvc);
        var settings = await svc.SettingsAsync();
        (AllowOverLimit, CashAlertDays) = (settings.AllowCreditOverLimit, settings.CashAlertDays);
        StatusMessage = $"{Devices.Count(x => x.IsActive)} جهاز مفعّل من {Devices.Count}";
    }

    private async Task RegisterAsync()
    {
        if (!Require(CanAdd, "تسجيل جهاز")) return;
        if (NewRep?.Value is not int repId) { Dialogs.Error("اختر المندوب"); return; }
        await using var db = Session.NewDb();
        string? key = null;
        if (await RunOperationAsync(async () =>
            {
                var (r, k) = await new RepAppService(db).RegisterDeviceAsync(repId, NewName, Session.UserId);
                key = k;
                return r;
            }, $"سُجّل الجهاز «{NewName.Trim()}» — انسخ المفتاح إلى الهاتف الآن، لن يُعرض مرة أخرى"))
        {
            LastKey = key;
            await using var db2 = Session.NewDb();
            var url = (await new CloudSyncService(db2).SettingsAsync()).ServerUrl;
            if (url is null)
            {
                LinkQr = null;
                LinkHint = "لعرض رمز الربط: اضبط عنوان الخادم في «الإعدادات ← المزامنة السحابية» ثم سجّل الجهاز";
            }
            else
            {
                using var qr = new QRCoder.QRCodeGenerator();
                using var data = qr.CreateQrCode(new Cloud.Contracts.LinkCode(url, key!).Encode(), QRCoder.QRCodeGenerator.ECCLevel.M);
                LinkQr = new QRCoder.PngByteQRCode(data).GetGraphic(8);
                LinkHint = $"افتح تطبيق المندوب ← «ربط الجهاز» ← صوّر الرمز. الخادم: {url}";
            }
            NewName = "";
            await LoadAsync();
        }
    }

    private async Task ToggleAsync()
    {
        if (!Require(CanEdit, "إيقاف جهاز أو تفعيله")) return;
        if (Selected is not { } row) { Dialogs.Error("اختر الجهاز من الجدول"); return; }
        if (row.IsActive && !Dialogs.Confirm($"إيقاف جهاز «{row.DeviceName}» للمندوب {row.RepName}؟ لن يستطيع الإرسال حتى يُفعَّل.")) return;
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new RepAppService(db).SetDeviceActiveAsync(row.Id, !row.IsActive, Session.UserId),
                                    row.IsActive ? $"أُوقف جهاز {row.DeviceName}" : $"فُعّل جهاز {row.DeviceName}"))
            await LoadAsync();
    }

    private async Task SaveSettingsAsync()
    {
        if (!Require(CanEdit, "تعديل إعدادات التطبيق")) return;
        await using var db = Session.NewDb();
        await RunOperationAsync(() => new RepAppService(db).SaveSettingsAsync(AllowOverLimit, CashAlertDays, Session.UserId), "حُفظت إعدادات تطبيق المندوبين");
    }
}
