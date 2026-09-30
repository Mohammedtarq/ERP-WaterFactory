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
    public AppSession(string projectName, string fullName, ProjectSessionInfo info)
    {
        ProjectName = projectName;
        FullName = fullName;
        ConnectionString = info.ConnectionString;
        UserId = info.LocalUserId;
        Username = info.Username;
        RoleName = info.RoleName;
        Permissions = info.Permissions;
    }

    public string ProjectName { get; }
    public string FullName { get; }
    public string ConnectionString { get; }
    public int UserId { get; }
    public string Username { get; }
    public string RoleName { get; }
    public UserPermissions Permissions { get; }

    public ProjectDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProjectDbContext>().UseSqlServer(ConnectionString).Options);
}
