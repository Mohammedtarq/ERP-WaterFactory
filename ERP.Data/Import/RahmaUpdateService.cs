using System.Globalization;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Import;

public enum RahmaUpdateAction { New, Changed, Same, SkippedByName }

/// <summary>سطر في تقرير «تحديث من الرحمة»: ما سيحدث لعنصر واحد.</summary>
public record RahmaUpdateRow(string Section, string Name, RahmaUpdateAction Action, string Details);

public class RahmaUpdatePreview
{
    public string? Error { get; init; }
    public List<RahmaUpdateRow> Rows { get; init; } = new();
    public List<string> Notes { get; init; } = new();
    public int Count(string section, RahmaUpdateAction a) => Rows.Count(r => r.Section == section && r.Action == a);
    public int ToApply => Rows.Count(r => r.Action is RahmaUpdateAction.New or RahmaUpdateAction.Changed);
    public IEnumerable<string> Sections => Rows.Select(r => r.Section).Distinct();
}

public class RahmaUpdateResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string Summary { get; init; } = "";
    public int RunId { get; init; }
    public List<string> Conflicts { get; init; } = new();
}

/// <summary>
/// «تحديث من الرحمة» (الرحمة هي المصدر الأساسي، والنظام الجديد يتحدث منها بزر): يقارن خطة محلَّلة من نسخة قاعدة الرحمة
/// بالبيانات الأساسية المنقولة سابقًا (بالمعرّفات القديمة المحفوظة عند النقل الأول) فيضيف الجديد ويحدّث المتغيّر ولا يكرر شيئًا.
/// <para>يشمل: العملاء والموردين (الاسم/الهاتف/العنوان) وأسعار المنتجات. الأرصدة (مخزون، ذمم، صناديق) لا يغيّرها هذا التحديث:
/// تُعرض أرقام الرحمة للمقارنة فقط، لأنها تُبنى في النظام الجديد من مستندات ومعاملات.</para>
/// <para>كل تغيير يُسجَّل بقيمته القديمة والجديدة، ويمكن التراجع عن <b>آخر</b> تحديث. للأدمن فقط.</para>
/// </summary>
public class RahmaUpdateService
{
    private readonly ProjectDbContext _db;
    public RahmaUpdateService(ProjectDbContext db) => _db = db;

    public const string CustomerType = "Customer", SupplierType = "Supplier", PriceType = "ProductPrice";

    private sealed record Change(string Section, string EntityType, string LegacyKey, string Name, RahmaUpdateAction Action, int? EntityId,
                                 List<(string field, string? oldValue, string? newValue)> Fields, Action<Customer>? NewCustomer = null);

