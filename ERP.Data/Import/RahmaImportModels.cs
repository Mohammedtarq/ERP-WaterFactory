namespace ERP.Data.Import;

/// <summary>نوع المادة الأولية في نظام الرحمة (يُستنتج من اسمها).</summary>
public enum RahmaRawKind { Preform, Cap, Label, Shrink, Carton, Other }

/// <summary>منتج تام في النظام القديم (330*20 شرنك، 330*40 كارتون) ← صنف جديد بالقطعة + مستوى تعبئة.</summary>
public class RahmaProductPlan
{
    public int LegacyId { get; init; }
    public string LegacyName { get; init; } = "";
    public int UnitsPerPack { get; init; }
    public string PackLevelName { get; init; } = "";       // شرنك / كارتون
    public string NewCode { get; init; } = "";
    public string NewName { get; init; } = "";
    public decimal PackPrice { get; init; }                 // آخر سعر بيع للعبوة
    public decimal UnitPrice => UnitsPerPack > 0 ? Math.Round(PackPrice / UnitsPerPack, 4) : 0;
}

/// <summary>مادة أولية (نوع + اسم خاص/لون) ← صنف مادة أولية جديد برصيد افتتاحي.</summary>
public class RahmaRawPlan
{
    public string Key { get; init; } = "";                   // نوع|الوصف بعد التوحيد
    public RahmaRawKind Kind { get; init; }
    public string LegacyName { get; init; } = "";           // امبولة / سدادة / ليبل ...
    public string Descriptor { get; init; } = "";           // الاسم الخاص أو اللون ("" = العام)
    public string NewCode { get; set; } = "";
    public string NewName { get; set; } = "";
    public decimal LegacyRemaining { get; init; }           // مجموع المتبقي من الدفعات
    public decimal Quantity { get; set; }                   // الكمية المنقولة (قابلة للتعديل بعد الجرد)
    public decimal? UnitCost { get; init; }                 // سعر آخر دفعة
    public decimal? AlertLevel { get; init; }
    public int Lots { get; init; }
    public bool IsSpecialLabel => Kind == RahmaRawKind.Label && Descriptor.Length > 0;
}

/// <summary>ملصق خاص ← وصفة مخصصة تستبدل الليبل العام، مربوطة بعميل (أو عامة لملصقات المناسبات).</summary>
public class RahmaRecipePlan
{
    public int ProductLegacyId { get; init; }
    public string SpecialName { get; init; } = "";
    public string LabelKey { get; init; } = "";
    public decimal LabelsPerUnit { get; init; }
    public int? CustomerLegacyId { get; set; }
    public string? CustomerName { get; set; }
}

/// <summary>رصيد منتج تام بحسب (المنتج، الاسم الخاص، اللون) ← تشغيلة افتتاحية.</summary>
public class RahmaFinishedStockPlan
{
    public int ProductLegacyId { get; init; }
    public string ProductName { get; init; } = "";
    public string SpecialName { get; init; } = "";
    public string Color { get; init; } = "";
    public decimal ProducedPacks { get; init; }
    public decimal RecordedPacks { get; init; }              // المتبقي المسجل في النظام القديم (mutabaqientaj)
    public decimal ComputedPacks { get; init; }              // الإنتاج − المحمّل − التالف − المسحوب
    public decimal Packs { get; set; }                       // المنقول (قابل للتعديل بعد الجرد)
    public string Label => string.Join(" · ", new[] { SpecialName, Color }.Where(x => x.Length > 0)) is { Length: > 0 } t ? t : "عام";
}

public class RahmaCustomerPlan
{
    public int LegacyId { get; init; }
    public string Name { get; init; } = "";
    public string? Phone { get; init; }
    public string? Address { get; init; }
    public decimal Balance { get; init; }                    // موجب = على العميل
    public decimal DepositBalance { get; init; }             // تأمينات الستيكر الخاص
}

public class RahmaSupplierPlan
{
    public int LegacyId { get; init; }
    public string Name { get; init; } = "";
    public string? Phone { get; init; }
    public decimal Balance { get; init; }                    // موجب = لنا عليه دين للمورد (ذمم دائنة)
}

public class RahmaShiftPlan
{
    public int LegacyId { get; init; }
    public string Name { get; init; } = "";
    public TimeSpan CheckIn { get; init; }
    public TimeSpan CheckOut { get; init; }
    public int CheckInGrace { get; init; }
    public int CheckOutGrace { get; init; }
}

