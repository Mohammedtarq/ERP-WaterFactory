using ERP.Data.ControlDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.ControlDb;

/// <summary>
/// سياق قاعدة التحكم المركزية (ERP_ControlDB) — قاعدة واحدة فقط لكل النظام،
/// لا تُفتح فيها أي بيانات تشغيلية، فقط المشاريع وصلاحيات الدخول إليها.
/// </summary>
public class ControlDbContext : DbContext
{
    public ControlDbContext(DbContextOptions<ControlDbContext> options) : base(options) { }

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<GlobalUser> GlobalUsers => Set<GlobalUser>();
    public DbSet<UserProjectAccess> UserProjectAccesses => Set<UserProjectAccess>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // اسم الجدول الفعلي في السكربت هو المفرد UserProjectAccess، بينما اسم
        // الكيان بالجمع في الكود لسهولة القراءة (Collection navigation) — هذا
        // السطر يثبّت التطابق بينهما دون أي حاجة لتعديل قاعدة البيانات.
        modelBuilder.Entity<UserProjectAccess>().ToTable("UserProjectAccess");

        modelBuilder.Entity<GlobalUser>()
            .HasIndex(u => u.Username)
            .IsUnique();

        modelBuilder.Entity<UserProjectAccess>()
            .HasIndex(a => new { a.GlobalUserId, a.ProjectId })
            .IsUnique();

        modelBuilder.Entity<UserProjectAccess>()
            .HasOne(a => a.GlobalUser)
            .WithMany(u => u.ProjectAccesses)
            .HasForeignKey(a => a.GlobalUserId);

        modelBuilder.Entity<UserProjectAccess>()
            .HasOne(a => a.Project)
            .WithMany(p => p.UserAccesses)
            .HasForeignKey(a => a.ProjectId);
    }
}
