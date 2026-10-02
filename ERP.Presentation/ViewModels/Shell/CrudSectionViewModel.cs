using System.Collections.ObjectModel;
using System.Reflection;
using ERP.Data.ProjectDb;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.ViewModels.Shell;

/// <summary>
/// نمط موحّد لكل شاشات القوائم: جدول + نموذج إضافة/تعديل جانبي + بحث.
/// أزرار التعديل/الحذف داخل صفوف الجدول تربط بـ EditCommand/DeleteCommand
/// عبر ElementName=Root مع CommandParameter = الصف نفسه.
/// </summary>
public abstract class CrudSectionViewModel<T> : SectionViewModel where T : class, new()
{
    private static readonly MethodInfo CloneMethod =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private List<T> _all = new();
    private T? _editor;
    private string _searchText = "";

    protected CrudSectionViewModel(AppSession session, IDialogService dialogs, string moduleCode,
                                   string title, string glyph, string color, string description)
        : base(session, dialogs, moduleCode, title, glyph, color, description)
    {
        NewCommand = new AsyncRelayCommand(NewAsync);
        EditCommand = new AsyncRelayCommand(p => p is T row ? EditAsync(row) : Task.CompletedTask);
        DeleteCommand = new AsyncRelayCommand(p => p is T row ? DeleteAsync(row) : Task.CompletedTask);
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        CancelCommand = new RelayCommand(() => Editor = null);
    }

    public ObservableCollection<T> Items { get; } = new();

    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) ApplyFilter(); }
    }

    /// <summary>نسخة منفصلة من الصف قيد التحرير (null = النموذج مغلق).</summary>
    public T? Editor
    {
        get => _editor;
        protected set
        {
            if (!SetProperty(ref _editor, value)) return;
            OnPropertyChanged(nameof(IsEditing));
            OnPropertyChanged(nameof(EditorTitle));
            OnEditorChanged();
        }
    }

    public bool IsEditing => Editor is not null;
    protected override bool HasPendingInput => IsEditing;
    public string EditorTitle => Editor is null ? "" : GetId(Editor) == 0 ? $"إضافة — {Title}" : $"تعديل — {Title}";

    public AsyncRelayCommand NewCommand { get; }
    public AsyncRelayCommand EditCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }

    protected abstract int GetId(T entity);
    protected abstract Task<List<T>> QueryAsync(ProjectDbContext db);
    protected abstract string Describe(T entity);
    protected virtual bool Matches(T entity, string text) => Describe(entity).Contains(text, StringComparison.OrdinalIgnoreCase);
    protected virtual string? Validate(T entity) => null;
    protected virtual T CreateNew() => new();
    protected virtual Task LoadLookupsAsync(ProjectDbContext db) => Task.CompletedTask;
    protected virtual void OnEditorChanged() { }
    /// <summary>تعديلات قبل الحفظ (قيم محسوبة، قص المسافات...).</summary>
    protected virtual Task BeforeSaveAsync(ProjectDbContext db, T entity) => Task.CompletedTask;
    /// <summary>بعد نجاح الحفظ (للعمليات خارج قاعدة المشروع، مثل حسابات الدخول في قاعدة التحكم). نص = خطأ يُعرض.</summary>
    protected virtual Task<string?> AfterSaveAsync(T entity, bool isNew) => Task.FromResult<string?>(null);

    public override async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            await using var db = Session.NewDb();
            await LoadLookupsAsync(db);
            _all = await QueryAsync(db);
            ApplyFilter();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyFilter()
    {
        Items.Clear();
        var text = SearchText.Trim();
        foreach (var e in _all)
            if (text.Length == 0 || Matches(e, text)) Items.Add(e);
    }

    private Task NewAsync()
    {
        if (Require(CanAdd, "الإضافة")) Editor = CreateNew();
        return Task.CompletedTask;
    }

    private Task EditAsync(T row)
    {
        if (Require(CanEdit, "التعديل")) Editor = (T)CloneMethod.Invoke(row, null)!;
        return Task.CompletedTask;
    }

    public async Task SaveAsync()
    {
        if (Editor is null) return;
        var error = Validate(Editor);
        if (error is not null)
        {
            Dialogs.Error(error);
            return;
        }

        IsBusy = true;
        try
        {
            await using var db = Session.NewDb();
            var entity = Editor;
            await BeforeSaveAsync(db, entity);
            DetachNavigations(db, entity);
            bool isNew = GetId(entity) == 0;
            if (isNew) db.Add(entity); else db.Update(entity);
            await db.SaveChangesAsync();
            var afterError = await AfterSaveAsync(entity, isNew);
            if (afterError is not null) Dialogs.Error(afterError);
            StatusMessage = isNew ? $"تمت إضافة: {Describe(entity)}" : $"تم حفظ التعديل: {Describe(entity)}";
            Editor = null;
        }
        catch (DbUpdateException ex)
        {
            Dialogs.Error(FriendlyDbError(ex));
            return;
        }
        finally
        {
            IsBusy = false;
        }
        await LoadAsync();
    }

    private async Task DeleteAsync(T row)
    {
        if (!Require(CanDelete, "الحذف")) return;
        if (!Dialogs.Confirm($"حذف \"{Describe(row)}\" نهائيًا؟")) return;

        IsBusy = true;
        try
        {
            await using var db = Session.NewDb();
            var stub = (T)CloneMethod.Invoke(row, null)!;
            DetachNavigations(db, stub);
            db.Remove(stub);
            await db.SaveChangesAsync();
            StatusMessage = $"تم حذف: {Describe(row)}";
        }
        catch (DbUpdateException)
        {
            Dialogs.Error($"لا يمكن حذف \"{Describe(row)}\" لأنه مرتبط بحركات أو مستندات مسجّلة. " +
                          "يمكنك إيقافه (غير فعّال) بدل حذفه للحفاظ على السجل.");
            return;
        }
        finally
        {
            IsBusy = false;
        }
        await LoadAsync();
    }

    /// <summary>
    /// الصفوف تُقرأ مع علاقاتها (للعرض)؛ قبل الحفظ نفصل العلاقات حتى لا يحاول
    /// EF تعديل الكيانات المرتبطة — المفاتيح الأجنبية (…Id) هي ما يُحفظ فقط.
    /// </summary>
    private static void DetachNavigations(ProjectDbContext db, T entity)
    {
        var type = db.Model.FindEntityType(typeof(T));
        if (type is null) return;
        foreach (var nav in type.GetNavigations().Where(n => !n.IsCollection))
            nav.PropertyInfo?.SetValue(entity, null);
    }

    protected static string FriendlyDbError(DbUpdateException ex)
    {
        // استثناء بلا سبب داخلي = رسالة عربية مقصودة من BeforeSaveAsync
        if (ex.InnerException is null) return ex.Message;
        var msg = ex.InnerException.Message;
        if (msg.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) || msg.Contains("duplicate key", StringComparison.OrdinalIgnoreCase))
            return "القيمة المُدخلة مكررة (رمز أو اسم مستخدم موجود مسبقًا).";
        if (msg.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase))
            return "السجل مرتبط بسجلات أخرى ولا يمكن تنفيذ العملية.";
        if (msg.Contains("CHECK constraint", StringComparison.OrdinalIgnoreCase))
            return "إحدى القيم خارج النطاق المسموح.";
        return "تعذّر الحفظ: " + msg;
    }
}
