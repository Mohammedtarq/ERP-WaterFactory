namespace ERP.Data.ProjectDb.Entities;

/// <summary>شريك في المعمل ونسبته من الأرباح. رصيده = حصصه من المطابقات + الافتتاحي − ما سحبه.</summary>
public class Partner
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal SharePercent { get; set; }
    public bool IsManager { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
}

public enum PartnerTxKind { ProfitShare, Withdrawal, Opening }

/// <summary>حركة على حساب شريك: حصة أرباح (موجبة أو سالبة عند الخسارة)، سحب نقدي (سالب)، أو رصيد افتتاحي.</summary>
public class PartnerTransaction
{
    public int Id { get; set; }
    public string TxNumber { get; set; } = "";
    public int PartnerId { get; set; }
    public Partner Partner { get; set; } = null!;
    public DateTime TxDate { get; set; } = DateTime.Today;
    public PartnerTxKind Kind { get; set; }
    /// <summary>موجب = له، سالب = عليه.</summary>
    public decimal Amount { get; set; }
    public decimal? SharePercent { get; set; }
    public int? ReconciliationId { get; set; }
    public AssetReconciliation? Reconciliation { get; set; }
    public string? Notes { get; set; }
    public int? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }
    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>تقييم المنتج التام في المطابقة.</summary>
public enum FinishedGoodsValuation { Cost, SalePrice }

/// <summary>
/// مطابقة الموجودات (لقطة محفوظة): المواد الأولية وتحت التصنيع بالكلفة، والمنتج التام بالكلفة أو بسعر البيع،
/// وديون العملاء والنقد والسلف، مطروحًا منها ديون الموردين وتأمينات العملاء. الفائض = الصافي − صافي المطابقة
/// السابقة + ما سحبه الشركاء بينهما، ويوزَّع على الشركاء بنسبهم.
/// </summary>
public class AssetReconciliation
{
    public int Id { get; set; }
    public string ReconNumber { get; set; } = "";
    public DateTime ReconDate { get; set; } = DateTime.Today;
    public FinishedGoodsValuation FinishedGoodsValuation { get; set; }
    public decimal RawMaterialsValue { get; set; }
    public decimal WorkInProcessValue { get; set; }
    public decimal FinishedGoodsValue { get; set; }
    public decimal CustomerDebts { get; set; }
    public decimal CashInBoxes { get; set; }
    public decimal CashWithReps { get; set; }
    public decimal EmployeeAdvances { get; set; }
    public decimal SupplierAdvances { get; set; }
    public decimal SupplierDebts { get; set; }
    public decimal CustomerDeposits { get; set; }
    public decimal NetAssets { get; set; }
    public int? PreviousReconciliationId { get; set; }
    public AssetReconciliation? PreviousReconciliation { get; set; }
    public decimal? PreviousNetAssets { get; set; }
    public decimal PartnerWithdrawalsSincePrevious { get; set; }
    /// <summary>إيداعات المالك في الصناديق منذ المطابقة السابقة — زيادة في النقد ليست ربحًا فتُطرح من الفائض.</summary>
    public decimal OwnerDepositsSincePrevious { get; set; }
    public decimal Surplus { get; set; }
    public bool IsBaseline { get; set; }
    public string? Notes { get; set; }
    public int? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }
    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<AssetReconciliationLine> Lines { get; set; } = new List<AssetReconciliationLine>();
}

public class AssetReconciliationLine
{
    public int Id { get; set; }
    public int ReconciliationId { get; set; }
    public AssetReconciliation Reconciliation { get; set; } = null!;
    public string Section { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal? Quantity { get; set; }
    public decimal? UnitValue { get; set; }
    public decimal Value { get; set; }
}
