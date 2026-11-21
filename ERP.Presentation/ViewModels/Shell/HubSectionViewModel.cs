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
        // ما أخفته الإدارة عن دور المستخدم من الشاشات يبقى مخفيًا داخل الصفحة أيضًا
        Tabs = tabs.Where(t => !s.Layout.Hidden.Contains(SectionCatalog.KeyOf(t.Child))).ToList();
    }

    public IReadOnlyList<HubTab> Tabs { get; }

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

    public T Child<T>() where T : SectionViewModel => Tabs.Select(t => t.Child).OfType<T>().First();

    /// <summary>فتح الصفحة (أو العودة إليها) يحدّث التبويب الظاهر فقط؛ والباقي عند فتحه.</summary>
    public override Task LoadAsync() => SelectedChild?.ActivateAsync() ?? Task.CompletedTask;
}
