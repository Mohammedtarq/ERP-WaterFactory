using ERP.Data.ProjectDb;
using ERP.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace ERP.Presentation.Services;

/// <summary>
/// حالة الجلسة بعد اختيار المشروع: المستخدم المحلي، صلاحياته، ومصنع سياقات
/// قاعدة البيانات. كل عملية تفتح سياقًا جديدًا قصير العمر (لا سياق مشترك طويل).
/// </summary>
public class AppSession
{
    public AppSession(string projectName, string fullName, ProjectSessionInfo info,
                      string? controlConnectionString = null, string? globalUsername = null, int projectId = 0)
    {
        ControlConnectionString = controlConnectionString;
        GlobalUsername = globalUsername ?? info.Username;
        ProjectId = projectId;
        ProjectName = projectName;
        FullName = fullName;
        ConnectionString = info.ConnectionString;
        UserId = info.LocalUserId;
        Username = info.Username;
        RoleName = info.RoleName;
        Permissions = info.Permissions;
    }

    public string ProjectName { get; }
    /// <summary>قاعدة التحكم (لإدارة حسابات الدخول والمشاريع). null في سياقات لا تحتاجها.</summary>
    public string? ControlConnectionString { get; }
    public string GlobalUsername { get; }
    public int ProjectId { get; }
    public string FullName { get; }
    public string ConnectionString { get; }
    public int UserId { get; }
    public string Username { get; }
    public string RoleName { get; }
    public UserPermissions Permissions { get; }

    private long _dataVersion;
    private DataChangeInterceptor? _interceptor;

    /// <summary>يزيد بعد كل عملية كتابة ناجحة في هذه الجلسة — الشاشات تُحدَّث عند فتحها إن تغيّر.</summary>
    public long DataVersion => Interlocked.Read(ref _dataVersion);
    public void MarkDataChanged() => Interlocked.Increment(ref _dataVersion);

    public ProjectDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProjectDbContext>().UseSqlServer(ConnectionString)
                .AddInterceptors(_interceptor ??= new DataChangeInterceptor(this)).Options);
}
