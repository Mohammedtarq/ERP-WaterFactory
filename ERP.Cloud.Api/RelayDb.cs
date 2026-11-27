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
    /// <summary>هوية هذه القاعدة: عنوانان يصلانها (نفق إلى الخادم المحلي) تعاملهما خدمة المزامنة خادمًا واحدًا.</summary>
    public Guid? RelayId { get; set; }
}

/// <summary>آخر ملخص للمدير رفعته حاسبة المعمل (JSON جاهز) — سطر واحد.</summary>
public class RelayManagerSummary
{
    public int Id { get; set; } = 1;
    public string Json { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}

/// <summary>حساب مدير كما أرسلته حاسبة المعمل: بصمة كلمة المرور فقط.</summary>
public class RelayManagerAccount
{
    public string Username { get; set; } = "";
    public string Salt { get; set; } = "";
    public string Hash { get; set; } = "";
    public bool IsActive { get; set; }
    public DateTime UpdatedAt { get; set; }
    /// <summary>Manager أو Tv (شاشة التلفاز: بيانات غير حساسة فقط).</summary>
    public string Kind { get; set; } = "Manager";
}

/// <summary>جلسة دخول المدير: بصمة الرمز فقط (الرمز نفسه عند الهاتف)، وتنتهي بتاريخها أو بحذف الحساب.</summary>
public class RelayManagerSession
{
    public string TokenHash { get; set; } = "";
    public string Username { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public class RelayDb : DbContext
{
    public RelayDb(DbContextOptions<RelayDb> options) : base(options) { }

    /// <summary>
    /// إنشاء القاعدة أول تشغيل، وترقيات صغيرة آمنة للتكرار لقاعدة أُنشئت بإصدار أقدم (لا ترقيات يدوية).
    /// SQLite للخادم المحلي وللاستضافة الصغيرة، وSQL Server إن ضُبطت سلسلة اتصاله.
    /// </summary>
    public async Task PrepareAsync()
    {
        await Database.EnsureCreatedAsync();
        // قاعدة أُنشئت بإصدار أقدم (قبل لوحة المدير): جداول اللوحة تُضاف وحدها
        if (Database.IsSqlServer())
        {
            await Database.ExecuteSqlRawAsync("IF OBJECT_ID('ManagerSummary','U') IS NULL CREATE TABLE ManagerSummary (Id INT NOT NULL PRIMARY KEY, Json NVARCHAR(MAX) NOT NULL, UpdatedAt DATETIME2 NOT NULL);");
            await Database.ExecuteSqlRawAsync("IF OBJECT_ID('ManagerAccounts','U') IS NULL CREATE TABLE ManagerAccounts (Username NVARCHAR(30) NOT NULL PRIMARY KEY, Salt VARCHAR(40) NOT NULL, Hash VARCHAR(100) NOT NULL, IsActive BIT NOT NULL, UpdatedAt DATETIME2 NOT NULL, Kind VARCHAR(10) NOT NULL DEFAULT 'Manager');");
            await Database.ExecuteSqlRawAsync("IF COL_LENGTH('ManagerAccounts','Kind') IS NULL ALTER TABLE ManagerAccounts ADD Kind VARCHAR(10) NOT NULL DEFAULT 'Manager';");
            await Database.ExecuteSqlRawAsync("IF OBJECT_ID('ManagerSessions','U') IS NULL CREATE TABLE ManagerSessions (TokenHash VARCHAR(64) NOT NULL PRIMARY KEY, Username NVARCHAR(30) NOT NULL, CreatedAt DATETIME2 NOT NULL, ExpiresAt DATETIME2 NOT NULL);");
        }
        else if (Database.IsSqlite())
        {
            await Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS ManagerSummary (Id INTEGER NOT NULL PRIMARY KEY, Json TEXT NOT NULL, UpdatedAt TEXT NOT NULL);");
            await Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS ManagerAccounts (Username TEXT NOT NULL PRIMARY KEY, Salt TEXT NOT NULL, Hash TEXT NOT NULL, IsActive INTEGER NOT NULL, UpdatedAt TEXT NOT NULL, Kind TEXT NOT NULL DEFAULT 'Manager');");
            // قاعدة أُنشئت قبل شاشة التلفاز: عمود النوع يُضاف مرة واحدة
            var hasKind = false;
            await using (var conn = Database.GetDbConnection())
            {
                if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('ManagerAccounts') WHERE name = 'Kind'";
                hasKind = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
            }
            if (!hasKind) await Database.ExecuteSqlRawAsync("ALTER TABLE ManagerAccounts ADD COLUMN Kind TEXT NOT NULL DEFAULT 'Manager';");
            await Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS ManagerSessions (TokenHash TEXT NOT NULL PRIMARY KEY, Username TEXT NOT NULL, CreatedAt TEXT NOT NULL, ExpiresAt TEXT NOT NULL);");
        }
        if (Database.IsSqlServer())
            await Database.ExecuteSqlRawAsync("IF COL_LENGTH('AgentState', 'RelayId') IS NULL ALTER TABLE AgentState ADD RelayId UNIQUEIDENTIFIER NULL;");
        else if (Database.IsSqlite())
        {
            // كتابة متزامنة من الهواتف وخدمة المزامنة دون قفل القراءة
            await Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        }
    }

    public DbSet<RelayDevice> Devices => Set<RelayDevice>();
    public DbSet<RelayRequest> Requests => Set<RelayRequest>();
    public DbSet<RelaySnapshot> Snapshots => Set<RelaySnapshot>();
    public DbSet<RelayAgentState> AgentState => Set<RelayAgentState>();
    public DbSet<RelayManagerSummary> ManagerSummaries => Set<RelayManagerSummary>();
    public DbSet<RelayManagerAccount> ManagerAccounts => Set<RelayManagerAccount>();
    public DbSet<RelayManagerSession> ManagerSessions => Set<RelayManagerSession>();

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
        b.Entity<RelayManagerSummary>(e => { e.ToTable("ManagerSummary"); e.Property(x => x.Id).ValueGeneratedNever(); });
        b.Entity<RelayManagerAccount>(e =>
        {
            e.ToTable("ManagerAccounts");
            e.HasKey(x => x.Username);
            e.Property(x => x.Username).HasMaxLength(30);
            e.Property(x => x.Salt).HasMaxLength(40).IsUnicode(false);
            e.Property(x => x.Hash).HasMaxLength(100).IsUnicode(false);
            e.Property(x => x.Kind).HasMaxLength(10).IsUnicode(false);
        });
        b.Entity<RelayManagerSession>(e =>
        {
            e.ToTable("ManagerSessions");
            e.HasKey(x => x.TokenHash);
            e.Property(x => x.TokenHash).HasMaxLength(64).IsUnicode(false);
            e.Property(x => x.Username).HasMaxLength(30);
        });
        b.Entity<RelayAgentState>(e =>
        {
            e.ToTable("AgentState");
            e.Property(x => x.Id).ValueGeneratedNever();
        });
    }
}
