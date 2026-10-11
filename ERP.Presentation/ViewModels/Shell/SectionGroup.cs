using ERP.Data.ProjectDb.Entities;
using System.Collections.ObjectModel;
using ERP.Presentation.Mvvm;

namespace ERP.Presentation.ViewModels.Shell;

/// <summary>مجموعة أقسام في القائمة الجانبية (مثل «الحضور» تضم «الحضور اليومي» و«استيراد البصمة»)، لها لون ثابت.</summary>
public class SectionGroup : ObservableObject
{
    private bool _isOpen;

    public SectionGroup(string name, string color)
    {
        Name = name;
        Color = color;
    }

    public string Name { get; }
    public string Color { get; }
    public ObservableCollection<SectionViewModel> Items { get; } = new();
    public int Count => Items.Count;
    public bool IsOpen { get => _isOpen; internal set => SetProperty(ref _isOpen, value); }
}

/// <summary>
/// توزيع أقسام كل وحدة على مجموعات للتنقل الجانبي. المفتاح = اسم شاشة القسم بلا اللاحقة (SectionCatalog.KeyOf).
/// قسم لا يرد هنا (منقول من وحدة أخرى بقرار الإدارة، أو شاشة جديدة) يظهر في مجموعة «أقسام أخرى» فلا يضيع.
/// </summary>
public static class SectionGrouping
{
    public sealed record GroupDef(string Name, string Color, params string[] Keys);

    public const string OtherName = "أقسام أخرى";
    public const string OtherColor = "#64748B";

    private const string Blue = "#3B82F6", Green = "#10B981", Amber = "#F59E0B", Teal = "#14B8A6", Purple = "#8B5CF6", Orange = "#F97316", Slate = "#64748B";

    private static readonly Dictionary<string, GroupDef[]> Defs = new()
    {
        [ModuleCode.Warehouse] = new[]
        {
            new GroupDef("مساحات المخازن", Blue, "WarehouseWorkspace", "WarehouseRepVans"),
            new GroupDef("الحركة اليومية", Green, "PrepareLoadOrders", "Stocktake", "Losses", "DamagedSales", "StockAdjustment"),
            new GroupDef("الأرصدة والتنبيهات", Amber, "CurrentStock", "StockAlerts", "Beneficiaries"),
            new GroupDef("التصنيع", Teal, "ManufacturingRequirement", "Bom", "PackagingTemplates"),
            new GroupDef("التعريفات", Purple, "Items", "Warehouses", "Packaging", "Locations"),
        },
        [ModuleCode.HR] = new[]
        {
            new GroupDef("الموظفون", Blue, "Employees", "Departments", "Shifts", "Promotions", "TempWorkers"),
            new GroupDef("الحضور", Green, "Attendance", "FingerprintImport"),
            new GroupDef("الحوافز", Amber, "MonthlyIncentive", "IncentiveSettings", "RepIncentiveRates", "ManagerTiers"),
            new GroupDef("الرواتب", Purple, "Payroll", "EmployeeDeductions"),
        },
        [ModuleCode.Production] = new[]
        {
            new GroupDef("التصنيع", Teal, "ProductionOrders", "DailyProduction", "MachineWip", "Packing"),
            new GroupDef("المتابعة", Amber, "PendingProduction", "VariantStock", "ProductionMonth"),
            new GroupDef("الإعداد", Slate, "Machines", "CustomRecipes"),
        },
        [ModuleCode.Lab] = new[]
        {
            new GroupDef("الفحص والاختبارات", Purple, "Qc", "QualityTests"),
        },
        [ModuleCode.Sales] = new[]
        {
            new GroupDef("البيع", Green, "SalesInvoice", "SalesInvoiceList", "CustomerReturn"),
            new GroupDef("العملاء", Blue, "Customers", "CustomerStatement", "CustomerDeposits"),
            new GroupDef("الأسعار والصندوق", Amber, "SellingPrices", "LoadingSettings", "CashBoxes"),
        },
        [ModuleCode.Suppliers] = new[]
        {
            new GroupDef("الشراء", Amber, "PurchaseInvoice", "PurchaseOrders", "GoodsReceipt", "Reorder"),
            new GroupDef("الموردون", Blue, "Suppliers", "SupplierStatement"),
        },
        [ModuleCode.Finance] = new[]
        {
            new GroupDef("النقد", Green, "CashBoxes", "DailyCash", "Vouchers", "Expenses", "Reconciliation"),
            new GroupDef("الحسابات", Blue, "ChartOfAccounts", "JournalEntries", "ExchangeRates", "MappingRules"),
            new GroupDef("التقارير والتحليل", Purple, "FinalAccounts", "WorkingCapital", "CostSimulation", "Partners"),
            new GroupDef("الإعداد", Slate, "FinanceCategories", "PeriodLock"),
        },
        [ModuleCode.Reps] = new[]
        {
            new GroupDef("السيارات والتحميل", Orange, "RepVans", "LoadOrders", "RepDocuments", "Fleet", "VanOperations"),
            new GroupDef("التسوية والمحفظة", Green, "RepSettlement", "Wallet", "RepIncentives"),
            new GroupDef("تطبيق المندوب", Blue, "RepRequests", "RepDevices", "SyncConflicts"),
            new GroupDef("العملاء والمناطق", Purple, "Territories", "CustomerAssignments"),
        },
        [ModuleCode.SystemSettings] = new[]
        {
            new GroupDef("الصلاحيات", Blue, "RolesPermissions", "SectionLayout", "Users", "AuditLog"),
            new GroupDef("المشروع", Green, "Branches", "Projects", "Branding"),
            new GroupDef("النظام", Slate, "Backup", "CloudSync", "RahmaImport"),
        },
    };

    public static IReadOnlyList<GroupDef> For(string? moduleCode) =>
        moduleCode is not null && Defs.TryGetValue(moduleCode, out var d) ? d : Array.Empty<GroupDef>();
}