    private static string? Clean(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > max ? s.Trim()[..max] : s.Trim();
    private static string Show(string? v) => string.IsNullOrEmpty(v) ? "(فارغ)" : v;
    private static string Norm(string? s) => RahmaLegacyReader.Normalize(s);

    private async Task<(List<Change>? changes, List<string> notes, string? error)> BuildAsync(RahmaImportPlan plan)
    {
        var notes = new List<string>();
        var import = await _db.LegacyImports.AsNoTracking().FirstOrDefaultAsync(i => i.SourceSystem == RahmaImporter.SourceSystem);
        if (import is null) return (null, notes, "لم يُنفَّذ «النقل الأول» من الرحمة في هذا المشروع بعد، فلا يوجد ما نحدّثه. نفّذ النقل الأول أولًا.");
        var map = await _db.LegacyImportMap.AsNoTracking().Where(m => m.ImportId == import.Id).ToListAsync();
        int? Mapped(string type, object key) => map.FirstOrDefault(m => m.EntityType == type && m.LegacyKey == key.ToString())?.NewId;

        var customers = await _db.Customers.AsNoTracking().ToListAsync();
        var suppliers = await _db.Suppliers.AsNoTracking().ToListAsync();
        var items = await _db.Items.AsNoTracking().ToListAsync();
        var mappedCustomerIds = map.Where(m => m.EntityType == "Customer").Select(m => m.NewId).ToHashSet();
        var mappedSupplierIds = map.Where(m => m.EntityType == "Supplier").Select(m => m.NewId).ToHashSet();
        var changes = new List<Change>();

        // ---------- العملاء ----------
        foreach (var c in plan.Customers.OrderBy(c => c.Name))
        {
            var name = Clean(c.Name, 150) ?? "";
            var phone = Clean(c.Phone, 30); var address = Clean(c.Address, 300);
            if (Mapped("Customer", c.LegacyId) is int id && customers.FirstOrDefault(x => x.Id == id) is { } cur)
            {
                var f = new List<(string, string?, string?)>();
                if (cur.Name != name) f.Add(("الاسم", cur.Name, name));
                if ((Clean(cur.Phone, 30) ?? "") != (phone ?? "")) f.Add(("الهاتف", cur.Phone, phone));
                if ((Clean(cur.Address, 300) ?? "") != (address ?? "")) f.Add(("العنوان", cur.Address, address));
                changes.Add(new Change("العملاء", CustomerType, c.LegacyId.ToString(), name, f.Count == 0 ? RahmaUpdateAction.Same : RahmaUpdateAction.Changed, id, f,
                    null));
            }
            else if (customers.Any(x => Norm(x.Name) == Norm(name) && !mappedCustomerIds.Contains(x.Id)))
                changes.Add(new Change("العملاء", CustomerType, c.LegacyId.ToString(), name, RahmaUpdateAction.SkippedByName, null, new()));
            else
                changes.Add(new Change("العملاء", CustomerType, c.LegacyId.ToString(), name, RahmaUpdateAction.New, null,
                    new() { ("الهاتف", null, phone), ("العنوان", null, address) },
                    x => { x.Name = name; x.Phone = phone; x.Address = address; x.CustomerType = ProjectDb.Entities.CustomerType.Direct; }));
        }

        // ---------- الموردون ----------
        foreach (var s in plan.Suppliers.OrderBy(s => s.Name))
        {
            var name = Clean(s.Name, 150) ?? ""; var phone = Clean(s.Phone, 30);
            if (Mapped("Supplier", s.LegacyId) is int id && suppliers.FirstOrDefault(x => x.Id == id) is { } cur)
            {
                var f = new List<(string, string?, string?)>();
                if (cur.Name != name) f.Add(("الاسم", cur.Name, name));
                if ((Clean(cur.Phone, 30) ?? "") != (phone ?? "")) f.Add(("الهاتف", cur.Phone, phone));
                changes.Add(new Change("الموردون", SupplierType, s.LegacyId.ToString(), name, f.Count == 0 ? RahmaUpdateAction.Same : RahmaUpdateAction.Changed, id, f));
            }
            else if (suppliers.Any(x => Norm(x.Name) == Norm(name) && !mappedSupplierIds.Contains(x.Id)))
                changes.Add(new Change("الموردون", SupplierType, s.LegacyId.ToString(), name, RahmaUpdateAction.SkippedByName, null, new()));
            else
                changes.Add(new Change("الموردون", SupplierType, s.LegacyId.ToString(), name, RahmaUpdateAction.New, null, new() { ("الهاتف", null, phone) }));
        }

        // ---------- أسعار المنتجات (سعر القطعة) ----------
        foreach (var p in plan.Products.OrderBy(p => p.NewName))
        {
            if (Mapped("Product", p.LegacyId) is not int id || items.FirstOrDefault(x => x.Id == id) is not { } item) continue;
            var price = Math.Round(p.UnitPrice, 2);
            var same = Math.Round(item.SalePrice, 2) == price;
            changes.Add(new Change("أسعار المنتجات", PriceType, p.LegacyId.ToString(), item.ItemName, same ? RahmaUpdateAction.Same : RahmaUpdateAction.Changed, id,
                same ? new() : new() { ("سعر القطعة", item.SalePrice.ToString("0.##", CultureInfo.InvariantCulture), price.ToString("0.##", CultureInfo.InvariantCulture)) }));
        }

        notes.Add($"أرصدة الرحمة للمقارنة فقط (لا يغيّرها هذا التحديث): ديون العملاء {plan.CustomersDebt:N0}، ذمم الموردين {plan.SuppliersDebt:N0}، " +
                  $"{plan.RawMaterials.Count:N0} صنف مواد أولية، {plan.FinishedStock.Sum(f => f.Packs):N0} عبوة منتج تام، {plan.CashBoxes.Count} صندوق.");
        if (plan.Products.Any(p => Mapped("Product", p.LegacyId) is null))
            notes.Add("منتجات جديدة في الرحمة غير منقولة: تُضاف يدويًا من «المخازن» (إضافة منتج جديد يحتاج وصفة ومواد).");
        return (changes, notes, null);
    }

    /// <summary>تقرير بما سيحدث دون أي كتابة.</summary>
    public async Task<RahmaUpdatePreview> PreviewAsync(RahmaImportPlan plan)
    {
        var (changes, notes, error) = await BuildAsync(plan);
        if (error is not null) return new RahmaUpdatePreview { Error = error };
        return new RahmaUpdatePreview
        {
            Notes = notes,
            Rows = changes!.Select(c => new RahmaUpdateRow(c.Section, c.Name, c.Action,
                c.Action == RahmaUpdateAction.New ? "جديد" :
                c.Action == RahmaUpdateAction.SkippedByName ? "موجود بالاسم في النظام الجديد — يُتخطّى (لا تكرار)" :
                c.Action == RahmaUpdateAction.Same ? "بلا تغيير" :
                string.Join("؛ ", c.Fields.Select(f => $"{f.field}: {Show(f.oldValue)} ← {Show(f.newValue)}")))).ToList()
        };
    }

    /// <summary>
    /// يطبّق التحديث في معاملة واحدة. <paramref name="preBackup"/> (اختياري) ينفّذ نسخة احتياطية قبل أي تغيير ويعيد مسارها؛
    /// إن فشل تُلغى العملية ولا يُمس شيء.
    /// </summary>
    public async Task<RahmaUpdateResult> ApplyAsync(RahmaImportPlan plan, int userId, Func<Task<string?>>? preBackup = null)
    {
        if (!await new CashBoxService(_db).IsAdminAsync(userId)) return Fail("التحديث من الرحمة للأدمن فقط");
        var (changes, _, error) = await BuildAsync(plan);
        if (error is not null) return Fail(error);
        var todo = changes!.Where(c => c.Action is RahmaUpdateAction.New or RahmaUpdateAction.Changed).ToList();
        if (todo.Count == 0) return Fail("لا يوجد ما يُحدَّث: النظام الجديد مطابق للرحمة في البيانات الأساسية");

        string? backupPath = null;
        if (preBackup is not null)
        {
            try { backupPath = await preBackup(); }
            catch (Exception ex) { return Fail("تعذّرت النسخة الاحتياطية قبل التحديث فلم يُغيَّر شيء: " + ex.Message); }
        }

        var import = await _db.LegacyImports.AsNoTracking().FirstAsync(i => i.SourceSystem == RahmaImporter.SourceSystem);
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync();
            try
            {
                var run = new LegacyUpdateRun { SourceDatabase = plan.SourceDatabase, AppliedByUserId = userId, PreBackupPath = backupPath };
                _db.LegacyUpdateRuns.Add(run);
                await _db.SaveChangesAsync();
                var log = new List<LegacyUpdateChange>();

                foreach (var c in todo)
                {
                    if (c.Action == RahmaUpdateAction.New)
                    {
                        int newId;
                        if (c.EntityType == CustomerType)
                        {
                            var e = new Customer(); c.NewCustomer!(e); _db.Customers.Add(e); await _db.SaveChangesAsync(); newId = e.Id;
                        }
                        else
                        {
                            var e = new Supplier { Name = c.Name, Phone = c.Fields.FirstOrDefault(f => f.field == "الهاتف").newValue };
                            _db.Suppliers.Add(e); await _db.SaveChangesAsync(); newId = e.Id;
                        }
                        _db.LegacyImportMap.Add(new LegacyImportMapEntry { ImportId = import.Id, EntityType = c.EntityType, LegacyKey = c.LegacyKey, NewId = newId });
                        log.Add(new LegacyUpdateChange { RunId = run.Id, EntityType = c.EntityType, EntityId = newId, LegacyKey = c.LegacyKey, Action = "Created", NewValue = c.Name });
                        continue;
                    }
                    foreach (var (field, oldV, newV) in c.Fields)
                    {
                        Set(c, field, newV);
                        log.Add(new LegacyUpdateChange { RunId = run.Id, EntityType = c.EntityType, EntityId = c.EntityId!.Value, LegacyKey = c.LegacyKey,
                                                         Action = "Updated", Field = field, OldValue = oldV, NewValue = newV });
                    }
                }
                _db.LegacyUpdateChanges.AddRange(log);
                var created = todo.Count(c => c.Action == RahmaUpdateAction.New);
                run.Summary = $"أُضيف {created:N0} وعُدّل {todo.Count - created:N0} (عملاء {todo.Count(c => c.EntityType == CustomerType)}، موردون {todo.Count(c => c.EntityType == SupplierType)}، أسعار {todo.Count(c => c.EntityType == PriceType)})";
                await _db.SaveChangesAsync();
                await tx.CommitAsync();
                var id = run.Id; var summary = run.Summary;
                _db.ChangeTracker.Clear();
                return new RahmaUpdateResult { Success = true, RunId = id, Summary = summary };
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                _db.ChangeTracker.Clear();
                return Fail("تعذّر التحديث ولم يُغيَّر شيء: " + (ex.InnerException?.Message ?? ex.Message));
            }
        });
    }

    // يعدّل كيانًا موجودًا (تتبّعه DbContext) بحسب الحقل
    private void Set(Change c, string field, string? value)
    {
        var id = c.EntityId!.Value;
        switch (c.EntityType)
        {
            case CustomerType:
            {
                var e = _db.Customers.First(x => x.Id == id);
                if (field == "الاسم") e.Name = value ?? e.Name; else if (field == "الهاتف") e.Phone = value; else if (field == "العنوان") e.Address = value;
                break;
            }
            case SupplierType:
            {
                var e = _db.Suppliers.First(x => x.Id == id);
                if (field == "الاسم") e.Name = value ?? e.Name; else if (field == "الهاتف") e.Phone = value;
                break;
            }
            case PriceType:
                _db.Items.First(x => x.Id == id).SalePrice = decimal.Parse(value!, CultureInfo.InvariantCulture);
                break;
        }
    }

    /// <summary>آخر تحديث لم يُلغَ بعد (أو null): الوحيد الذي يمكن التراجع عنه.</summary>
    public Task<LegacyUpdateRun?> LastActiveRunAsync() =>
        _db.LegacyUpdateRuns.AsNoTracking().Where(r => r.UndoneAt == null).OrderByDescending(r => r.Id).FirstOrDefaultAsync();

    public Task<List<LegacyUpdateRun>> HistoryAsync() =>
        _db.LegacyUpdateRuns.AsNoTracking().OrderByDescending(r => r.Id).Take(30).ToListAsync();

    /// <summary>
    /// يتراجع عن آخر تحديث: يعيد القيم القديمة لما عدّله، ويحذف ما أضافه (أو يعطّله إن استُعمل في مستندات).
    /// ما عدّله المستخدم بعد التحديث (قيمته الحالية ≠ ما كتبه التحديث) لا يُستبدل بصمت، بل يُذكر في التعارضات.
    /// </summary>
    public async Task<RahmaUpdateResult> UndoLastAsync(int userId, string? reason)
    {
        if (!await new CashBoxService(_db).IsAdminAsync(userId)) return Fail("التراجع عن التحديث للأدمن فقط");
        if (string.IsNullOrWhiteSpace(reason)) return Fail("اكتب سبب التراجع");
        var run = await _db.LegacyUpdateRuns.Include(r => r.Changes).Where(r => r.UndoneAt == null).OrderByDescending(r => r.Id).FirstOrDefaultAsync();
        if (run is null) return Fail("لا يوجد تحديث للتراجع عنه");
        var import = await _db.LegacyImports.AsNoTracking().FirstAsync(i => i.SourceSystem == RahmaImporter.SourceSystem);
        var conflicts = new List<string>();
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync();
            try
            {
                foreach (var ch in run.Changes.OrderByDescending(c => c.Id))
                {
                    if (ch.Action == "Updated")
                    {
                        if (!await RevertFieldAsync(ch)) conflicts.Add($"{Label(ch.EntityType)} «{await NameOfAsync(ch)}»: {ch.Field} عُدّل بعد التحديث فلم يُستبدل");
                        continue;
                    }
                    // Created: احذف الخريطة والكيان، أو عطّله إن كان مستعملًا
                    var map = await _db.LegacyImportMap.FirstOrDefaultAsync(m => m.ImportId == import.Id && m.EntityType == ch.EntityType && m.LegacyKey == ch.LegacyKey);
                    if (map is not null) _db.LegacyImportMap.Remove(map);
                    await _db.SaveChangesAsync();
                    if (!await TryRemoveAsync(ch)) conflicts.Add($"{Label(ch.EntityType)} «{ch.NewValue}» مستعمل في مستندات فعُطّل بدل حذفه");
                }
                // (التعطيل أعلاه قد يمسح متتبّع الكيانات) فنُعيد قراءة سجل التحديث قبل ختمه
                var stamp = await _db.LegacyUpdateRuns.FirstAsync(r => r.Id == run.Id);
                stamp.UndoneAt = DateTime.UtcNow; stamp.UndoneByUserId = userId; stamp.UndoReason = reason.Trim();
                await _db.SaveChangesAsync();
                await tx.CommitAsync();
                _db.ChangeTracker.Clear();
                return new RahmaUpdateResult { Success = true, RunId = run.Id, Summary = $"تم التراجع عن التحديث رقم {run.Id}", Conflicts = conflicts };
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                _db.ChangeTracker.Clear();
                return Fail("تعذّر التراجع ولم يُغيَّر شيء: " + (ex.InnerException?.Message ?? ex.Message));
            }
        });
    }

    private static string Label(string type) => type switch { CustomerType => "العميل", SupplierType => "المورد", _ => "المنتج" };

    private async Task<string> NameOfAsync(LegacyUpdateChange ch) => ch.EntityType switch
    {
        CustomerType => await _db.Customers.Where(x => x.Id == ch.EntityId).Select(x => x.Name).FirstOrDefaultAsync() ?? "?",
        SupplierType => await _db.Suppliers.Where(x => x.Id == ch.EntityId).Select(x => x.Name).FirstOrDefaultAsync() ?? "?",
        _ => await _db.Items.Where(x => x.Id == ch.EntityId).Select(x => x.ItemName).FirstOrDefaultAsync() ?? "?"
    };

    private async Task<bool> RevertFieldAsync(LegacyUpdateChange ch)
    {
        switch (ch.EntityType)
        {
            case CustomerType:
            {
                var e = await _db.Customers.FirstOrDefaultAsync(x => x.Id == ch.EntityId);
                if (e is null) return false;
                var cur = ch.Field == "الاسم" ? e.Name : ch.Field == "الهاتف" ? e.Phone : e.Address;
                if ((cur ?? "") != (ch.NewValue ?? "")) return false;
                if (ch.Field == "الاسم") e.Name = ch.OldValue ?? e.Name; else if (ch.Field == "الهاتف") e.Phone = ch.OldValue; else e.Address = ch.OldValue;
                break;
            }
            case SupplierType:
            {
                var e = await _db.Suppliers.FirstOrDefaultAsync(x => x.Id == ch.EntityId);
                if (e is null) return false;
                var cur = ch.Field == "الاسم" ? e.Name : e.Phone;
                if ((cur ?? "") != (ch.NewValue ?? "")) return false;
                if (ch.Field == "الاسم") e.Name = ch.OldValue ?? e.Name; else e.Phone = ch.OldValue;
                break;
            }
            default:
            {
                var e = await _db.Items.FirstOrDefaultAsync(x => x.Id == ch.EntityId);
                if (e is null || e.SalePrice.ToString("0.##", CultureInfo.InvariantCulture) != ch.NewValue) return false;
                e.SalePrice = decimal.Parse(ch.OldValue!, CultureInfo.InvariantCulture);
                break;
            }
        }
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>يحذف الكيان المُنشأ إن لم يُستعمل؛ وإلا يعطّله ويعيد false.</summary>
    private async Task<bool> TryRemoveAsync(LegacyUpdateChange ch)
    {
        try
        {
            if (ch.EntityType == CustomerType)
            {
                var e = await _db.Customers.FirstOrDefaultAsync(x => x.Id == ch.EntityId);
                if (e is null) return true;
                _db.Customers.Remove(e);
            }
            else
            {
                var e = await _db.Suppliers.FirstOrDefaultAsync(x => x.Id == ch.EntityId);
                if (e is null) return true;
                _db.Suppliers.Remove(e);
            }
            await _db.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            if (ch.EntityType == CustomerType) { var e = await _db.Customers.FirstOrDefaultAsync(x => x.Id == ch.EntityId); if (e is not null) e.IsActive = false; }
            else { var e = await _db.Suppliers.FirstOrDefaultAsync(x => x.Id == ch.EntityId); if (e is not null) e.IsActive = false; }
            await _db.SaveChangesAsync();
            return false;
        }
    }

    private static RahmaUpdateResult Fail(string message) => new() { Success = false, Error = message };
}
