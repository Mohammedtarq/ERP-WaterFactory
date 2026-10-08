namespace ERP.Data.ProjectDb.Entities;

/// <summary>هوية الطباعة المركزية (سجل واحد، Id = 1).</summary>
public class CompanyProfile
{
    public int Id { get; set; } = 1;
    public string NameAr { get; set; } = "";
    public string? NameEn { get; set; }
    /// <summary>أرقام الهاتف، رقم في كل سطر.</summary>
    public string? Phones { get; set; }
    public string? Address { get; set; }
    public string? TaxNumber { get; set; }
    public string? CommercialRegister { get; set; }
    public string BrandColor { get; set; } = "#0F766E";
    public string? FooterText { get; set; }
    public byte[]? Logo { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
