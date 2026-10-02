using System.Collections.ObjectModel;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.ControlDb;
using ERP.Data.ControlDb.Entities;
using ERP.Data.Security;
using ERP.Data.Services;
using ERP.Data.Setup;
using Microsoft.Data.SqlClient;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.Settings;

public class SettingsModuleViewModel : ModuleViewModel
{
    public SettingsModuleViewModel(AppSession s, IDialogService d)
        : base("إعدادات النظام", Icons.Settings, ModuleColors.Settings)
    {
        Add(new RolesPermissionsSectionViewModel(s, d));
        Add(new UsersSectionViewModel(s, d));
        Add(new BranchesSectionViewModel(s, d));
        if (s.ControlConnectionString is not null) Add(new ProjectsSectionViewModel(s, d));
        Add(new BackupSectionViewModel(s, d));
    }
}

// ============================ الأدوار والصلاحيات ============================
public class PermissionRow : ObservableObject
{
    private bool _canView, _canAdd, _canEdit, _canDelete, _canPost;

    public string ModuleCode { get; init; } = "";
    public string ModuleName { get; init; } = "";
    public bool CanView { get => _canView; set => SetProperty(ref _canView, value); }
    public bool CanAdd { get => _canAdd; set => SetProperty(ref _canAdd, value); }
    public bool CanEdit { get => _canEdit; set => SetProperty(ref _canEdit, value); }
    public bool CanDelete { get => _canDelete; set => SetProperty(ref _canDelete, value); }
    public bool CanPost { get => _canPost; set => SetProperty(ref _canPost, value); }
}

public class RolesPermissionsSectionViewModel : SectionViewModel
{
    /// <summary>مصفوفة الصلاحيات تُحرَّر مباشرة: لا تُستبدل تحت المستخدم قبل الحفظ.</summary>
    protected override bool HasPendingInput => true;

    public static readonly IReadOnlyList<(string code, string name)> Modules = new[]
    {
        (ModuleCode.Dashboard, "لوحة المعلومات"), (ModuleCode.Warehouse, "المخازن"), (ModuleCode.Sales, "المبيعات"),
        (ModuleCode.Suppliers, "الموردون والمشتريات"), (ModuleCode.Finance, "المالية"), (ModuleCode.HR, "الموارد البشرية"),
        (ModuleCode.Reps, "المندوبون"), (ModuleCode.Production, "الإنتاج والمختبر"), (ModuleCode.SystemSettings, "إعدادات النظام"),
    };

    private Role? _selectedRole;
    private string _newRoleName = "";

