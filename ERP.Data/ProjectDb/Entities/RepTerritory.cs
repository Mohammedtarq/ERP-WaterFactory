namespace ERP.Data.ProjectDb.Entities;

public class RepTerritory
{
    public int Id { get; set; }

    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public string TerritoryName { get; set; } = string.Empty;
}

/// <summary>
/// علاقة متعدد-لمتعدد مرنة بين المندوبين والعملاء. الوكلاء بلا مندوب حاليًا
/// ببساطة لا يملكون أي سطر هنا؛ يمكن ربطهم لاحقًا دون أي تعديل بنيوي.
/// </summary>
public class RepCustomerAssignment
{
    public int Id { get; set; }

    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
}
