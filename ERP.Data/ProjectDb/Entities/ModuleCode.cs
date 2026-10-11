namespace ERP.Data.ProjectDb.Entities;

/// <summary>
/// أكواد الوحدات المستخدمة في مصفوفة الصلاحيات وفي بناء الشريط الجانبي.
/// إضافة وحدة جديدة مستقبلاً = إضافة سطر جديد هنا فقط.
/// </summary>
public static class ModuleCode
{
    public const string Dashboard = "Dashboard";
    public const string Warehouse = "Warehouse";
    public const string Sales = "Sales";
    public const string Suppliers = "Suppliers";
    public const string Finance = "Finance";
    public const string HR = "HR";
    public const string Reps = "Reps";
    public const string Production = "Production";
    /// <summary>المختبر وحدة مستقلة عن الإنتاج: من ينتج لا يحكم على جودة إنتاجه (فحص الأوامر واختبارات الجودة).</summary>
    public const string Lab = "Lab";
    public const string SystemSettings = "SystemSettings";
}