    public RolesPermissionsSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.SystemSettings, "الأدوار والصلاحيات", Icons.Lock, "#64748B", "مصفوفة عرض/إضافة/تعديل/حذف/ترحيل لكل دور ووحدة")
    {
        AddRoleCommand = new AsyncRelayCommand(AddRoleAsync);
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        DeleteRoleCommand = new AsyncRelayCommand(p => p is Role r ? DeleteRoleAsync(r) : Task.CompletedTask);
        GrantAllCommand = new RelayCommand(() => SetAll(true));
        RevokeAllCommand = new RelayCommand(() => SetAll(false));
    }

    public ObservableCollection<Role> Roles { get; } = new();
    public ObservableCollection<PermissionRow> Matrix { get; } = new();

    public Role? SelectedRole { get => _selectedRole; set { if (SetProperty(ref _selectedRole, value)) LoadMatrix(); } }
    public string NewRoleName { get => _newRoleName; set => SetProperty(ref _newRoleName, value); }

    public AsyncRelayCommand AddRoleCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand DeleteRoleCommand { get; }
    public RelayCommand GrantAllCommand { get; }
    public RelayCommand RevokeAllCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var selectedId = SelectedRole?.Id;
        Roles.Clear();
        foreach (var r in await db.Roles.AsNoTracking().Include(r => r.Permissions).OrderBy(r => r.Name).ToListAsync()) Roles.Add(r);
        SelectedRole = Roles.FirstOrDefault(r => r.Id == selectedId) ?? Roles.FirstOrDefault();
        LoadMatrix();
    }

    private void LoadMatrix()
    {
        Matrix.Clear();
        if (SelectedRole is null) return;
        foreach (var (code, name) in Modules)
        {
            var p = SelectedRole.Permissions.FirstOrDefault(x => x.ModuleCode == code);
            Matrix.Add(new PermissionRow
            {
                ModuleCode = code, ModuleName = name,
                CanView = p?.CanView ?? false, CanAdd = p?.CanAdd ?? false, CanEdit = p?.CanEdit ?? false,
                CanDelete = p?.CanDelete ?? false, CanPost = p?.CanPost ?? false
            });
        }
    }

    private void SetAll(bool value)
    {
        foreach (var r in Matrix) r.CanView = r.CanAdd = r.CanEdit = r.CanDelete = r.CanPost = value;
    }

    private async Task AddRoleAsync()
    {
        if (!Require(CanAdd, "إضافة الأدوار")) return;
        var name = NewRoleName.Trim();
        if (name.Length == 0) { Dialogs.Error("أدخل اسم الدور"); return; }
        if (Roles.Any(r => r.Name == name)) { Dialogs.Error("يوجد دور بنفس الاسم"); return; }
        await using var db = Session.NewDb();
        var role = new Role { Name = name };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        NewRoleName = "";
        StatusMessage = $"أُضيف الدور \"{name}\" — حدّد صلاحياته ثم احفظ";
        await LoadAsync();
        SelectedRole = Roles.First(r => r.Id == role.Id);
    }

    private async Task SaveAsync()
    {
        if (!Require(CanEdit, "تعديل الصلاحيات")) return;
        if (SelectedRole is null) return;
        // أي صلاحية فعلية تستلزم صلاحية العرض، وإلا لن تظهر الوحدة أصلًا
        foreach (var r in Matrix.Where(r => r.CanAdd || r.CanEdit || r.CanDelete || r.CanPost)) r.CanView = true;

        await using var db = Session.NewDb();
        var existing = await db.RolePermissions.Where(p => p.RoleId == SelectedRole.Id).ToListAsync();
        foreach (var row in Matrix)
        {
            var p = existing.FirstOrDefault(x => x.ModuleCode == row.ModuleCode);
            if (p is null) db.RolePermissions.Add(p = new RolePermission { RoleId = SelectedRole.Id, ModuleCode = row.ModuleCode });
            (p.CanView, p.CanAdd, p.CanEdit, p.CanDelete, p.CanPost) = (row.CanView, row.CanAdd, row.CanEdit, row.CanDelete, row.CanPost);
        }
        await db.SaveChangesAsync();
        StatusMessage = $"تم حفظ صلاحيات \"{SelectedRole.Name}\" — تسري عند الدخول التالي للمستخدمين";
        await LoadAsync();
    }

    private async Task DeleteRoleAsync(Role role)
    {
        if (!Require(CanDelete, "حذف الأدوار")) return;
        await using var db = Session.NewDb();
        if (await db.Users.AnyAsync(u => u.RoleId == role.Id)) { Dialogs.Error("لا يمكن حذف دور مسند لمستخدمين"); return; }
        if (!Dialogs.Confirm($"حذف الدور \"{role.Name}\"؟")) return;
        await db.RolePermissions.Where(p => p.RoleId == role.Id).ExecuteDeleteAsync();
        await db.Roles.Where(r => r.Id == role.Id).ExecuteDeleteAsync();
        SelectedRole = null;
        await LoadAsync();
    }
}

// ============================ المستخدمون ============================
public class UsersSectionViewModel : CrudSectionViewModel<User>
{
    private string _newPassword = "";

