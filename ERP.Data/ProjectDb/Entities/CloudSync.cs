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
    /// <summary>عنوان الخادم المحلي للهواتف — تكتبه خدمة المزامنة من عنوان الحاسبة الحالي.</summary>
    public string? LocalUrl { get; set; }
    public DateTime? LocalLastSuccessAt { get; set; }
    public string? LocalLastError { get; set; }
    public DateTime? LocalLastErrorAt { get; set; }
}
