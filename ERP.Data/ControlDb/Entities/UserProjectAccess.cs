namespace ERP.Data.ControlDb.Entities;

/// <summary>
/// يربط: أي مستخدم عام له صلاحية الدخول لأي مشروع، وما هو معرّفه المحلي
/// داخل قاعدة بيانات ذلك المشروع (Users.Id هناك).
/// </summary>
public class UserProjectAccess
{
    public int Id { get; set; }
    public int GlobalUserId { get; set; }
    public GlobalUser GlobalUser { get; set; } = null!;

    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public int LocalUserIdInProject { get; set; }
    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;
}