    public UsersSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.SystemSettings, "المستخدمون", Icons.People, "#0EA5E9", "حسابات الدخول وأدوارها في هذا المشروع") { }

    public ObservableCollection<Role> Roles { get; } = new();
    public ObservableCollection<Employee> Employees { get; } = new();

    /// <summary>كلمة مرور جديدة (إلزامية للمستخدم الجديد، اختيارية عند التعديل).</summary>
    public string NewPassword { get => _newPassword; set => SetProperty(ref _newPassword, value); }

    protected override int GetId(User e) => e.Id;
    protected override string Describe(User e) => e.Username;

    protected override async Task LoadLookupsAsync(ProjectDbContext db)
    {
        Roles.Clear();
        foreach (var r in await db.Roles.AsNoTracking().OrderBy(r => r.Name).ToListAsync()) Roles.Add(r);
        Employees.Clear();
        foreach (var e in await db.Employees.AsNoTracking().Where(e => e.IsActive).OrderBy(e => e.FullName).ToListAsync()) Employees.Add(e);
    }

    protected override Task<List<User>> QueryAsync(ProjectDbContext db) =>
        db.Users.AsNoTracking().Include(u => u.Role).Include(u => u.Employee).OrderBy(u => u.Username).ToListAsync();

    protected override User CreateNew() => new() { RoleId = Roles.FirstOrDefault()?.Id ?? 0 };
    protected override void OnEditorChanged() => NewPassword = "";

    protected override string? Validate(User e)
    {
        if (string.IsNullOrWhiteSpace(e.Username)) return "أدخل اسم المستخدم";
        if (e.RoleId == 0) return "اختر الدور";
        if (e.Id == 0 && NewPassword.Length < 6) return "كلمة المرور يجب أن تكون 6 أحرف على الأقل";
        if (e.Id != 0 && NewPassword.Length is > 0 and < 6) return "كلمة المرور يجب أن تكون 6 أحرف على الأقل";
        if (e.Id == Session.UserId && !e.IsActive) return "لا يمكنك إيقاف حسابك الحالي";
        return null;
    }

    protected override Task BeforeSaveAsync(ProjectDbContext db, User e)
    {
        e.Username = e.Username.Trim();
        if (NewPassword.Length > 0) e.PasswordHash = PasswordHasher.Hash(NewPassword);
        return Task.CompletedTask;
    }

    /// <summary>
    /// الدخول يتم عبر قاعدة التحكم: كل مستخدم محلي يحصل تلقائيًا على حساب دخول موحّد بنفس الاسم
    /// وكلمة المرور، مربوط بهذا المشروع. اسم موجود مسبقًا (نفس الشخص في مشروع آخر) يُربط فقط.
    /// </summary>
    protected override async Task<string?> AfterSaveAsync(User e, bool isNew)
    {
        if (Session.ControlConnectionString is null || Session.ProjectId == 0) return null;
        await using var cdb = new ControlDbContext(new DbContextOptionsBuilder<ControlDbContext>().UseSqlServer(Session.ControlConnectionString).Options);
        var global = await cdb.GlobalUsers.FirstOrDefaultAsync(g => g.Username == e.Username);
        if (global is null)
        {
            if (NewPassword.Length == 0) return "حُفظ المستخدم، لكن لا يمكن إنشاء حساب دخول له بلا كلمة مرور";
            string fullName = e.Username;
            await using (var db = Session.NewDb())
                if (e.EmployeeId is int emp)
                    fullName = await db.Employees.Where(x => x.Id == emp).Select(x => x.FullName).FirstOrDefaultAsync() ?? fullName;
            cdb.GlobalUsers.Add(global = new GlobalUser { Username = e.Username, FullName = fullName, PasswordHash = PasswordHasher.Hash(NewPassword) });
            await cdb.SaveChangesAsync();
        }
        else if (NewPassword.Length > 0)
        {
            global.PasswordHash = PasswordHasher.Hash(NewPassword);   // تغيير كلمة المرور يسري على الدخول
        }

        var access = await cdb.UserProjectAccesses.FirstOrDefaultAsync(a => a.GlobalUserId == global.Id && a.ProjectId == Session.ProjectId);
        if (access is null) cdb.UserProjectAccesses.Add(new UserProjectAccess { GlobalUserId = global.Id, ProjectId = Session.ProjectId, LocalUserIdInProject = e.Id });
        else access.LocalUserIdInProject = e.Id;
        await cdb.SaveChangesAsync();
        return null;
    }
}

// ============================ الفروع ============================
public class BranchesSectionViewModel : CrudSectionViewModel<Branch>
{
    public BranchesSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.SystemSettings, "الفروع", Icons.Location, "#F59E0B", "فروع الشركة (كل مخزن يتبع فرعًا)") { }

    protected override int GetId(Branch e) => e.Id;
    protected override string Describe(Branch e) => e.Name;
    protected override Task<List<Branch>> QueryAsync(ProjectDbContext db) => db.Branches.AsNoTracking().OrderBy(b => b.Name).ToListAsync();
    protected override string? Validate(Branch e) => string.IsNullOrWhiteSpace(e.Name) ? "أدخل اسم الفرع" : null;
}

