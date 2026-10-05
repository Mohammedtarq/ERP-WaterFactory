namespace ERP.Data.ProjectDb.Entities;

/// <summary>قالب أمر اليوم المعتاد (34_batch_variants.sql): سطور جاهزة تُعدَّل كمياتها ثم تُسجَّل.</summary>
public class DailyProductionTemplate
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int CreatedByUserId { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<DailyProductionTemplateLine> Lines { get; set; } = new List<DailyProductionTemplateLine>();
}

public class DailyProductionTemplateLine
{
    public int Id { get; set; }
    public int TemplateId { get; set; }
    public DailyProductionTemplate Template { get; set; } = null!;
    public int LineNo { get; set; }
    public int FinishedItemId { get; set; }
    public Item FinishedItem { get; set; } = null!;
    public int PackagingLevelId { get; set; }
    public ItemPackagingLevel PackagingLevel { get; set; } = null!;
    public int? CustomRecipeId { get; set; }
    public CustomRecipe? CustomRecipe { get; set; }
    public decimal Packs { get; set; }
}
