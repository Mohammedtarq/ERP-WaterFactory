using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>سطر قالب عند الحفظ: الدور، المادة الافتراضية (اختياري)، والنسبة "عدد لكل عدد".</summary>
public record TemplateLineInput(string Role, int? DefaultItemId, decimal ComponentQuantity, decimal PerUnits);

/// <summary>
/// قوالب التعبئة والوصفات المخصصة:
/// القالب يملأ قائمة مواد الصنف (الصنف يُعرَّف مرة واحدة بوصفة ثابتة)، وبديل العميل يستبدل مكوّن دور معيّن
/// بصنف مخزني مستقل، والاستبدال على مستوى أمر واحد يُسجَّل بسببه دون تغيير تعريف الصنف.
/// </summary>
public class PackagingTemplateService
{
    private readonly ProjectDbContext _db;

    public PackagingTemplateService(ProjectDbContext db) => _db = db;

    public Task<List<PackagingTemplate>> GetAllAsync(bool activeOnly = false) =>
        _db.PackagingTemplates.AsNoTracking().Include(t => t.Lines).ThenInclude(l => l.DefaultItem)
           .Where(t => !activeOnly || t.IsActive).OrderBy(t => t.Name).ToListAsync();

    public async Task<(FinanceOperationResult result, int? templateId)> SaveTemplateAsync(
        int? id, string name, string? description, IReadOnlyList<TemplateLineInput> lines, bool isActive = true)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return (FinanceOperationResult.Fail("أدخل اسم القالب (مثل 330×40 كارتون)"), null);
        if (lines.Count == 0) return (FinanceOperationResult.Fail("أضف مكوّنًا واحدًا على الأقل للقالب"), null);
        if (lines.Any(l => string.IsNullOrWhiteSpace(l.Role))) return (FinanceOperationResult.Fail("اكتب دور كل مكوّن (كارتون، غطاء، لاصق...)"), null);
        if (lines.Any(l => l.ComponentQuantity <= 0 || l.PerUnits <= 0)) return (FinanceOperationResult.Fail("النسبة يجب أن تكون أكبر من صفر في كل سطر"), null);
        if (lines.GroupBy(l => l.Role.Trim()).Any(g => g.Count() > 1)) return (FinanceOperationResult.Fail("الدور مكرر في القالب"), null);
        var self = id ?? 0;
        if (await _db.PackagingTemplates.AnyAsync(t => t.Name == name && t.Id != self)) return (FinanceOperationResult.Fail("يوجد قالب بنفس الاسم"), null);

