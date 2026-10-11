using System.Collections.ObjectModel;
using System.Globalization;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.HR;

// ============================ أنواع الإجازات والعطل الرسمية ============================

/// <summary>سطر نوع إجازة قابل للتعديل في الجدول. الأيام فارغة = بلا حد.</summary>
public class LeaveTypeEditRow : ObservableObject
{
    private string _name = "";
    private string _days = "";
    private Option<LeaveAllowancePeriod> _period;
    private bool _isPaid = true, _isRegular, _isActive = true;

    public LeaveTypeEditRow(Option<LeaveAllowancePeriod> period) => _period = period;
    public int Id { get; init; }
    public string Name { get => _name; set => SetProperty(ref _name, value ?? ""); }
    public string Days { get => _days; set => SetProperty(ref _days, value ?? ""); }
    public Option<LeaveAllowancePeriod> Period { get => _period; set => SetProperty(ref _period, value); }
    public bool IsPaid { get => _isPaid; set => SetProperty(ref _isPaid, value); }
    public bool IsRegular { get => _isRegular; set => SetProperty(ref _isRegular, value); }
    public bool IsActive { get => _isActive; set => SetProperty(ref _isActive, value); }
}

/// <summary>
/// أنواع الإجازات بأيامها (شهريًا أو سنويًا، قابلة للتعديل)، والزمنيات المسموحة في الشهر، والعطل الرسمية
/// (لا يُسجَّل فيها غياب من البصمة).
/// </summary>
public class LeaveSettingsSectionViewModel : SectionViewModel
{
    private int _freeHourly = 2;
    private int _holidayYear = DateTime.Today.Year;
    private DateTime _newHolidayDate = DateTime.Today;
    private string _newHolidayName = "";

