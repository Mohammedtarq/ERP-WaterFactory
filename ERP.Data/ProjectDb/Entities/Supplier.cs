namespace ERP.Data.ProjectDb.Entities;

public enum SupplierPaymentTerms { Cash, Credit, AdvancePlusCredit }

public class Supplier
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public SupplierPaymentTerms? DefaultPaymentTerms { get; set; }
    public bool IsActive { get; set; } = true;
}
