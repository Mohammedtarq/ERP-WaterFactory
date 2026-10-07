using Microsoft.EntityFrameworkCore;

namespace ERP.Cloud.Api;

/// <summary>جهاز مندوب كما أرسلته خدمة المزامنة: بصمة المفتاح فقط، والإيقاف في المعمل يصل هنا في الدورة التالية.</summary>
public class RelayDevice
{
    /// <summary>رقم الجهاز في قاعدة المعمل.</summary>
    public int Id { get; set; }
    public int RepEmployeeId { get; set; }
    public string KeyHash { get; set; } = "";
    public bool IsActive { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
}

/// <summary>حركة من الهاتف تنتظر المعمل (Waiting) أو عادت نتيجتها (Done).</summary>
public class RelayRequest
{
    public long Id { get; set; }
    public Guid ClientId { get; set; }
    public int DeviceId { get; set; }
    public int RepEmployeeId { get; set; }
    public string Kind { get; set; } = "";
    public DateTime OccurredAt { get; set; }
    public string Payload { get; set; } = "";
    public byte[]? Photo { get; set; }
    public DateTime ReceivedAt { get; set; }
    public string State { get; set; } = "";
    public bool? Accepted { get; set; }
    public string? ResultStatus { get; set; }
    public string? ResultMessage { get; set; }
    public int? ResultId { get; set; }
    public string? Warning { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>آخر «نسخة عمل» لكل مندوب (JSON كما بناها المعمل).</summary>
public class RelaySnapshot
{
    public int RepEmployeeId { get; set; }
    public string Hash { get; set; } = "";
    public string Json { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}

/// <summary>سطر واحد: آخر اتصال لخدمة المزامنة.</summary>
public class RelayAgentState
{
    public int Id { get; set; } = 1;
    public DateTime LastSeenAt { get; set; }
}

public class RelayDb : DbContext
{
    public RelayDb(DbContextOptions<RelayDb> options) : base(options) { }

    public DbSet<RelayDevice> Devices => Set<RelayDevice>();
    public DbSet<RelayRequest> Requests => Set<RelayRequest>();
    public DbSet<RelaySnapshot> Snapshots => Set<RelaySnapshot>();
    public DbSet<RelayAgentState> AgentState => Set<RelayAgentState>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<RelayDevice>(e =>
        {
            e.ToTable("Devices");
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.KeyHash).HasMaxLength(64).IsUnicode(false);
            e.HasIndex(x => x.KeyHash).IsUnique();
        });
        b.Entity<RelayRequest>(e =>
        {
            e.ToTable("Requests");
            e.HasIndex(x => x.ClientId).IsUnique();
            e.HasIndex(x => new { x.State, x.Id });
            e.HasIndex(x => new { x.DeviceId, x.UpdatedAt });
            e.Property(x => x.Kind).HasMaxLength(20).IsUnicode(false);
            e.Property(x => x.State).HasMaxLength(10).IsUnicode(false);
            e.Property(x => x.ResultStatus).HasMaxLength(10).IsUnicode(false);
            e.Property(x => x.ResultMessage).HasMaxLength(500);
            e.Property(x => x.Warning).HasMaxLength(300);
        });
        b.Entity<RelaySnapshot>(e =>
        {
            e.ToTable("Snapshots");
            e.HasKey(x => x.RepEmployeeId);
            e.Property(x => x.RepEmployeeId).ValueGeneratedNever();
            e.Property(x => x.Hash).HasMaxLength(64).IsUnicode(false);
        });
        b.Entity<RelayAgentState>(e =>
        {
            e.ToTable("AgentState");
            e.Property(x => x.Id).ValueGeneratedNever();
        });
    }
}