// ============================ المشاريع (الشركات) ============================
public class ProjectRow
{
    public int Id { get; init; }
    public string ProjectName { get; init; } = "";
    public string DatabaseName { get; init; } = "";
    public string ServerAddress { get; init; } = "";
    public bool IsActive { get; init; }
    public int UsersCount { get; init; }
    public bool IsCurrent { get; init; }
}

/// <summary>
/// كل مشروع/شركة قاعدة بيانات مستقلة. "مشروع جديد" يثبّت قاعدة كاملة بالإعدادات الأساسية
/// ويمنح المستخدم الحالي دور المدير فيها — جاهزة لشركة أخرى دون أي تعديل في النظام.
/// </summary>
public class ProjectsSectionViewModel : SectionViewModel
{
    private string _newProjectName = "";
    private string _newDatabaseName = "";
    private bool _demoData;

    public ProjectsSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.SystemSettings, "المشاريع والشركات", Icons.Store, "#0F766E", "إضافة شركة/مشروع جديد بقاعدة بيانات مستقلة")
    {
        CreateCommand = new AsyncRelayCommand(CreateAsync);
    }

    public ObservableCollection<ProjectRow> Projects { get; } = new();
    public ObservableCollection<string> Log { get; } = new();
    public string NewProjectName { get => _newProjectName; set => SetProperty(ref _newProjectName, value); }
    public string NewDatabaseName { get => _newDatabaseName; set => SetProperty(ref _newDatabaseName, value); }
    public bool DemoData { get => _demoData; set => SetProperty(ref _demoData, value); }
    public AsyncRelayCommand CreateCommand { get; }

    private ControlDbContext NewControlDb() =>
        new(new DbContextOptionsBuilder<ControlDbContext>().UseSqlServer(Session.ControlConnectionString!).Options);

    public override async Task LoadAsync()
    {
        await using var cdb = NewControlDb();
        var rows = await cdb.Projects.AsNoTracking().OrderBy(p => p.ProjectName)
            .Select(p => new ProjectRow { Id = p.Id, ProjectName = p.ProjectName, DatabaseName = p.DatabaseName, ServerAddress = p.ServerAddress,
                                          IsActive = p.IsActive, UsersCount = p.UserAccesses.Count(), IsCurrent = p.Id == Session.ProjectId })
            .ToListAsync();
        Projects.Clear();
        foreach (var r in rows) Projects.Add(r);
    }

    private async Task CreateAsync()
    {
        if (!Require(CanAdd, "إنشاء المشاريع")) return;
        if (string.IsNullOrWhiteSpace(NewProjectName) || string.IsNullOrWhiteSpace(NewDatabaseName))
        { Dialogs.Error("أدخل اسم المشروع واسم قاعدة بياناته"); return; }
        if (!Dialogs.Confirm($"إنشاء قاعدة بيانات جديدة \"{NewDatabaseName.Trim()}\" للمشروع \"{NewProjectName.Trim()}\"؟")) return;

        Log.Clear();
        IsBusy = true;
        try
        {
            // حساب الدخول الحالي موجود في قاعدة التحكم فيُربط؛ كلمة مرور المستخدم المحلي الجديد لا تُستخدم للدخول
            var result = await new ProvisioningService().InstallAsync(new InstallRequest(
                Session.ControlConnectionString!, NewProjectName, NewDatabaseName.Trim(), Session.FullName, Session.GlobalUsername,
                Guid.NewGuid().ToString("N"), DemoData, ExistingAdminPolicy.LinkWithoutPassword), new Progress<string>(m => Log.Add(m)));
            if (!result.Success) { Dialogs.Error(result.ErrorMessage!); return; }
            StatusMessage = $"أُنشئ المشروع \"{NewProjectName.Trim()}\". سجّل الخروج واختره من شاشة المشاريع.";
            NewProjectName = NewDatabaseName = "";
            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }
}

