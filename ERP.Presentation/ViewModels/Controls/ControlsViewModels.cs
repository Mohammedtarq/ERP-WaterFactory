using System.Collections.ObjectModel;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.Controls;

// ============================ سجل الحركات ============================
/// <summary>من أضاف أو عدّل أو ألغى ماذا ومتى، مع القيم قبل وبعد. للعرض فقط (صلاحية خاصة "سجل الحركات").</summary>
public class AuditLogSectionViewModel : SectionViewModel
{
    private DateTime _from = DateTime.Today.AddDays(-7);
    private DateTime _to = DateTime.Today;
    private Option<int?>? _user;
    private Option<string?>? _table;
    private string _search = "";
    private AuditService.AuditRow? _selected;

    public AuditLogSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.SystemSettings, "سجل الحركات", Icons.Shield, "#0F766E", "من أضاف أو عدّل أو ألغى ماذا ومتى، والقيم قبل وبعد")
    {
        SearchCommand = new AsyncRelayCommand(LoadAsync);
        PrintCommand = new RelayCommand(Print);
    }

    public bool Allowed => Has(SpecialPermission.AuditLog);
    public DateTime From { get => _from; set => SetProperty(ref _from, value); }
    public DateTime To { get => _to; set => SetProperty(ref _to, value); }
    public ObservableCollection<Option<int?>> UserOptions { get; } = new();
    public ObservableCollection<Option<string?>> TableOptions { get; } = new();
    public Option<int?>? SelectedUser { get => _user; set => SetProperty(ref _user, value); }
    public Option<string?>? SelectedTable { get => _table; set => SetProperty(ref _table, value); }
    public string Search { get => _search; set => SetProperty(ref _search, value); }
    public ObservableCollection<AuditService.AuditRow> Rows { get; } = new();
    public AuditService.AuditRow? Selected { get => _selected; set { if (SetProperty(ref _selected, value)) OnPropertyChanged(nameof(SelectedChanges)); } }

    /// <summary>تفاصيل التغيير المختار: سطر لكل حقل "الحقل: قبل ← بعد".</summary>
    public IReadOnlyList<string> SelectedChanges => ChangeLines(Selected?.Changes);

    public AsyncRelayCommand SearchCommand { get; }
    public RelayCommand PrintCommand { get; }

    public static IReadOnlyList<string> ChangeLines(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try
        {
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement[]>>(json);
            if (dict is null) return Array.Empty<string>();
            static string V(System.Text.Json.JsonElement e) => e.ValueKind == System.Text.Json.JsonValueKind.Null ? "—" : e.ToString();
            return dict.Select(kv => kv.Value.Length >= 2 ? $"{kv.Key}: {V(kv.Value[0])} ← {V(kv.Value[1])}" : $"{kv.Key}").ToList();
        }
        catch (System.Text.Json.JsonException) { return new[] { json }; }
    }

    public override async Task LoadAsync()
    {
        Rows.Clear();
        if (!Allowed) { StatusMessage = "سجل الحركات يحتاج صلاحية خاصة من المدير"; return; }
        await using var db = Session.NewDb();
        if (UserOptions.Count == 0)
        {
            UserOptions.Add(new Option<int?>(null, "كل المستخدمين"));
            foreach (var u in await db.Users.AsNoTracking().OrderBy(u => u.Username).ToListAsync()) UserOptions.Add(new Option<int?>(u.Id, u.Username));
            SelectedUser = UserOptions[0];
            TableOptions.Add(new Option<string?>(null, "كل الجداول"));
            foreach (var t in await db.AuditLogs.AsNoTracking().Select(a => a.TableName).Distinct().ToListAsync())
                TableOptions.Add(new Option<string?>(t, AuditService.TableTitle(t)));
            SelectedTable = TableOptions[0];
        }
        var rows = await new AuditService(db).QueryAsync(From, To, SelectedUser?.Value, SelectedTable?.Value, Search);
        foreach (var r in rows) Rows.Add(r);
        StatusMessage = $"{rows.Count} حركة";
    }

    private void Print()
    {
        var r = new ReportDocument { CompanyName = Session.ProjectName, Title = $"سجل الحركات — {From:yyyy/MM/dd} إلى {To:yyyy/MM/dd}", PrintedBy = Session.FullName };
        r.Columns.AddRange(new[] { "الوقت", "المستخدم", "العملية", "الجدول", "السجل", "الوصف", "التغييرات" });
        foreach (var x in Rows)
            r.Rows.Add(new[] { x.AtLocal.ToString("yyyy/MM/dd HH:mm"), x.User, x.ActionName, x.TableTitle, x.RecordId ?? "", x.Summary ?? "",
                               string.Join(" · ", ChangeLines(x.Changes)) });
        r.Total("عدد الحركات", Rows.Count.ToString());
        Dialogs.ShowReport(r);
    }
}

