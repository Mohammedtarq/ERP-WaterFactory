using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;

namespace ERP.Presentation.ViewModels.Shell;

/// <summary>تبويب داخل صفحة مدموجة: عنوان ولون وشاشته الأصلية كما هي (بلا تعديل في منطقها).</summary>
public sealed record HubTab(string Title, string Color, SectionViewModel Child);

/// <summary>
/// صفحة واحدة بتبويبات ملوّنة تضم عدة شاشات قائمة كما هي (حساباتها وصلاحياتها لا تتغير). كل تبويب بلونه،
/// والمختار يمتلئ بلونه ويأخذ إطار الصفحة اللون نفسه. النمط ذاته المستعمل في «المطابقة والأرباح».
/// </summary>
public abstract class HubSectionViewModel : SectionViewModel
{
    private int _selectedIndex;

    protected HubSectionViewModel(AppSession s, IDialogService d, string module, string title, string glyph, string color,
                                  string description, params HubTab[] tabs)
        : base(s, d, module, title, glyph, color, description)
    {
        // ما أخفته الإدارة عن دور المستخدم أو نقلته إلى وحدة أخرى يغيب عن الصفحة؛ يبقى كائنه في AllTabs للروابط الداخلية
        AllTabs = tabs;
        Tabs = tabs.Where(t => !IsAway(s, t.Child)).ToList();
    }

    /// <summary>الشاشة غابت عن هذه الصفحة: مخفية عن دور المستخدم، أو منقولة بقرار الإدارة إلى وحدة أخرى (تظهر هناك شاشةً مستقلة).</summary>
    private static bool IsAway(AppSession s, SectionViewModel child)
    {
        var key = SectionCatalog.KeyOf(child);
        if (s.Layout.Hidden.Contains(key) && SectionCatalog.IsConfigurable(child.HomeModule)) return true;
        return s.Layout.Moves.TryGetValue(key, out var target) && target != child.HomeModule && SectionCatalog.IsMovable(child.GetType(), child.HomeModule);
    }

    /// <summary>التبويبات الظاهرة لهذا المستخدم.</summary>
    public IReadOnlyList<HubTab> Tabs { get; }
    /// <summary>كل تبويبات الصفحة قبل الإخفاء والنقل (لتوزيع الأقسام وللروابط الداخلية).</summary>
    public IReadOnlyList<HubTab> AllTabs { get; }

    protected override bool ReloadOnActivate => true;
    protected override bool HasPendingInput => Tabs.Any(t => t.Child.HasPendingInputNow);
    protected override void ResetInput() { foreach (var t in Tabs) t.Child.Leave(); }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (value < 0 || value >= Tabs.Count) return;
            if (!SetProperty(ref _selectedIndex, value)) return;
            OnPropertyChanged(nameof(SelectedChild));
            OnPropertyChanged(nameof(SelectedColor));
            Background(Tabs[value].Child.ActivateAsync());
        }
    }

    public SectionViewModel? SelectedChild => Tabs.Count == 0 ? null : Tabs[_selectedIndex].Child;
    public string SelectedColor => Tabs.Count == 0 ? Color : Tabs[_selectedIndex].Color;

    /// <summary>الشاشة الأصلية موجودة داخل هذه الصفحة؟ (للبحث والتنبيهات والمفضلة القديمة)</summary>
    public bool Contains(Func<SectionViewModel, bool> match) => Tabs.Any(t => match(t.Child));

    public bool Select(Func<SectionViewModel, bool> match)
    {
        var i = Tabs.ToList().FindIndex(t => match(t.Child));
        if (i < 0) return false;
        SelectedIndex = i;
        return true;
    }

    /// <summary>الشاشة داخل الصفحة بنوعها، ولو غابت عن هذا المستخدم (يبقى كائنها متاحًا للروابط الداخلية).</summary>
    public T Child<T>() where T : SectionViewModel => AllTabs.Select(t => t.Child).OfType<T>().First();

    /// <summary>فتح الصفحة (أو العودة إليها) يحدّث التبويب الظاهر فقط؛ والباقي عند فتحه.</summary>
    public override Task LoadAsync() => SelectedChild?.ActivateAsync() ?? Task.CompletedTask;
}