public class RahmaEmployeePlan
{
    public int LegacyId { get; init; }
    public string Name { get; init; } = "";
    public string? Phone { get; init; }
    public string? Department { get; init; }
    public string? JobTitle { get; init; }
    public int? ShiftLegacyId { get; init; }
    public bool IsUsd { get; init; }
    public decimal Salary { get; init; }
    public DateTime? HireDate { get; init; }
    public decimal LoanBalance { get; set; }                 // رصيد السلفة غير المسدد
    public decimal LoanInstallment { get; set; }             // القسط الشهري في النظام الجديد
    /// <summary>ورد اسمه في فواتير تحميل السيارات (Fwater.EID): يُعلَّم مندوبًا وتُنشأ له سيارة (كاش فان).</summary>
    public bool IsSalesRep { get; set; }
}

public class RahmaCashBoxPlan
{
    public int LegacyId { get; init; }
    public string Name { get; init; } = "";
    public decimal LegacyBalance { get; init; }
    public decimal CountedAmount { get; set; }               // الجرد الفعلي يوم الانتقال
    public bool MergeIntoDefault { get; set; }               // يصبح الصندوق الرئيسي الافتراضي في النظام الجديد
}

/// <summary>كل ما سيُنقل، بعد التحليل وقبل التنفيذ. المستخدم يراجع ويعدّل الكميات والمبالغ والربط.</summary>
public class RahmaImportPlan
{
    public string SourceServer { get; init; } = "";
    public string SourceDatabase { get; init; } = "";
    public string? CompanyName { get; init; }
    public DateTime? LastActivity { get; init; }
    public DateTime CutoverDate { get; set; } = DateTime.Today;

    public List<RahmaProductPlan> Products { get; } = new();
    public List<RahmaRawPlan> RawMaterials { get; } = new();
    /// <summary>وصفة كل منتج للقطعة الواحدة: (مفتاح المادة، الكمية للقطعة، الدور).</summary>
    public Dictionary<int, List<(string rawKey, decimal perUnit, string role)>> Boms { get; } = new();
    public List<RahmaRecipePlan> Recipes { get; } = new();
    public List<RahmaFinishedStockPlan> FinishedStock { get; } = new();
    public List<RahmaCustomerPlan> Customers { get; } = new();
    public List<RahmaSupplierPlan> Suppliers { get; } = new();
    public List<string> Departments { get; } = new();
    public List<RahmaShiftPlan> Shifts { get; } = new();
    public List<RahmaEmployeePlan> Employees { get; } = new();
    public List<RahmaCashBoxPlan> CashBoxes { get; } = new();
    public List<string> Warnings { get; } = new();

    public decimal CustomersDebt => Customers.Where(c => c.Balance > 0).Sum(c => c.Balance);
    public decimal CustomersCredit => Customers.Where(c => c.Balance < 0).Sum(c => -c.Balance);
    public decimal DepositsTotal => Customers.Sum(c => c.DepositBalance);
    public decimal SuppliersDebt => Suppliers.Where(s => s.Balance > 0).Sum(s => s.Balance);
    public decimal SuppliersAdvance => Suppliers.Where(s => s.Balance < 0).Sum(s => -s.Balance);
    public decimal LoansTotal => Employees.Sum(e => e.LoanBalance);
    public decimal CashTotal => CashBoxes.Sum(b => b.CountedAmount);
}

/// <summary>سطر في تقرير المطابقة: القيمة في النظام القديم مقابل ما سُجّل فعلًا في النظام الجديد.</summary>
public record RahmaReconciliationRow(string Section, string Description, decimal Legacy, decimal New)
{
    public decimal Difference => New - Legacy;
    public bool Matches => Difference == 0;
}

public class RahmaImportResult
{
    public bool Success { get; init; }
    public bool Committed { get; init; }
    public string? Error { get; init; }
    public List<RahmaReconciliationRow> Reconciliation { get; init; } = new();
    public string Summary { get; init; } = "";
    public bool AllMatch => Reconciliation.All(r => r.Matches);
}

/// <summary>قاعدة بيانات على السيرفر تحمل شكل نظام الرحمة.</summary>
public record RahmaDatabaseCandidate(string Name, DateTime? LastActivity);
