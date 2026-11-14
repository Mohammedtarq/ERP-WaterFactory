namespace ERP.Data.ProjectDb.Entities;

/// <summary>
/// عملة الراتب — الدينار العراقي هو الافتراضي لكل النظام،
/// مع استثناء الدولار لعدد محدود من الموظفين فقط (كما تقرر).
/// </summary>
public enum SalaryCurrency
{
    IQD,
    USD
}
