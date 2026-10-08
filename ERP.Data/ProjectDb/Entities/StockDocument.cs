namespace ERP.Data.ProjectDb.Entities;

/// <summary>نوع مستند المخزن من واجهة كل مخزن.</summary>
/// <summary>RepLoad = إسناد حمولة لمندوب، RepReturn = إرجاع من مندوب (21_rep_documents.sql).</summary>
public enum StockDocumentType { Receipt, Issue, Transfer, Damaged, FreeIssue, RepLoad, RepReturn }

/// <summary>
/// مستند مخزني مرقّم (إدخال، إخراج، مناقلة، تالف، مسحوب مجاني) — قابل للطباعة.
/// حركاته الفعلية في StockTransactions (ReferenceTable = "StockDocuments").
/// </summary>
public class StockDocument
{
    public int Id { get; set; }
    public string DocumentNumber { get; set; } = "";
    public StockDocumentType DocumentType { get; set; }

    public int WarehouseId { get; set; }
    public Warehouse Warehouse { get; set; } = null!;
    public int? CounterWarehouseId { get; set; }
    public Warehouse? CounterWarehouse { get; set; }

    public DateTime DocumentDate { get; set; } = DateTime.Today;
    public string? PartyName { get; set; }
    public int? RepEmployeeId { get; set; }
    public Employee? RepEmployee { get; set; }
    public DamageReason? DamageReason { get; set; }
    /// <summary>تصنيف جهة المسحوب المجاني (حكومية، سائقون، شركاء...) لتقرير شهري لكل جهة.</summary>
    public BeneficiaryCategory? BeneficiaryCategory { get; set; }
    public string? Notes { get; set; }

    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<StockDocumentLine> Lines { get; set; } = new();
}

public class StockDocumentLine
{
    public int Id { get; set; }
    public int StockDocumentId { get; set; }
    public StockDocument StockDocument { get; set; } = null!;

    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;
    public int PackagingLevelId { get; set; }
    public ItemPackagingLevel PackagingLevel { get; set; } = null!;

    public decimal QuantityInLevel { get; set; }
    public decimal QuantityBaseUnits { get; set; }
    public int? BatchId { get; set; }
    public ItemBatch? Batch { get; set; }
    /// <summary>سطر إرجاع تالف ميدانيًا (لا يعود رصيدًا سليمًا).</summary>
    public bool IsDamaged { get; set; }
    public string? Notes { get; set; }
}
