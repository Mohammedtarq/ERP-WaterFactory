namespace ERP.Data.ProjectDb.Entities;

/// <summary>جرد فعلي لمخزن (29_production_stock.sql): الفرق يُسجَّل "فرق جرد" بالكلفة، منفصلًا عن التلف.</summary>
public class StockCount
{
    public int Id { get; set; }
    public string CountNumber { get; set; } = "";
    public int WarehouseId { get; set; }
    public Warehouse Warehouse { get; set; } = null!;
    public DateTime CountDate { get; set; } = DateTime.Today;
    public string? Notes { get; set; }
    public decimal ShortageValue { get; set; }
    public decimal SurplusValue { get; set; }
    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<StockCountLine> Lines { get; set; } = new();
}

public class StockCountLine
{
    public int Id { get; set; }
    public int StockCountId { get; set; }
    public StockCount StockCount { get; set; } = null!;
    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;
    public decimal SystemQuantity { get; set; }
    public decimal CountedQuantity { get; set; }
    /// <summary>كما عدّه الموظف: "3 باليت + 12 كرتون + 150 قطعة".</summary>
    public string? CountDetail { get; set; }
    public decimal? UnitCost { get; set; }
    public decimal VarianceValue { get; set; }
    public string? Notes { get; set; }
    public decimal Variance => CountedQuantity - SystemQuantity;
}

public enum BeneficiaryCategory { Government, Drivers, Partners, Staff, Reps, Other }

/// <summary>جهة ثابتة يُصرف لها مسحوب مجاني (شرطة، بلدية، سائقو التريلات...)، بتصنيف لتقرير شهري لكل جهة.</summary>
public class FreeIssueBeneficiary
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public BeneficiaryCategory Category { get; set; } = BeneficiaryCategory.Other;
    public bool IsActive { get; set; } = true;
}

/// <summary>ما بيع فوق الرصيد المسجّل (بانتظار الإنتاج): يُسوّى تلقائيًا عند تسجيل الإنتاج (09_sales_logic.sql).</summary>
public class PendingProductionShortage
{
    public int Id { get; set; }
    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;
    public int WarehouseId { get; set; }
    public Warehouse Warehouse { get; set; } = null!;
    public decimal Quantity { get; set; }
    public decimal SettledQuantity { get; set; }
    public int? SalesInvoiceId { get; set; }
    public SalesInvoice? SalesInvoice { get; set; }
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SettledAt { get; set; }
    public decimal Open => Quantity - SettledQuantity;
}
