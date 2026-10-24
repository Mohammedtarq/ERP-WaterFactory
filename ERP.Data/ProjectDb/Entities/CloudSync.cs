namespace ERP.Data.ProjectDb.Entities;

/// <summary>
/// إعداد المزامنة وحالتها (40_cloud_sync.sql، 47_sync_dual.sql) — سطر واحد.
/// نظامان معًا: الخادم المحلي (Local*) على حاسبة السيرفر، والخادم السحابي (ServerUrl وأعمدة Last*).
/// </summary>
public class CloudSyncSetting
{
    public int Id { get; set; } = 1;
    public string? ServerUrl { get; set; }
    public string? AgentKey { get; set; }
    public bool IsEnabled { get; set; }
    public int IntervalSeconds { get; set; } = 20;
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastErrorAt { get; set; }
    public int ReceivedCount { get; set; }
    public DateTime? LastSnapshotAt { get; set; }
    public string? AgentMachine { get; set; }

    public bool LocalEnabled { get; set; }
    /// <summary>فُعّل الخادم المحلي تلقائيًا من خدمة المزامنة (52_sync_auto_setup.sql)، أو حسم المستخدم الأمر: لا يتكرر التفعيل.</summary>
    public bool AutoSetupDone { get; set; }
    /// <summary>عنوان الخادم المحلي للهواتف — تكتبه خدمة المزامنة من عنوان الحاسبة الحالي.</summary>
    public string? LocalUrl { get; set; }
    public DateTime? LocalLastSuccessAt { get; set; }
    public string? LocalLastError { get; set; }
    public DateTime? LocalLastErrorAt { get; set; }
}

/// <summary>حساب دخول لوحة المدير على الإنترنت (67_manager_accounts.sql): كلمة المرور مجزّأة بملح.</summary>
public class ManagerAccount
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string Salt { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ChangedAt { get; set; }
}
