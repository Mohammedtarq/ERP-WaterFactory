namespace ERP.Data.ProjectDb.Entities;

public enum PromotionMovementType { Promotion, AnnualRaise, AnnualBonus }
public enum PromotionApplicationType { PermanentAddition, OneTime }

public class PromotionAndRaise
{
    public int Id { get; set; }

    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public PromotionMovementType MovementType { get; set; }
    public string? NewJobTitle { get; set; }
    public decimal Amount { get; set; }
    public DateTime EffectiveDate { get; set; }
    public PromotionApplicationType ApplicationType { get; set; }
    public string? Notes { get; set; }

    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
}
