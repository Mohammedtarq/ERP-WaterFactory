using ERP.Data.ProjectDb.Entities;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.HR;
using ERP.Presentation.ViewModels.Shell;

namespace ERP.Presentation.ViewModels.Warehouse;

// «المخازن»: إحدى وعشرون بطاقة صارت ثلاث مساحات مخازن + سبع صفحات بتبويبات ملوّنة (عملياتها مترابطة)؛
// كل تبويب هو الشاشة الأصلية كما هي. الإدارة تخفي أي تبويب عن دور أو تنقله إلى وحدة أخرى من «توزيع الأقسام».

/// <summary>المندوبون: دورة المندوب اليومية في المخزن — تجهيز السيارة، متابعتها، تسوية مرتجعها ومجانيها، تأكيد التسوية.</summary>
public class WarehouseRepsHubSectionViewModel : HubSectionViewModel
{
    public WarehouseRepsHubSectionViewModel(AppSession s, IDialogService d) : base(s, d, ModuleCode.Warehouse, "المندوبون", Icons.Truck, "#16A34A",
        "تجهيز سيارات المندوبين ومتابعتها وتسوية مرتجعها ومجانيها وتأكيد التسوية — في صفحة واحدة",
        new HubTab("طلبات التجهيز", HubColors.Blue, new PrepareLoadOrdersSectionViewModel(s, d)),
        new HubTab("سيارات المندوبين", HubColors.Green, new Reps.WarehouseRepVansSectionViewModel(s, d)),
        new HubTab("تسوية المنتجات", HubColors.Amber, new Reps.RepProductSettlementSectionViewModel(s, d)),
        new HubTab("تأكيد التسويات", HubColors.Pink, new SettlementConfirmSectionViewModel(s, d))) { }
}

/// <summary>الجرد والتالف: العدّ الفعلي وفرقه، والتلف والمسحوب المجاني وجهاته، وبيع المواد التالفة.</summary>
public class StockLossesHubSectionViewModel : HubSectionViewModel
{
    public StockLossesHubSectionViewModel(AppSession s, IDialogService d) : base(s, d, ModuleCode.Warehouse, "الجرد والتالف", Icons.Adjust, "#B45309",
        "الجرد الفعلي والتلف والمسحوب المجاني وجهاته وبيع المواد التالفة — في صفحة واحدة",
        new HubTab("الجرد", HubColors.Teal, new StocktakeSectionViewModel(s, d)),
        new HubTab("التلف والمسحوب وفرق الجرد", HubColors.Orange, new LossesSectionViewModel(s, d)),
        new HubTab("بيع المواد التالفة", HubColors.Pink, new DamagedSalesSectionViewModel(s, d)),
        new HubTab("جهات المسحوب المجاني", HubColors.Purple, new BeneficiariesSectionViewModel(s, d))) { }
}

/// <summary>الأرصدة والتنبيهات: الرصيد لكل صنف ومخزن وتشغيلة، وحدود التنبيه.</summary>
public class StockBalancesHubSectionViewModel : HubSectionViewModel
{
    public StockBalancesHubSectionViewModel(AppSession s, IDialogService d) : base(s, d, ModuleCode.Warehouse, "الأرصدة والتنبيهات", Icons.Stock, "#10B981",
        "الكميات والقيمة لكل صنف ومخزن وتشغيلة وحدود التنبيه — في صفحة واحدة",
        new HubTab("الرصيد الحالي", HubColors.Green, new CurrentStockSectionViewModel(s, d)),
        new HubTab("إعدادات التنبيهات", HubColors.Orange, new StockAlertsSectionViewModel(s, d))) { }
}

/// <summary>احتياجات وقوائم التصنيع: المواد المطلوبة مقابل المتاح، والوصفة الأساسية لكل منتج.</summary>
public class ManufacturingHubSectionViewModel : HubSectionViewModel
{
    public ManufacturingHubSectionViewModel(AppSession s, IDialogService d) : base(s, d, ModuleCode.Warehouse, "احتياجات وقوائم التصنيع", Icons.Factory, "#8B5CF6",
        "المواد الأولية المطلوبة مقابل المتاح، والوصفة الأساسية لكل منتج — في صفحة واحدة",
        new HubTab("احتياجات التصنيع", HubColors.Purple, new ManufacturingRequirementSectionViewModel(s, d)),
        new HubTab("قوائم المواد", HubColors.Pink, new BomSectionViewModel(s, d))) { }
}

/// <summary>الأصناف والتعبئة: الأصناف والباركود وسعر البيع، وهيكلية التعبئة (قطعة ← شرنك ← كارتون) وقوالبها.</summary>
public class ItemsPackagingHubSectionViewModel : HubSectionViewModel
{
    public ItemsPackagingHubSectionViewModel(AppSession s, IDialogService d) : base(s, d, ModuleCode.Warehouse, "الأصناف والتعبئة", Icons.Item, "#0EA5E9",
        "الأصناف والباركود وسعر البيع وهيكلية التعبئة وقوالبها — في صفحة واحدة",
        new HubTab("الأصناف", HubColors.Blue, new ItemsSectionViewModel(s, d)),
        new HubTab("هيكلية التعبئة", HubColors.Teal, new PackagingSectionViewModel(s, d)),
        new HubTab("قوالب التعبئة", HubColors.Amber, new PackagingTemplatesSectionViewModel(s, d))) { }
}

/// <summary>المخازن والمواقع: تعريف المخازن ومواقع التخزين فيها (منطقة ← رف ← موقع).</summary>
public class WarehouseSetupHubSectionViewModel : HubSectionViewModel
{
    public WarehouseSetupHubSectionViewModel(AppSession s, IDialogService d) : this(s, d, null) { }

    /// <param name="changed">يُستدعى بعد حفظ مخزن: ليظهر تبويبه في الوحدة فورًا.</param>
    public WarehouseSetupHubSectionViewModel(AppSession s, IDialogService d, Func<Task>? changed) : base(s, d, ModuleCode.Warehouse, "المخازن والمواقع", Icons.Store, "#6366F1",
        "تعريف المخازن ومواقع التخزين فيها (منطقة ← رف ← موقع) — في صفحة واحدة",
        new HubTab("تعريف المخازن", HubColors.Blue, new WarehousesSectionViewModel(s, d, changed)),
        new HubTab("مواقع المخزن", HubColors.Amber, new LocationsSectionViewModel(s, d))) { }
}
