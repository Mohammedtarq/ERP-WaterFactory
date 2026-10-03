namespace ERP.Data.ProjectDb.Entities;

public enum CustomerType { Agent, SubCustomer, Direct }

public class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public CustomerType CustomerType { get; set; } = CustomerType.Direct;

    public int? ParentAgentId { get; set; }
    public Customer? ParentAgent { get; set; }

    public string? Province { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>حد الدين بالمبلغ: الآجل الذي يتجاوزه يوقف حتى يوافق المدير (NULL = بلا حد).</summary>
    public decimal? CreditLimit { get; set; }

    // ملاحظة: مديونية كل عميل (وكيل أو فرعي) مستقلة تمامًا وتُحسب من
    // SalesInvoices + Vouchers الخاصة به فقط — لا حقل رصيد هنا، بنفس مبدأ
    // "السجل بدل العمود" المستخدم في المخزون والمالية.
}