        PackagingTemplate template;
        if (id is int existing)
        {
            template = await _db.PackagingTemplates.Include(t => t.Lines).FirstOrDefaultAsync(t => t.Id == existing)
                       ?? throw new InvalidOperationException("القالب غير موجود");
            _db.PackagingTemplateLines.RemoveRange(template.Lines);
        }
        else
            _db.PackagingTemplates.Add(template = new PackagingTemplate());
        template.Name = name;
        template.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        template.IsActive = isActive;
        foreach (var l in lines)
            template.Lines.Add(new PackagingTemplateLine { ComponentRole = l.Role.Trim(), DefaultItemId = l.DefaultItemId, ComponentQuantity = l.ComponentQuantity, PerUnits = l.PerUnits });
        await _db.SaveChangesAsync();
        return (FinanceOperationResult.Ok(), template.Id);
    }

    /// <summary>
    /// تطبيق قالب على صنف: يُعاد ملء قائمة مواده الفعّالة من أدوار القالب (المادة المختارة لكل دور أو الافتراضية)
    /// بنسبها لكل وحدة. الأوامر السابقة لا تتأثر (تحمل نسختها من المكونات).
    /// </summary>
    public async Task<FinanceOperationResult> ApplyToItemAsync(int finishedItemId, int templateId, IReadOnlyDictionary<string, int>? choices = null)
    {
        var template = await _db.PackagingTemplates.Include(t => t.Lines).FirstOrDefaultAsync(t => t.Id == templateId);
        if (template is null) return FinanceOperationResult.Fail("القالب غير موجود");
        var item = await _db.Items.FindAsync(finishedItemId);
        if (item is null) return FinanceOperationResult.Fail("اختر الصنف النهائي");

        var resolved = new List<(PackagingTemplateLine line, int itemId)>();
        foreach (var l in template.Lines)
        {
            var chosen = choices is not null && choices.TryGetValue(l.ComponentRole, out var c) ? c : l.DefaultItemId;
            if (chosen is null) return FinanceOperationResult.Fail($"اختر المادة لدور \"{l.ComponentRole}\"");
            if (chosen == finishedItemId) return FinanceOperationResult.Fail("المنتج لا يمكن أن يكون مكوّنًا لنفسه");
            resolved.Add((l, chosen.Value));
        }
        if (resolved.GroupBy(r => r.itemId).Any(g => g.Count() > 1))
            return FinanceOperationResult.Fail("نفس المادة مختارة لدورين — اختر مادة مختلفة لكل دور");

        var bom = await _db.BillOfMaterials.Include(b => b.Lines).FirstOrDefaultAsync(b => b.FinishedItemId == finishedItemId && b.IsActive);
        if (bom is null) _db.BillOfMaterials.Add(bom = new BillOfMaterials { FinishedItemId = finishedItemId });
        else _db.BOMLines.RemoveRange(bom.Lines);
        bom.PackagingTemplateId = template.Id;
        foreach (var (l, itemId) in resolved)
            bom.Lines.Add(new BOMLine { RawMaterialItemId = itemId, QuantityPerUnit = l.QuantityPerUnit, ComponentRole = l.ComponentRole });
        await _db.SaveChangesAsync();
        return FinanceOperationResult.Ok();
    }

    /// <summary>أدوار قائمة مواد الصنف (لاختيار بدائل العميل): الدور، المادة الأساسية، والكمية لكل وحدة.</summary>
    public Task<List<BOMLine>> GetRolesAsync(int finishedItemId) =>
        _db.BOMLines.AsNoTracking().Include(l => l.RawMaterialItem)
           .Where(l => l.BOM.FinishedItemId == finishedItemId && l.BOM.IsActive && l.ComponentRole != null)
           .OrderBy(l => l.ComponentRole).ToListAsync();

    /// <summary>
    /// بديل العميل لدور: يضيف/يحدّث سطرًا في الوصفة المخصصة يستبدل مكوّن ذلك الدور بصنف مخزني مستقل
    /// (بنفس الكمية لكل وحدة)، دون المساس بالوصفة الأساسية للصنف.
    /// </summary>
    public async Task<FinanceOperationResult> SetCustomerVariantAsync(int customRecipeId, string role, int variantItemId)
    {
        var recipe = await _db.CustomRecipes.Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == customRecipeId);
        if (recipe is null) return FinanceOperationResult.Fail("اختر الوصفة المخصصة");
        var baseLine = await _db.BOMLines.FirstOrDefaultAsync(l => l.BOM.FinishedItemId == recipe.FinishedItemId && l.BOM.IsActive && l.ComponentRole == role);
        if (baseLine is null) return FinanceOperationResult.Fail($"لا يوجد دور \"{role}\" في قائمة مواد الصنف — طبّق قالب التعبئة أولًا");
        if (variantItemId == baseLine.RawMaterialItemId) return FinanceOperationResult.Fail("البديل هو نفس المادة الأساسية");
        var existing = recipe.Lines.FirstOrDefault(l => l.ReplacesRawMaterialItemId == baseLine.RawMaterialItemId);
        if (existing is null)
            recipe.Lines.Add(existing = new CustomRecipeLine { ReplacesRawMaterialItemId = baseLine.RawMaterialItemId });
        existing.ComponentItemId = variantItemId;
        existing.ComponentLabel = role;
        existing.QuantityPerUnit = baseLine.QuantityPerUnit;
        await _db.SaveChangesAsync();
        return FinanceOperationResult.Ok();
    }

    /// <summary>
    /// استبدال مكوّن في أمر إنتاج واحد (مسودة) — مثل غطاء بلون آخر لنفاد اللون الأساسي. يُسجَّل بسببه ومن نفّذه،
    /// ولا يغيّر تعريف الصنف ولا وصفته.
    /// </summary>
    public async Task<FinanceOperationResult> OverrideOrderComponentAsync(int orderLineId, int originalItemId, int replacementItemId, string reason, int userId)
    {
        if (string.IsNullOrWhiteSpace(reason)) return FinanceOperationResult.Fail("سبب الاستبدال إلزامي");
        if (originalItemId == replacementItemId) return FinanceOperationResult.Fail("البديل هو نفس المكوّن");
        var line = await _db.ProductionOrderLines.Include(l => l.ProductionOrder).ThenInclude(o => o.Consumptions).FirstOrDefaultAsync(l => l.Id == orderLineId);
        if (line is null) return FinanceOperationResult.Fail("سطر الأمر غير موجود");
        if (line.ProductionOrder.Status != ProductionOrderStatus.Draft)
            return FinanceOperationResult.Fail("الاستبدال يكون قبل بدء التشغيل (الأمر مسودة) — بعد الصرف استخدم الصرف الإضافي أو الإرجاع");
        if (line.FinishedItemId == replacementItemId) return FinanceOperationResult.Fail("المنتج لا يمكن أن يكون مكوّنًا لنفسه");
        if (await _db.Items.FindAsync(replacementItemId) is null) return FinanceOperationResult.Fail("اختر المادة البديلة");

        var consumption = line.ProductionOrder.Consumptions.FirstOrDefault(c => c.ProductionOrderLineId == line.Id && c.RawMaterialItemId == originalItemId);
        if (consumption is null) return FinanceOperationResult.Fail("المكوّن ليس من مكونات هذا الصنف في الأمر");
        var quantity = consumption.QuantityRequired;
        var merge = line.ProductionOrder.Consumptions.FirstOrDefault(c => c.ProductionOrderLineId == line.Id && c.RawMaterialItemId == replacementItemId);
        if (merge is not null)
        {
            merge.QuantityRequired += quantity;
            _db.ProductionOrderConsumptions.Remove(consumption);
        }
        else consumption.RawMaterialItemId = replacementItemId;

        _db.ProductionOrderComponentOverrides.Add(new ProductionOrderComponentOverride
        {
            ProductionOrderLineId = line.Id, OriginalItemId = originalItemId, ReplacementItemId = replacementItemId,
            Quantity = quantity, Reason = reason.Trim(), ChangedByUserId = userId
        });
        await _db.SaveChangesAsync();
        return FinanceOperationResult.Ok();
    }

    public Task<List<ProductionOrderComponentOverride>> GetOrderOverridesAsync(int orderId) =>
        _db.ProductionOrderComponentOverrides.AsNoTracking()
           .Include(o => o.OriginalItem).Include(o => o.ReplacementItem).Include(o => o.ChangedByUser).Include(o => o.Line).ThenInclude(l => l.FinishedItem)
           .Where(o => o.Line.ProductionOrderId == orderId).OrderBy(o => o.Id).ToListAsync();
}