    public LeaveSettingsSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.HR, "أنواع الإجازات والعطل", Icons.Calendar, "#10B981", "أيام كل نوع (شهري أو سنوي)، والزمنيات المسموحة، والعطل الرسمية")
    {
        AddTypeCommand = new RelayCommand(() => Types.Add(new LeaveTypeEditRow(Periods[1])));
        SaveTypesCommand = new AsyncRelayCommand(SaveTypesAsync);
        AddHolidayCommand = new AsyncRelayCommand(AddHolidayAsync);
        DeleteHolidayCommand = new AsyncRelayCommand(p => p is PublicHoliday h ? DeleteHolidayAsync(h) : Task.CompletedTask);
    }

    public IReadOnlyList<Option<LeaveAllowancePeriod>> Periods { get; } = new[]
    {
        new Option<LeaveAllowancePeriod>(LeaveAllowancePeriod.Month, "في الشهر"), new Option<LeaveAllowancePeriod>(LeaveAllowancePeriod.Year, "في السنة")
    };
    public IReadOnlyList<int> Years { get; } = Enumerable.Range(DateTime.Today.Year - 1, 3).ToList();
    public ObservableCollection<LeaveTypeEditRow> Types { get; } = new();
    public ObservableCollection<PublicHoliday> Holidays { get; } = new();
    public int FreeHourlyPerMonth { get => _freeHourly; set => SetProperty(ref _freeHourly, value); }
    public int HolidayYear { get => _holidayYear; set { if (SetProperty(ref _holidayYear, value)) Background(LoadHolidaysAsync()); } }
    public DateTime NewHolidayDate { get => _newHolidayDate; set => SetProperty(ref _newHolidayDate, value); }
    public string NewHolidayName { get => _newHolidayName; set => SetProperty(ref _newHolidayName, value ?? ""); }

    public RelayCommand AddTypeCommand { get; }
    public AsyncRelayCommand SaveTypesCommand { get; }
    public AsyncRelayCommand AddHolidayCommand { get; }
    public AsyncRelayCommand DeleteHolidayCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var svc = new HrLeaveService(db);
        Types.Clear();
        foreach (var t in await svc.GetTypesAsync())
            Types.Add(new LeaveTypeEditRow(Periods.Single(p => p.Value == t.AllowancePeriod))
            {
                Id = t.Id, Name = t.Name, Days = t.AllowedDays?.ToString("0.##", CultureInfo.InvariantCulture) ?? "",
                IsPaid = t.IsPaid, IsRegular = t.IsRegular, IsActive = t.IsActive
            });
        FreeHourlyPerMonth = (await svc.GetPolicyAsync()).FreeHourlyLeavesPerMonth;
        await LoadHolidaysAsync();
    }

    private async Task LoadHolidaysAsync()
    {
        await using var db = Session.NewDb();
        Holidays.Clear();
        foreach (var h in await new HrLeaveService(db).GetHolidaysAsync(HolidayYear)) Holidays.Add(h);
    }

    private async Task SaveTypesAsync()
    {
        if (!Require(CanEdit, "تعديل أنواع الإجازات")) return;
        var list = new List<LeaveType>();
        foreach (var r in Types.Where(r => r.Id != 0 || !string.IsNullOrWhiteSpace(r.Name)))
        {
            decimal? days = null;
            if (!string.IsNullOrWhiteSpace(r.Days))
            {
                if (!decimal.TryParse(r.Days.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) || v < 0)
                { Dialogs.Error($"أيام «{r.Name}» غير صحيحة — اكتب رقمًا، أو اتركها فارغة لنوع بلا حد"); return; }
                days = v;
            }
            list.Add(new LeaveType { Id = r.Id, Name = r.Name, AllowedDays = days, AllowancePeriod = r.Period.Value, IsPaid = r.IsPaid, IsRegular = r.IsRegular, IsActive = r.IsActive });
        }
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new HrLeaveService(db).SaveTypesAsync(list, FreeHourlyPerMonth, Session.UserId), "حُفظت أنواع الإجازات"))
            await LoadAsync();
    }

    private async Task AddHolidayAsync()
    {
        if (!Require(CanAdd || CanEdit, "تسجيل العطل الرسمية")) return;
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new HrLeaveService(db).AddHolidayAsync(NewHolidayDate, NewHolidayName, Session.UserId),
                                    $"سُجّلت العطلة {NewHolidayName} ({NewHolidayDate:yyyy/MM/dd}) — لا غياب فيها"))
        {
            NewHolidayName = "";
            HolidayYear = NewHolidayDate.Year;
            await LoadHolidaysAsync();
        }
    }

    private async Task DeleteHolidayAsync(PublicHoliday h)
    {
        if (!Require(CanDelete || CanEdit, "حذف العطل الرسمية")) return;
        if (!Dialogs.Confirm($"حذف العطلة «{h.Name}» ({h.HolidayDate:yyyy/MM/dd})؟")) return;
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new HrLeaveService(db).DeleteHolidayAsync(h.Id, Session.UserId), "حُذفت العطلة"))
            await LoadHolidaysAsync();
    }
}

// ============================ الإجازات ============================

/// <summary>
/// تسجيل الإجازات: يومية (من تاريخ إلى تاريخ، أيام العمل فقط) أو زمنية (من ساعة إلى ساعة).
/// تظهر قبل الحفظ طريقة احتسابها (من الرصيد أو بدون راتب)، وتسري على الحضور والبصمة فورًا، وتُطبع إشعارًا.
/// </summary>
public class LeavesSectionViewModel : SectionViewModel
{
    private int _month = DateTime.Today.Month, _year = DateTime.Today.Year;
    public IReadOnlyList<int> Months { get; } = Enumerable.Range(1, 12).ToList();
    public IReadOnlyList<int> Years { get; } = Enumerable.Range(DateTime.Today.Year - 2, 4).ToList();
    /// <summary>شهر القائمة (الافتراضي الشهر الحالي).</summary>
    public int Month { get => _month; set { if (SetProperty(ref _month, value)) Background(LoadRowsAsync()); } }
    public int Year { get => _year; set { if (SetProperty(ref _year, value)) Background(LoadRowsAsync()); } }
    private Employee? _employee;
    private LeaveType? _type;
    private Option<LeaveKind> _kind;
    private DateTime _fromDate = DateTime.Today, _toDate = DateTime.Today;
    private string _fromTime = "08:00", _toTime = "10:00";
    private string? _note;
    private string _quoteText = "", _quoteWarning = "";
    private Employee? _filterEmployee;
    private LeaveRow? _voidTarget;
    private string? _voidReason;
    private string _hourlyText = "";
    private int _quoteToken;

