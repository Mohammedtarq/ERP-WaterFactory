namespace ERP.Data.ProjectDb.Entities;

/// <summary>إعداد المزامنة السحابية وحالتها (40_cloud_sync.sql) — سطر واحد.</summary>
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
}