// ============================ إغلاق الشهر ============================
/// <summary>
/// قفل الفترة المحاسبية: بعد إغلاق الشهر لا تُضاف ولا تُعدَّل ولا تُلغى حركات بتاريخ داخله.
/// الفتح للمدير فقط مع سبب مكتوب، وكل إغلاق وفتح يبقى في السجل.
/// </summary>
public class PeriodLockSectionViewModel : SectionViewModel
{
    private string _lockText = "";
    private int _year = DateTime.Today.AddMonths(-1).Year;
    private int _month = DateTime.Today.AddMonths(-1).Month;
    private string _reason = "";

    public PeriodLockSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.Finance, "إغلاق الشهر", Icons.Lock, "#0F766E", "قفل الفترة بعد طباعة الحسابات الختامية، والفتح للمدير فقط مع السبب")
    {
        CloseCommand = new AsyncRelayCommand(CloseAsync);
        ReopenCommand = new AsyncRelayCommand(ReopenAsync);
    }

    protected override bool ReloadOnActivate => true;

    public bool CanManage => Has(SpecialPermission.PeriodClose);
    public string LockText { get => _lockText; private set => SetProperty(ref _lockText, value); }
    public IReadOnlyList<int> Years { get; } = Enumerable.Range(DateTime.Today.Year - 3, 4).Reverse().ToList();
    public IReadOnlyList<Option<int>> Months { get; } = Enumerable.Range(1, 12).Select(m => new Option<int>(m, $"{m} — {ArabicMonths[m - 1]}")).ToList();
    public int Year { get => _year; set => SetProperty(ref _year, value); }
    public int Month { get => _month; set => SetProperty(ref _month, value); }
    public string Reason { get => _reason; set => SetProperty(ref _reason, value); }
    public ObservableCollection<PeriodLockService.LockHistoryRow> History { get; } = new();
    public AsyncRelayCommand CloseCommand { get; }
    public AsyncRelayCommand ReopenCommand { get; }

    private static readonly string[] ArabicMonths =
        { "كانون الثاني", "شباط", "آذار", "نيسان", "أيار", "حزيران", "تموز", "آب", "أيلول", "تشرين الأول", "تشرين الثاني", "كانون الأول" };

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var svc = new PeriodLockService(db);
        var locked = await svc.GetLockedThroughAsync();
        LockText = locked is DateTime d ? $"🔒 الفترة مقفلة حتى {d:yyyy/MM/dd} — لا إضافة ولا تعديل ولا إلغاء بتاريخ قبله أو فيه"
                                        : "🔓 لا توجد فترة مقفلة بعد";
        History.Clear();
        foreach (var h in await svc.HistoryAsync()) History.Add(h);
    }

    private async Task CloseAsync()
    {
        if (!CanManage) { Dialogs.Error("إغلاق الشهر يحتاج صلاحية خاصة من المدير"); return; }
        if (!Dialogs.Confirm($"إغلاق شهر {Month}/{Year}؟ بعده لا يمكن إضافة أو تعديل أي حركة بتاريخ داخله حتى يفتحه المدير.")) return;
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new PeriodLockService(db).CloseMonthAsync(Year, Month, Session.UserId), $"أُغلق شهر {Month}/{Year}"))
            await LoadAsync();
    }

    private async Task ReopenAsync()
    {
        if (!CanManage) { Dialogs.Error("فتح الشهر المقفل للمدير فقط"); return; }
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new PeriodLockService(db).ReopenFromMonthAsync(Year, Month, Reason, Session.UserId), $"فُتح شهر {Month}/{Year} للتعديل"))
        {
            Reason = "";
            await LoadAsync();
        }
    }
}