    public LeavesSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.HR, "الإجازات", Icons.Calendar, "#10B981", "يومية أو زمنية، برصيد كل نوع — تسري على الحضور والبصمة")
    {
        _kind = Kinds[0];
        RegisterCommand = new AsyncRelayCommand(RegisterAsync);
        PrintCommand = new AsyncRelayCommand(p => p is LeaveRow r ? PrintAsync(db => DocumentReports.LeaveNoticeAsync(Session, db, r.Id)) : Task.CompletedTask);
        PrintBalanceCommand = new AsyncRelayCommand(() => Employee is null ? NoEmployee() : PrintAsync(db => DocumentReports.LeaveBalanceAsync(Session, db, Employee.Id, FromDate)));
        BeginVoidCommand = new RelayCommand(p =>
        {
            if (p is not LeaveRow r) return;
            if (r.IsVoided) { Dialogs.Error("ملغاة مسبقًا"); return; }
            VoidReason = null;
            VoidTarget = r;
        });
        CancelVoidCommand = new RelayCommand(() => VoidTarget = null);
        VoidCommand = new AsyncRelayCommand(VoidAsync);
    }

    private Task NoEmployee() { Dialogs.Error("اختر الموظف أولًا"); return Task.CompletedTask; }

    protected override bool ReloadOnActivate => true;
    protected override bool HasPendingInput => Employee is not null;

    protected override void ResetInput()
    {
        _employee = null;
        OnPropertyChanged(nameof(Employee));
        Kind = Kinds[0];
        FromDate = ToDate = DateTime.Today;
        Note = null;
        QuoteText = QuoteWarning = "";
        Balances.Clear();
        HourlyText = "";
        VoidTarget = null;
    }

    public IReadOnlyList<Option<LeaveKind>> Kinds { get; } = new[]
    {
        new Option<LeaveKind>(LeaveKind.Day, "يومية (من تاريخ إلى تاريخ)"), new Option<LeaveKind>(LeaveKind.Hourly, "زمنية (من ساعة إلى ساعة)")
    };
    public ObservableCollection<Employee> Employees { get; } = new();
    public ObservableCollection<LeaveType> Types { get; } = new();
    public ObservableCollection<LeaveRow> Rows { get; } = new();
    public ObservableCollection<LeaveBalanceRow> Balances { get; } = new();

    public Employee? Employee { get => _employee; set { if (SetProperty(ref _employee, value)) { Background(LoadBalancesAsync()); Requote(); } } }
    public LeaveType? Type { get => _type; set { if (SetProperty(ref _type, value)) Requote(); } }
    public Option<LeaveKind> Kind
    {
        get => _kind;
        set
        {
            if (!SetProperty(ref _kind, value)) return;
            OnPropertyChanged(nameof(IsHourly));
            OnPropertyChanged(nameof(IsDaily));
            Requote();
        }
    }
    public bool IsHourly => Kind.Value == LeaveKind.Hourly;
    public bool IsDaily => !IsHourly;
    public DateTime FromDate { get => _fromDate; set { if (SetProperty(ref _fromDate, value.Date)) { if (ToDate < FromDate) ToDate = FromDate; Background(LoadBalancesAsync()); Requote(); } } }
    public DateTime ToDate { get => _toDate; set { if (SetProperty(ref _toDate, value.Date)) Requote(); } }
    public string FromTime { get => _fromTime; set { if (SetProperty(ref _fromTime, value ?? "")) Requote(); } }
    public string ToTime { get => _toTime; set { if (SetProperty(ref _toTime, value ?? "")) Requote(); } }
    public string? Note { get => _note; set => SetProperty(ref _note, value); }
    public string QuoteText { get => _quoteText; private set => SetProperty(ref _quoteText, value); }
    public string QuoteWarning { get => _quoteWarning; private set { if (SetProperty(ref _quoteWarning, value)) OnPropertyChanged(nameof(HasQuoteWarning)); } }
    public bool HasQuoteWarning => QuoteWarning.Length > 0;
    public string HourlyText { get => _hourlyText; private set => SetProperty(ref _hourlyText, value); }

    public Employee? FilterEmployee { get => _filterEmployee; set { if (SetProperty(ref _filterEmployee, value)) Background(LoadRowsAsync()); } }
    public LeaveRow? VoidTarget { get => _voidTarget; private set { if (SetProperty(ref _voidTarget, value)) OnPropertyChanged(nameof(IsVoiding)); } }
    public bool IsVoiding => VoidTarget is not null;
    public string? VoidReason { get => _voidReason; set => SetProperty(ref _voidReason, value); }

    public AsyncRelayCommand RegisterCommand { get; }
    public AsyncRelayCommand PrintCommand { get; }
    public AsyncRelayCommand PrintBalanceCommand { get; }
    public RelayCommand BeginVoidCommand { get; }
    public RelayCommand CancelVoidCommand { get; }
    public AsyncRelayCommand VoidCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var (selected, filter, typeId) = (Employee?.Id, FilterEmployee?.Id, Type?.Id);
        Employees.Clear();
        foreach (var e in await db.Employees.AsNoTracking().Where(e => e.IsActive && !e.IsTemporary).OrderBy(e => e.FullName).ToListAsync()) Employees.Add(e);
        Types.Clear();
        foreach (var t in await new HrLeaveService(db).GetTypesAsync(activeOnly: true)) Types.Add(t);
        _employee = Employees.FirstOrDefault(e => e.Id == selected);
        _filterEmployee = Employees.FirstOrDefault(e => e.Id == filter);
        _type = Types.FirstOrDefault(t => t.Id == typeId) ?? Types.FirstOrDefault(t => t.IsRegular) ?? Types.FirstOrDefault();
        OnPropertyChanged(nameof(Employee));
        OnPropertyChanged(nameof(FilterEmployee));
        OnPropertyChanged(nameof(Type));
        await LoadRowsAsync();
        await LoadBalancesAsync();
    }

    private int _rowsLoad;

    /// <summary>تغيير الشهر والسنة معًا يطلب تحميلين: يُعتمد آخرهما فقط حتى لا تتكرر الصفوف.</summary>
    private async Task LoadRowsAsync()
    {
        var token = ++_rowsLoad;
        VoidTarget = null;
        await using var db = Session.NewDb();
        var from = new DateTime(Year, Month, 1);
        var rows = await new HrLeaveService(db).ListAsync(from, from.AddMonths(1).AddDays(-1), FilterEmployee?.Id);
        if (token != _rowsLoad) return;
        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
    }

    private async Task LoadBalancesAsync()
    {
        Balances.Clear();
        HourlyText = "";
        if (Employee is null) return;
        await using var db = Session.NewDb();
        var (rows, taken, free) = await new HrLeaveService(db).BalancesAsync(Employee.Id, FromDate);
        foreach (var r in rows) Balances.Add(r);
        HourlyText = $"الزمنيات في {FromDate:MM/yyyy}: {taken} من {free} مسموحة" + (taken >= free ? " — التالية تُحسب يومًا من الاعتيادية" : "");
    }

    private bool TryInput(out LeaveInput? input, bool showErrors)
    {
        input = null;
        if (Employee is null) { if (showErrors) Dialogs.Error("اختر الموظف"); return false; }
        if (IsDaily && Type is null) { if (showErrors) Dialogs.Error("اختر نوع الإجازة"); return false; }
        TimeSpan? ft = null, tt = null;
        if (IsHourly)
        {
            if (!TimeSpan.TryParse(FromTime.Trim(), CultureInfo.InvariantCulture, out var a) || !TimeSpan.TryParse(ToTime.Trim(), CultureInfo.InvariantCulture, out var b))
            { if (showErrors) Dialogs.Error("اكتب الوقت بصيغة 08:30"); return false; }
            (ft, tt) = (a, b);
        }
        var regularId = Types.FirstOrDefault(t => t.IsRegular)?.Id ?? 0;
        input = new LeaveInput(Employee.Id, IsHourly ? regularId : Type!.Id, Kind.Value, FromDate, IsHourly ? FromDate : ToDate, ft, tt, Note);
        return true;
    }

    /// <summary>معاينة الاحتساب عند كل تغيير (يُعتمد آخر طلب فقط).</summary>
    private void Requote() => Background(QuoteAsync());

    private async Task QuoteAsync()
    {
        var token = ++_quoteToken;
        if (!TryInput(out var input, showErrors: false)) { QuoteText = QuoteWarning = ""; return; }
        await using var db = Session.NewDb();
        var (r, quote) = await new HrLeaveService(db).QuoteAsync(input!);
        if (token != _quoteToken) return;
        QuoteText = r.Success ? quote!.Summary : r.ErrorMessage ?? "";
        QuoteWarning = r.Success ? quote!.Warning ?? "" : "";
    }

    private async Task RegisterAsync()
    {
        if (!Require(CanAdd, "تسجيل الإجازات")) return;
        if (!TryInput(out var input, showErrors: true)) return;
        await using var db = Session.NewDb();
        var (result, created, warning) = await new HrLeaveService(db).RegisterAsync(input!, Session.UserId);
        if (!result.Success) { Dialogs.Error(result.ErrorMessage ?? "تعذّر التسجيل"); return; }
        StatusMessage = $"سُجّلت {(IsHourly ? "الزمنية" : $"إجازة {Type!.Name}")} لـ {Employee!.FullName} ({string.Join("، ", created.Select(l => l.LeaveNumber))})"
                        + (warning is null ? "" : $" — تنبيه: {warning}");
        Note = null;
        await LoadRowsAsync();
        await LoadBalancesAsync();
        Requote();
        await PrintAsync(d => DocumentReports.LeaveNoticeAsync(Session, d, created[0].Id));
    }

    private async Task VoidAsync()
    {
        if (VoidTarget is null) return;
        if (!Require(CanDelete || CanEdit, "إلغاء الإجازات")) return;
        if (string.IsNullOrWhiteSpace(VoidReason)) { Dialogs.Error("اكتب سبب الإلغاء"); return; }
        var target = VoidTarget;
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new HrLeaveService(db).VoidAsync(target.Id, VoidReason!, Session.UserId),
                                    $"أُلغيت الإجازة {target.LeaveNumber} — أيامها الماضية بلا بصمة صارت غيابًا، أعد رفع البصمة إن لزم"))
        {
            await LoadRowsAsync();
            await LoadBalancesAsync();
        }
    }
}

