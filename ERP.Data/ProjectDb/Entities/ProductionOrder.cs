namespace ERP.Data.ProjectDb.Entities;

public enum ProductionOrderStatus { Draft, InProgress, Completed, Cancelled }

public class ProductionOrder
{
    public int Id { get; set; }
    public string MONumber { get; set; } = string.Empty;

    public int FinishedItemId { get; set; }
    public Item FinishedItem { get; set; } = null!;

    public int BOMId { get; set; }
    public BillOfMaterials BOM { get; set; } = null!;

    public int? CustomRecipeId { get; set; }   // NULL = الوصفة الأساسية فقط
    public CustomRecipe? CustomRecipe { get; set; }

    public decimal QuantityToProduce { get; set; }

    public int RawMaterialsWarehouseId { get; set; }   // WarehouseType = RawMaterial
    public Warehouse RawMaterialsWarehouse { get; set; } = null!;

    /// <summary>الماكينة التي يُصرف لها ويُنتج عليها (فارغ في الأوامر القديمة قبل 15_machines_wip.sql).</summary>
    public int? MachineId { get; set; }
    public Machine? Machine { get; set; }

    public int? OutputBatchId { get; set; }
    public ItemBatch? OutputBatch { get; set; }

    public ProductionOrderStatus Status { get; set; } = ProductionOrderStatus.Draft;

    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;

    public ICollection<ProductionOrderConsumption> Consumptions { get; set; } = new List<ProductionOrderConsumption>();

    /// <summary>
    /// أصناف الأمر (17_production_order_lines.sql). رأس الأمر يحمل بيانات السطر الأول للتوافق،
    /// وكمية الرأس = مجموع كميات السطور.
    /// </summary>
    public ICollection<ProductionOrderLine> Lines { get; set; } = new List<ProductionOrderLine>();
}

/// <summary>سطر أمر إنتاج: صنف نهائي بوصفته وكميته ودفعته الخاصة (الفحص والتعبئة لكل سطر عبر دفعته).</summary>
public class ProductionOrderLine
{
    public int Id { get; set; }
    public int ProductionOrderId { get; set; }
    public ProductionOrder ProductionOrder { get; set; } = null!;
    public int LineNo { get; set; }
    public int FinishedItemId { get; set; }
    public Item FinishedItem { get; set; } = null!;
    public int BOMId { get; set; }
    public BillOfMaterials BOM { get; set; } = null!;
    public int? CustomRecipeId { get; set; }
    public CustomRecipe? CustomRecipe { get; set; }
    public decimal QuantityToProduce { get; set; }
    public int? OutputBatchId { get; set; }
    public ItemBatch? OutputBatch { get; set; }
}

/// <summary>تفصيل الاستهلاك الفعلي لكل مادة أولية (BOM + الوصفة المخصصة مدموجَين).</summary>
public class ProductionOrderConsumption
{
    public int Id { get; set; }

    public int ProductionOrderId { get; set; }
    public ProductionOrder ProductionOrder { get; set; } = null!;

    public int RawMaterialItemId { get; set; }
    public Item RawMaterialItem { get; set; } = null!;

    /// <summary>سطر الأمر (الصنف) الذي تخصه هذه المادة — كل صنف له مكوناته منفصلة.</summary>
    public int? ProductionOrderLineId { get; set; }
    public ProductionOrderLine? Line { get; set; }

    public decimal QuantityRequired { get; set; }
    public decimal QuantityConsumed { get; set; }
}
