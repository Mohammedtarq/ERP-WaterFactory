using System.Collections.ObjectModel;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Security;
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
        Add(new EmployeesSectionViewModel(s, d));
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
        : base(s, d, ModuleCode.SystemSettings, "المستخدمون", Icons.People, "#0EA5E9", "مستخدمو هذا المشروع وأدوارهم") { }

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

// ============================ الموظفون (البيانات الأساسية) ============================
public class EmployeesSectionViewModel : CrudSectionViewModel<Employee>
{
    public EmployeesSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.SystemSettings, "الموظفون", Icons.HR, "#EC4899", "البيانات الأساسية وعلامة المندوب/مدير المبيعات") { }

    public ObservableCollection<Branch> Branches { get; } = new();
    public IReadOnlyList<SalaryCurrency> Currencies { get; } = Enum.GetValues<SalaryCurrency>();

    protected override int GetId(Employee e) => e.Id;
    protected override string Describe(Employee e) => e.FullName;
    protected override bool Matches(Employee e, string t) => base.Matches(e, t) || (e.JobTitle?.Contains(t) ?? false);

    protected override async Task LoadLookupsAsync(ProjectDbContext db)
    {
        Branches.Clear();
        foreach (var b in await db.Branches.AsNoTracking().OrderBy(b => b.Name).ToListAsync()) Branches.Add(b);
    }

    protected override Task<List<Employee>> QueryAsync(ProjectDbContext db) =>
        db.Employees.AsNoTracking().Include(e => e.Branch).OrderBy(e => e.FullName).ToListAsync();

    protected override Employee CreateNew() => new() { HireDate = DateTime.Today, BranchId = Branches.FirstOrDefault()?.Id };
    protected override string? Validate(Employee e) =>
        string.IsNullOrWhiteSpace(e.FullName) ? "أدخل اسم الموظف" : e.BaseSalary < 0 ? "الراتب لا يمكن أن يكون سالبًا" : null;
}
