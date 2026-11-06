using ERP.Data.ProjectDb.Entities;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;

namespace ERP.Presentation.ViewModels.Production;

/// <summary>
/// المختبر وحدة مستقلة عن الإنتاج بصلاحيتها: فحص واحد لكل أمر إنتاج بكل أصنافه، وتعريف اختبارات الجودة.
/// التعبئة (في الإنتاج) تبقى ممنوعة لأي أمر لم ينجح فحصه.
/// </summary>
public class LabModuleViewModel : ModuleViewModel
{
    public LabModuleViewModel(AppSession s, IDialogService d)
        : base("المختبر", Icons.Lab, ModuleColors.Lab)
    {
        UseDashboard(s, d, ModuleCode.Lab, ModuleDashboardViewModel.LabModule);
        Qc = Add(new QcSectionViewModel(s, d));
        Tests = Add(new QualityTestsSectionViewModel(s, d));
    }

    public QcSectionViewModel Qc { get; }
    public QualityTestsSectionViewModel Tests { get; }
}
