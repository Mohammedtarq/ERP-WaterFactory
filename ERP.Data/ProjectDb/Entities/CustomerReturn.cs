namespace ERP.Data.ProjectDb.Entities;

/// <summary>Debt = تُخصم القيمة من دين الزبون، Cash = تُرد نقدًا من محفظة المندوب.</summary>
public enum CustomerReturnSettlement { Debt, Cash }

/// <summary>مرتجع زبون (39_customer_returns.sql): بضاعة اشتراها وأعادها، بسعر آخر بيع له.</summary>
public class CustomerReturn
{
    public int Id { get; set; }
    public string ReturnNumber { get; set; } = "";
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    /// <summary>يعود إليه السليم: سيارة المندوب أو مخزن المنتج التام.</summary>
    public int WarehouseId { get; set; }
    public Warehouse Warehouse { get; set; } = null!;
    public int? RepEmployeeId { get; set; }
    public Employee? RepEmployee { get; set; }
    public DateTime ReturnDate { get; set; }
    public CustomerReturnSettlement Settlement { get; set; }
    public decimal TotalAmount { get; set; }
    public string Reason { get; set; } = "";
    public int? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }
    public int? VoucherId { get; set; }
    public Voucher? Voucher { get; set; }
    public int? RepRequestId { get; set; }
    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<CustomerReturnLine> Lines { get; set; } = new();
}

public class CustomerReturnLine
{
    public int Id { get; set; }
    public int CustomerReturnId { get; set; }
    public CustomerReturn CustomerReturn { get; set; } = null!;
    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;
    public int PackagingLevelId { get; set; }
    public ItemPackagingLevel PackagingLevel { get; set; } = null!;
    public decimal QuantityInLevel { get; set; }
    /// <summary>من الكمية: ما عاد تالفًا.</summary>
    public decimal DamagedInLevel { get; set; }
    public decimal QuantityBaseUnits { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
    public int? BatchId { get; set; }
    public ItemBatch? Batch { get; set; }
}
