namespace ERP.Data.ProjectDb.Entities;

public class Role
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>سقف الصرف للعملية الواحدة (سند صرف، سحب من صندوق). null = بلا سقف؛ ما فوقه يحتاج صلاحية الاعتماد.</summary>
    public decimal? MaxPaymentAmount { get; set; }

    public ICollection<RolePermission> Permissions { get; set; } = new List<RolePermission>();
}
