using System.Text.Json;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ERP.Data.ProjectDb;

/// <summary>
/// سجل الحركات العام: كل إضافة أو تعديل أو حذف يمر عبر هذا السياق يُسجَّل تلقائيًا في AuditLogs
/// (المستخدم، الجدول، رقم السجل، والقيم قبل وبعد). العمليات التي تنفّذها الإجراءات المخزّنة
/// (ترحيل الفاتورة، إلغاؤها) تسجّلها خدماتها صراحةً عبر <see cref="Services.AuditService"/>.
/// </summary>
public partial class ProjectDbContext
{
    /// <summary>المستخدم الذي تُنسب إليه التغييرات. تضبطه الجلسة عند إنشاء السياق.</summary>
    public int? AuditUserId { get; set; }

    /// <summary>جداول تفصيلية أو آلية تتبع مستندًا أبًا مُسجَّلًا أصلًا — تسجيلها يغرق السجل بلا فائدة.</summary>
    private static readonly HashSet<Type> NotAudited = new()
    {
        typeof(AuditLog), typeof(JournalEntryLine), typeof(StockTransaction), typeof(PaymentAllocation),
        typeof(ProductionOrderConsumption), typeof(EmployeeDeductionInstallment), typeof(PayrollLine),
        typeof(LegacyImportMapEntry), typeof(SyncConflict),
    };

    private static readonly string[] DescribeProps =
    {
        "InvoiceNumber", "VoucherNumber", "EntryNumber", "TxNumber", "DocumentNumber", "ReceiptNumber", "OrderNumber",
        "ItemName", "FullName", "Name", "AccountName", "Username", "LevelName", "BatchNumber",
    };

    private bool _writingAudit;

    private sealed record PendingAudit(EntityEntry Entry, string Action, string Table, string? Summary, string? Changes, string? KeyBefore);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        if (_writingAudit) return base.SaveChanges(acceptAllChangesOnSuccess);
        var pending = CaptureAudit();
        var result = base.SaveChanges(acceptAllChangesOnSuccess);
        if (pending.Count > 0)
        {
            AddAuditRows(pending);
            _writingAudit = true;
            try { base.SaveChanges(acceptAllChangesOnSuccess); } finally { _writingAudit = false; }
        }
        return result;
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        if (_writingAudit) return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        var pending = CaptureAudit();
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        if (pending.Count > 0)
        {
            AddAuditRows(pending);
            _writingAudit = true;
            try { await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken); } finally { _writingAudit = false; }
        }
        return result;
    }

    private List<PendingAudit> CaptureAudit()
    {
        var list = new List<PendingAudit>();
        foreach (var e in ChangeTracker.Entries())
        {
            if (e.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            if (NotAudited.Contains(e.Metadata.ClrType) || e.Metadata.IsOwned()) continue;

            var table = e.Metadata.GetTableName() ?? e.Metadata.ClrType.Name;
            string action;
            Dictionary<string, object?[]>? changes = null;
            switch (e.State)
            {
                case EntityState.Added:
                    action = "Insert";
                    break;
                case EntityState.Deleted:
                    action = "Delete";
                    changes = e.Properties.Where(p => !p.Metadata.IsPrimaryKey() && !IsSecret(p.Metadata.Name))
                                          .ToDictionary(p => p.Metadata.Name, p => new[] { Plain(p.OriginalValue), null });
                    break;
                default:
                    changes = e.Properties
                        .Where(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue) && !p.Metadata.IsConcurrencyToken)
                        .ToDictionary(p => p.Metadata.Name,
                                      p => IsSecret(p.Metadata.Name) ? new object?[] { "***", "***" } : new[] { Plain(p.OriginalValue), Plain(p.CurrentValue) });
                    if (changes.Count == 0) continue;
                    // تعليم السجل "ملغى" يُسجَّل إلغاءً لا تعديلًا عاديًا
                    action = changes.ContainsKey("IsVoided") && Equals(e.CurrentValues["IsVoided"], true) ? "Void" : "Update";
                    break;
            }
            var json = changes is null ? null : JsonSerializer.Serialize(changes, JsonOpts);
            if (json is { Length: > 3800 }) json = json[..3800] + "…";
            list.Add(new PendingAudit(e, action, table, Describe(e), json, e.State == EntityState.Added ? null : KeyOf(e)));
        }
        return list;
    }

    private void AddAuditRows(List<PendingAudit> pending)
    {
        foreach (var p in pending)
            AuditLogs.Add(new AuditLog
            {
                UserId = AuditUserId,
                Action = p.Action,
                TableName = p.Table,
                RecordId = p.KeyBefore ?? KeyOf(p.Entry),
                Summary = p.Summary,
                Changes = p.Changes,
            });
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static bool IsSecret(string name) => name.Contains("Password", StringComparison.OrdinalIgnoreCase);

    private static object? Plain(object? v) => v switch
    {
        null => null,
        DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd") : d.ToString("yyyy-MM-dd HH:mm"),
        Enum en => en.ToString(),
        byte[] => "(بيانات ثنائية)",
        _ => v,
    };

    private static string? KeyOf(EntityEntry e)
    {
        var key = e.Metadata.FindPrimaryKey();
        if (key is null) return null;
        return string.Join(",", key.Properties.Select(p => e.Property(p.Name).CurrentValue?.ToString()));
    }

    private static string? Describe(EntityEntry e)
    {
        foreach (var name in DescribeProps)
        {
            var prop = e.Metadata.FindProperty(name);
            if (prop is null) continue;
            var value = (e.State == EntityState.Deleted ? e.Property(name).OriginalValue : e.Property(name).CurrentValue)?.ToString();
            if (!string.IsNullOrWhiteSpace(value)) return value.Length > 380 ? value[..380] : value;
        }
        return null;
    }
}
