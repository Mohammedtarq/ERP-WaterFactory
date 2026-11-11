using ERP.Data.ProjectDb.Entities;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;

namespace ERP.Presentation.ViewModels.HR;

// «الموارد البشرية» عشرون شاشة صارت ست صفحات بتبويبات ملوّنة + التقارير؛ كل تبويب هو الشاشة الأصلية كما هي.
// ألوان التبويبات: أزرق، أخضر، كهرماني، وردي، بنفسجي، فيروزي، برتقالي.
internal static class HubColors
{
    public const string Blue = "#3B82F6", Green = "#10B981", Amber = "#F59E0B", Pink = "#EC4899", Purple = "#8B5CF6", Teal = "#14B8A6", Orange = "#F97316";
}

/// <summary>الموظفون: الموظفون، الأقسام، الورديات، استثناءات الوردية، العمال المؤقتون.</summary>
public class EmployeesHubSectionViewModel : HubSectionViewModel
{
    public EmployeesHubSectionViewModel(AppSession s, IDialogService d) : base(s, d, ModuleCode.HR, "الموظفون", Icons.HR, HubColors.Blue,
        "الموظفون وأقسامهم وورديات الدوام والعمال المؤقتون — في صفحة واحدة",
        new HubTab("الموظفون", HubColors.Blue, new EmployeesSectionViewModel(s, d)),
        new HubTab("الأقسام", HubColors.Green, new DepartmentsSectionViewModel(s, d)),
        new HubTab("شفتات العمل", HubColors.Amber, new ShiftsSectionViewModel(s, d)),
        new HubTab("استثناء الشفتات", HubColors.Pink, new ShiftOverridesSectionViewModel(s, d)),
        new HubTab("العمال المؤقتون", HubColors.Purple, new TempWorkersSectionViewModel(s, d))) { }
}

/// <summary>الحضور: الحضور اليومي، استيراد البصمة، الوقت الإضافي.</summary>
public class AttendanceHubSectionViewModel : HubSectionViewModel
{
    public AttendanceHubSectionViewModel(AppSession s, IDialogService d) : base(s, d, ModuleCode.HR, "الحضور", Icons.Calendar, HubColors.Pink,
        "الحضور اليومي واستيراد البصمة والوقت الإضافي — في صفحة واحدة",
        new HubTab("الحضور اليومي", HubColors.Pink, new AttendanceSectionViewModel(s, d)),
        new HubTab("استيراد البصمة", HubColors.Blue, new FingerprintImportSectionViewModel(s, d)),
        new HubTab("الوقت الإضافي", HubColors.Orange, new OvertimeSectionViewModel(s, d))) { }
}

/// <summary>الإجازات: الإجازات وإعداداتها.</summary>
public class LeavesHubSectionViewModel : HubSectionViewModel
{
    public LeavesHubSectionViewModel(AppSession s, IDialogService d) : base(s, d, ModuleCode.HR, "الإجازات", Icons.Calendar, HubColors.Green,
        "طلبات الإجازات وأرصدتها وإعداد أنواعها — في صفحة واحدة",
        new HubTab("الإجازات", HubColors.Green, new LeavesSectionViewModel(s, d)),
        new HubTab("إعدادات الإجازات", HubColors.Teal, new LeaveSettingsSectionViewModel(s, d))) { }
}

/// <summary>الترقيات والعلاوات: الترقيات، العلاوة السنوية، درجات العلاوة.</summary>
public class RaisesHubSectionViewModel : HubSectionViewModel
{
    public RaisesHubSectionViewModel(AppSession s, IDialogService d) : base(s, d, ModuleCode.HR, "الترقيات والعلاوات", Icons.HR, HubColors.Purple,
        "الترقيات والعلاوات السنوية ودرجات العلاوة — في صفحة واحدة",
        new HubTab("الترقيات", HubColors.Purple, new PromotionsSectionViewModel(s, d)),
        new HubTab("العلاوة السنوية", HubColors.Amber, new AnnualRaisesSectionViewModel(s, d)),
        new HubTab("درجات العلاوة", HubColors.Teal, new RaiseGradesSectionViewModel(s, d))) { }
}

/// <summary>الحوافز: الحافز الشهري، إعداداته، نسب المندوبين، شرائح المدراء.</summary>
public class IncentivesHubSectionViewModel : HubSectionViewModel
{
    public IncentivesHubSectionViewModel(AppSession s, IDialogService d) : base(s, d, ModuleCode.HR, "الحوافز", Icons.HR, HubColors.Amber,
        "الحافز الشهري وإعداداته ونسب المندوبين وشرائح المدراء — في صفحة واحدة",
        new HubTab("الحافز الشهري", HubColors.Amber, new MonthlyIncentiveSectionViewModel(s, d)),
        new HubTab("إعدادات الحوافز", HubColors.Blue, new IncentiveSettingsSectionViewModel(s, d)),
        new HubTab("حوافز المندوبين", HubColors.Green, new RepIncentiveRatesSectionViewModel(s, d)),
        new HubTab("حافز مدير المبيعات", HubColors.Pink, new ManagerTiersSectionViewModel(s, d))) { }
}

/// <summary>الرواتب: كشف الرواتب والاستقطاعات.</summary>
public class PayrollHubSectionViewModel : HubSectionViewModel
{
    public PayrollHubSectionViewModel(AppSession s, IDialogService d) : base(s, d, ModuleCode.HR, "الرواتب", Icons.HR, HubColors.Teal,
        "كشف الرواتب الشهري والاستقطاعات — في صفحة واحدة",
        new HubTab("كشف الرواتب", HubColors.Teal, new PayrollSectionViewModel(s, d)),
        new HubTab("الاستقطاعات", HubColors.Orange, new EmployeeDeductionsSectionViewModel(s, d))) { }
}
