namespace ERP.Data.ProjectDb.Entities;

/// <summary>بيع نقدي لمواد تالفة (من مخزن التالف) لجهة، بمستند إخراج مخزني وقيد إيراد.</summary>
public class DamagedSale
{
    public int Id { get; set; }
    public string SaleNumber { get; set; } = "";
    public DateTime SaleDate { get; set; } = DateTime.Today;
    public string BuyerName { get; set; } = "";
    public int StockDocumentId { get; set; }
    public StockDocument StockDocument { get; set; } = null!;
    public decimal TotalAmount { get; set; }
    public string? Notes { get; set; }
    public int? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }
    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<DamagedSaleLine> Lines { get; set; } = new List<DamagedSaleLine>();
}

public class DamagedSaleLine
{
    public int Id { get; set; }
    public int DamagedSaleId { get; set; }
    public DamagedSale DamagedSale { get; set; } = null!;
    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;
    /// <summary>بالوحدة الأساسية.</summary>
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Amount { get; set; }
}
