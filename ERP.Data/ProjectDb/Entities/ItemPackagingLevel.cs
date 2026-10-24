namespace ERP.Data.ProjectDb.Entities;

/// <summary>
/// مستوى تعبيئة واحد لصنف معيّن (قطعة/شرنك/كارتون...). العلاقة الذاتية
/// (ParentLevelId) تسمح ببناء هرمية بأي عدد من المستويات.
/// </summary>
public class ItemPackagingLevel
{
    public int Id { get; set; }

    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;

    public string LevelName { get; set; } = string.Empty;   // قطعة / شرنك / كارتون

    public int? ParentLevelId { get; set; }
    public ItemPackagingLevel? ParentLevel { get; set; }

    public decimal ContainsQuantity { get; set; } = 1;       // كم من المستوى الأصغر يحويه هذا المستوى
    public decimal EquivalentBaseUnits { get; set; }         // = بالقطعة (محسوبة ومخزَّنة)
    public bool IsSellableUnit { get; set; } = true;
}