// ============================ العمل الإضافي ============================

/// <summary>
/// العمل الإضافي (إدخال يدوي): يوم إضافي (الجمعة/العطل حين يقتضي الدوام) بالأجر اليومي، وساعات بسعر ساعة
/// تضبطه الإدارة للكل أو لموظف بعينه. يدخل كشف الراتب تلقائيًا.
/// </summary>
public class OvertimeSectionViewModel : SectionViewModel
{
    private int _month = DateTime.Today.Month, _year = DateTime.Today.Year;
    public IReadOnlyList<int> Months { get; } = Enumerable.Range(1, 12).ToList();
    public IReadOnlyList<int> Years { get; } = Enumerable.Range(DateTime.Today.Year - 2, 4).ToList();
    /// <summary>شهر القائمة (الافتراضي الشهر الحالي).</summary>
    public int Month { get => _month; set { if (SetProperty(ref _month, value)) Background(LoadRowsAsync()); } }
    public int Year { get => _year; set { if (SetProperty(ref _year, value)) Background(LoadRowsAsync()); } }
    private Employee? _employee;
    private Option<OvertimeKind> _kind;
    private DateTime _date = DateTime.Today;
    private decimal _quantity = 1;
    private string? _note;
    private decimal _globalRate;
    private decimal _rate;
    private bool _rateForAll = true;
    private Employee? _rateEmployee;
    private OvertimeRow? _voidTarget;
    private string? _voidReason;

