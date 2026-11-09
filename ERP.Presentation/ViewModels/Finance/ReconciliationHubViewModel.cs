using ERP.Data.ProjectDb.Entities;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;

namespace ERP.Presentation.ViewModels.Finance;

/// <summary>
/// «المطابقة والأرباح»: صفحة واحدة بثلاث تبويبات تضم الشاشات الثلاث الحالية دون تعديلها:
/// ① الفائض (رأس المال والفائض)، ② المطابقة الدورية، ③ الشركاء والأرباح. تحسب من مصدر الأرقام نفسه،
/// وتبقى الحسابات والصلاحيات كما هي. تنتقل بين الخطوات بضغطة تبويب دون مغادرة الصفحة.
/// </summary>
public class ReconciliationHubSectionViewModel : SectionViewModel
{
    private int _selectedIndex = 1;   // المطابقة أولًا: أكثر التبويبات استعمالًا

    public ReconciliationHubSectionViewModel(AppSession s, IDialogService d)
        : this(s, d, new ReconciliationSectionViewModel(s, d), new WorkingCapitalSectionViewModel(s, d), new PartnersSectionViewModel(s, d)) { }

    public ReconciliationHubSectionViewModel(AppSession s, IDialogService d, ReconciliationSectionViewModel reconciliation,
                                             WorkingCapitalSectionViewModel surplus, PartnersSectionViewModel partners)
        : base(s, d, ModuleCode.Finance, "المطابقة والأرباح", Icons.Statement, "#0F766E",
               "الفائض عن رأس المال، والمطابقة الدورية وتوزيع الأرباح، وأرصدة الشركاء وسحبهم — في صفحة واحدة")
    {
        Reconciliation = reconciliation;
        Surplus = surplus;
        Partners = partners;
    }

    /// <summary>الشاشات الثلاث نفسها (حسابات وصلاحيات كل منها كما هي).</summary>
    public ReconciliationSectionViewModel Reconciliation { get; }
    public WorkingCapitalSectionViewModel Surplus { get; }
    public PartnersSectionViewModel Partners { get; }

    protected override bool ReloadOnActivate => true;

    /// <summary>التبويب الظاهر: 0 الفائض، 1 المطابقة، 2 الشركاء.</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set { if (SetProperty(ref _selectedIndex, value)) { OnPropertyChanged(nameof(SelectedChild)); Background(SelectedChild.ActivateAsync()); } }
    }

    public SectionViewModel SelectedChild => _selectedIndex switch { 0 => Surplus, 2 => Partners, _ => Reconciliation };

    /// <summary>فتح الصفحة (أو العودة إليها) يحدّث التبويب الظاهر فقط؛ والباقي عند فتحه.</summary>
    public override Task LoadAsync() => SelectedChild.ActivateAsync();
}
