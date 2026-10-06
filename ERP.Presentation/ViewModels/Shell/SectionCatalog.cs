using System.Reflection;
using ERP.Data.ProjectDb.Entities;
using ERP.Presentation.Services;

namespace ERP.Presentation.ViewModels.Shell;

/// <summary>قسم قابل للتوزيع: مفتاحه واسمه ووحدته الأصلية، وهل يُنقل.</summary>
public sealed record SectionEntry(string Key, string Title, string HomeModule, string HomeTitle, bool Movable);

/// <summary>
/// كل أقسام النظام التي تُبنى من الجلسة وحدها (مفتاح = اسم نوع الشاشة): لنقلها بين الوحدات أو إخفائها عن دور.
/// أقسام الإعدادات ولوحة المعلومات لا تُوزَّع (حتى لا يُغلق الباب على الإدارة)، وفاتورة البيع وسجل الفواتير
/// لا تُنقل لأن ترحيلها وإلغاءها يتحققان من صلاحية المبيعات داخل قاعدة البيانات نفسها.
/// </summary>
public static class SectionCatalog
{
    private static readonly HashSet<string> NotMovable = new()
    {
        KeyOf(typeof(Sales.SalesInvoiceSectionViewModel)), KeyOf(typeof(Sales.SalesInvoiceListSectionViewModel)),
    };

    private static readonly Lazy<Dictionary<string, Type>> Types = new(() =>
        typeof(SectionCatalog).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(SectionViewModel).IsAssignableFrom(t)
                        && t.GetConstructor(new[] { typeof(AppSession), typeof(IDialogService) }) is not null)
            .ToDictionary(KeyOf));

    /// <summary>مفتاح قصير ثابت: اسم الشاشة بلا اللاحقة (مثل VariantStock) — يتسع لسجل الحركات.</summary>
    public static string KeyOf(Type type) => type.Name.EndsWith("SectionViewModel") ? type.Name[..^"SectionViewModel".Length] : type.Name;
    public static string KeyOf(SectionViewModel section) => KeyOf(section.GetType());

    public static string ModuleTitle(string code) =>
        Settings.RolesPermissionsSectionViewModel.Modules.FirstOrDefault(m => m.code == code).name ?? code;

    /// <summary>أقسام الإعدادات ولوحة المعلومات خارج التوزيع.</summary>
    public static bool IsConfigurable(string homeModule) => homeModule is not (ModuleCode.SystemSettings or ModuleCode.Dashboard);

    public static bool IsMovable(Type type, string homeModule) => IsConfigurable(homeModule) && !NotMovable.Contains(KeyOf(type));

    public static SectionViewModel? Create(string key, AppSession session, IDialogService dialogs)
    {
        if (!Types.Value.TryGetValue(key, out var type)) return null;
        try { return (SectionViewModel)Activator.CreateInstance(type, session, dialogs)!; }
        catch (TargetInvocationException) { return null; }
    }

    /// <summary>الأقسام القابلة للتوزيع مرتبة حسب الوحدة ثم الاسم (تُبنى بإنشاء كل شاشة دون تحميلها).</summary>
    public static List<SectionEntry> Entries(AppSession session, IDialogService dialogs) =>
        Types.Value.Keys.Select(k => Create(k, session, dialogs)).OfType<SectionViewModel>()
            .Where(s => IsConfigurable(s.HomeModule))
            .Select(s => new SectionEntry(KeyOf(s), s.Title, s.HomeModule, ModuleTitle(s.HomeModule), IsMovable(s.GetType(), s.HomeModule)))
            .OrderBy(e => Settings.RolesPermissionsSectionViewModel.Modules.Select(m => m.code).ToList().IndexOf(e.HomeModule)).ThenBy(e => e.Title)
            .ToList();
}