    public OvertimeSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.HR, "العمل الإضافي", Icons.Clock, "#8B5CF6", "أيام إضافية بالأجر اليومي، وساعات بسعر تضبطه الإدارة للكل أو لموظف")
    {
        _kind = Kinds[0];
        AddCommand = new AsyncRelayCommand(AddAsync);
        ApplyRateCommand = new AsyncRelayCommand(ApplyRateAsync);
        ClearEmployeeRateCommand = new AsyncRelayCommand(ClearEmployeeRateAsync);
        BeginVoidCommand = new RelayCommand(p =>
        {
            if (p is not OvertimeRow r) return;
            if (r.IsVoided) { Dialogs.Error("ملغى مسبقًا"); return; }
            VoidReason = null;
            VoidTarget = r;
        });
        CancelVoidCommand = new RelayCommand(() => VoidTarget = null);
        VoidCommand = new AsyncRelayCommand(VoidAsync);
    }

    protected override bool ReloadOnActivate => true;

    protected override void ResetInput()
    {
        _employee = null;
        OnPropertyChanged(nameof(Employee));
        Kind = Kinds[0];
        Date = DateTime.Today;
        Quantity = 1;
        Note = null;
        VoidTarget = null;
    }

    public IReadOnlyList<Option<OvertimeKind>> Kinds { get; } = new[]
    {
        new Option<OvertimeKind>(OvertimeKind.Day, "يوم إضافي (بالأجر اليومي)"), new Option<OvertimeKind>(OvertimeKind.Hours, "ساعات إضافية (بسعر الساعة)")
    };
    public ObservableCollection<Employee> Employees { get; } = new();
    public ObservableCollection<OvertimeRow> Rows { get; } = new();
    public ObservableCollection<OvertimeSummaryRow> Summary { get; } = new();

    public Employee? Employee { get => _employee; set => SetProperty(ref _employee, value); }
    public Option<OvertimeKind> Kind
    {
        get => _kind;
        set
        {
            if (!SetProperty(ref _kind, value)) return;
            OnPropertyChanged(nameof(IsHours));
            OnPropertyChanged(nameof(QuantityLabel));
            Quantity = IsHours ? Math.Max(1, Quantity) : 1;
        }
    }
    public bool IsHours => Kind.Value == OvertimeKind.Hours;
    public string QuantityLabel => IsHours ? "عدد الساعات" : "عدد الأيام (يوم لكل تاريخ)";
    public DateTime Date { get => _date; set => SetProperty(ref _date, value.Date); }
    public decimal Quantity { get => _quantity; set => SetProperty(ref _quantity, value); }
    public string? Note { get => _note; set => SetProperty(ref _note, value); }

    public decimal GlobalRate { get => _globalRate; private set { if (SetProperty(ref _globalRate, value)) OnPropertyChanged(nameof(GlobalRateText)); } }
    public string GlobalRateText => GlobalRate == 0 ? "سعر الساعة العام: غير محدد" : $"سعر الساعة العام: {GlobalRate:N0} د.ع";
    public decimal Rate { get => _rate; set => SetProperty(ref _rate, value); }
    public bool RateForAll { get => _rateForAll; set { if (SetProperty(ref _rateForAll, value)) OnPropertyChanged(nameof(RateForEmployee)); } }
    public bool RateForEmployee { get => !_rateForAll; set => RateForAll = !value; }
    public Employee? RateEmployee { get => _rateEmployee; set => SetProperty(ref _rateEmployee, value); }
    public string MissingRateText => Summary.Where(s => s.MissingRate).Select(s => s.EmployeeName).ToList() is { Count: > 0 } m
        ? $"تنبيه: ساعات بلا سعر لـ {string.Join("، ", m)} — حدّد السعر قبل توليد الرواتب" : "";
    public bool HasMissingRate => MissingRateText.Length > 0;

    public OvertimeRow? VoidTarget { get => _voidTarget; private set { if (SetProperty(ref _voidTarget, value)) OnPropertyChanged(nameof(IsVoiding)); } }
    public bool IsVoiding => VoidTarget is not null;
    public string? VoidReason { get => _voidReason; set => SetProperty(ref _voidReason, value); }

    public AsyncRelayCommand AddCommand { get; }
    public AsyncRelayCommand ApplyRateCommand { get; }
    public AsyncRelayCommand ClearEmployeeRateCommand { get; }
    public RelayCommand BeginVoidCommand { get; }
    public RelayCommand CancelVoidCommand { get; }
    public AsyncRelayCommand VoidCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var (selected, rateEmp) = (Employee?.Id, RateEmployee?.Id);
        Employees.Clear();
        foreach (var e in await db.Employees.AsNoTracking().Where(e => e.IsActive && !e.IsTemporary).OrderBy(e => e.FullName).ToListAsync()) Employees.Add(e);
        _employee = Employees.FirstOrDefault(e => e.Id == selected);
        _rateEmployee = Employees.FirstOrDefault(e => e.Id == rateEmp);
        OnPropertyChanged(nameof(Employee));
        OnPropertyChanged(nameof(RateEmployee));
        GlobalRate = await new OvertimeService(db).GetGlobalRateAsync();
        if (Rate == 0) Rate = GlobalRate;
        await LoadRowsAsync();
    }

    private int _rowsLoad;

    /// <summary>يُعتمد آخر تحميل فقط (تغيير الشهر والسنة معًا يطلب تحميلين).</summary>
    private async Task LoadRowsAsync()
    {
        var token = ++_rowsLoad;
        VoidTarget = null;
        await using var db = Session.NewDb();
        var svc = new OvertimeService(db);
        var rows = await svc.ListAsync(Month, Year);
        var summary = await svc.SummaryAsync(Month, Year);
        if (token != _rowsLoad) return;
        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
        Summary.Clear();
        foreach (var r in summary) Summary.Add(r);
        OnPropertyChanged(nameof(MissingRateText));
        OnPropertyChanged(nameof(HasMissingRate));
    }

    private async Task AddAsync()
    {
        if (!Require(CanAdd, "تسجيل العمل الإضافي")) return;
        if (Employee is null) { Dialogs.Error("اختر الموظف"); return; }
        await using var db = Session.NewDb();
        var (name, kind, qty) = (Employee.FullName, Kind.Label, Quantity);
        if (await RunOperationAsync(() => new OvertimeService(db).AddAsync(Employee.Id, Date, Kind.Value, Quantity, Note, Session.UserId),
                                    $"سُجّل {kind} لـ {name}: {(IsHours ? $"{qty:0.##} ساعة" : "يوم")} بتاريخ {Date:yyyy/MM/dd}"))
        {
            Note = null;
            if (Date.Month != Month || Date.Year != Year)
            {
                (_month, _year) = (Date.Month, Date.Year);
                OnPropertyChanged(nameof(Month));
                OnPropertyChanged(nameof(Year));
            }
            await LoadRowsAsync();
        }
    }

    private async Task ApplyRateAsync()
    {
        if (!Require(CanEdit, "ضبط سعر الساعة الإضافية")) return;
        if (RateForEmployee && RateEmployee is null) { Dialogs.Error("اختر الموظف الذي يُخصَّص له السعر"); return; }
        if (RateForAll && !Dialogs.Confirm($"اعتماد {Rate:N0} د.ع لساعة العمل الإضافي للكل؟ تُزال الأسعار الخاصة بالموظفين.")) return;
        await using var db = Session.NewDb();
        var target = RateForAll ? "للكل" : $"لـ {RateEmployee!.FullName}";
        if (await RunOperationAsync(() => new OvertimeService(db).SetRateAsync(RateForAll ? null : RateEmployee!.Id, Rate, Session.UserId),
                                    $"سعر الساعة الإضافية {target}: {Rate:N0} د.ع — أعد توليد الرواتب إن كانت مولّدة"))
            await LoadAsync();
    }

    private async Task ClearEmployeeRateAsync()
    {
        if (!Require(CanEdit, "ضبط سعر الساعة الإضافية")) return;
        if (RateEmployee is null) { Dialogs.Error("اختر الموظف"); return; }
        await using var db = Session.NewDb();
        var name = RateEmployee.FullName;
        if (await RunOperationAsync(() => new OvertimeService(db).SetRateAsync(RateEmployee.Id, null, Session.UserId), $"{name} يعود للسعر العام"))
            await LoadAsync();
    }

    private async Task VoidAsync()
    {
        if (VoidTarget is null) return;
        if (!Require(CanDelete || CanEdit, "إلغاء العمل الإضافي")) return;
        if (string.IsNullOrWhiteSpace(VoidReason)) { Dialogs.Error("اكتب سبب الإلغاء"); return; }
        var target = VoidTarget;
        await using var db = Session.NewDb();
        if (await RunOperationAsync(() => new OvertimeService(db).VoidAsync(target.Id, VoidReason!, Session.UserId), "أُلغي سطر العمل الإضافي"))
            await LoadRowsAsync();
    }
}