// ============================ النسخ الاحتياطي ============================
/// <summary>
/// نسخة كاملة لقاعدة المشروع وقاعدة التحكم بضغطة واحدة، مع سجل آخر النسخ وتنبيه إن تأخرت.
/// الملفات تُكتب على جهاز السيرفر (SQL Server هو من يكتبها).
/// </summary>
public class BackupSectionViewModel : SectionViewModel
{
    private string _folder = "";
    private string _lastBackupText = "";
    private bool _isOverdue;

    public BackupSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.SystemSettings, "النسخ الاحتياطي", Icons.Backup, "#0EA5E9", "نسخة كاملة لقواعد البيانات بضغطة واحدة وسجل النسخ السابقة")
    {
        BackupCommand = new AsyncRelayCommand(BackupAsync);
    }

    protected override bool ReloadOnActivate => true;

    public ObservableCollection<BackupHistoryRow> History { get; } = new();
    public ObservableCollection<string> Log { get; } = new();
    public string Folder { get => _folder; set => SetProperty(ref _folder, value); }
    public string LastBackupText { get => _lastBackupText; private set => SetProperty(ref _lastBackupText, value); }
    public bool IsOverdue { get => _isOverdue; private set => SetProperty(ref _isOverdue, value); }
    public AsyncRelayCommand BackupCommand { get; }

    public string ProjectDatabase => new SqlConnectionStringBuilder(Session.ConnectionString).InitialCatalog;
    public string? ControlDatabase => Session.ControlConnectionString is null ? null : new SqlConnectionStringBuilder(Session.ControlConnectionString).InitialCatalog;
    private IEnumerable<string> Databases => ControlDatabase is null ? new[] { ProjectDatabase } : new[] { ProjectDatabase, ControlDatabase };

    public override async Task LoadAsync()
    {
        var svc = new BackupService(Session.ConnectionString);
        if (string.IsNullOrWhiteSpace(Folder)) Folder = await svc.GetDefaultFolderAsync() ?? "";
        History.Clear();
        List<BackupHistoryRow> rows;
        try { rows = await svc.GetHistoryAsync(Databases); }
        catch (SqlException) { rows = new(); }   // لا صلاحية قراءة msdb: السجل يبقى فارغًا
        foreach (var r in rows) History.Add(r);

        var last = rows.Where(r => r.DatabaseName == ProjectDatabase).Select(r => (DateTime?)r.FinishedAt).FirstOrDefault();
        IsOverdue = last is null || (DateTime.Now - last.Value).TotalDays >= 1;
        LastBackupText = last is null
            ? "⚠ لا توجد أي نسخة احتياطية لقاعدة المشروع — خذ نسخة الآن"
            : IsOverdue ? $"⚠ آخر نسخة قبل {(int)(DateTime.Now - last.Value).TotalDays} يوم ({last:yyyy/MM/dd HH:mm}) — يُنصح بنسخة يومية"
                        : $"✓ آخر نسخة: {last:yyyy/MM/dd HH:mm}";
    }

    private async Task BackupAsync()
    {
        if (!Require(CanEdit, "النسخ الاحتياطي")) return;
        if (string.IsNullOrWhiteSpace(Folder)) { Dialogs.Error("حدد مجلد الحفظ على جهاز السيرفر"); return; }
        Log.Clear();
        IsBusy = true;
        try
        {
            var svc = new BackupService(Session.ConnectionString);
            foreach (var db in Databases)
            {
                Log.Add($"جاري نسخ {db}...");
                var path = await svc.BackupAsync(db, Folder);
                Log.Add($"✓ {path}");
            }
            StatusMessage = "اكتمل النسخ الاحتياطي. انسخ الملفات دوريًا إلى قرص خارجي أو جهاز آخر.";
            await LoadAsync();
        }
        catch (SqlException ex)
        {
            Log.Add("✗ " + ex.Message);
            Dialogs.Error("تعذّر النسخ الاحتياطي:\n" + ex.Message +
                          "\n\nتأكد أن المجلد موجود على جهاز السيرفر وأن لخدمة SQL Server صلاحية الكتابة فيه، وأن لحسابك صلاحية النسخ.");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
