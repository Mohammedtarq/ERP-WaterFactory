using ERP.Cloud.Contracts;

namespace ERP.RepApp.Core;

/// <summary>نتيجة محاولة دخول: Ok، أو خطأ مع المحاولات المتبقية قبل الإيقاف، أو إيقاف مؤقت بثوانٍ.</summary>
public record LoginResult(bool Ok, int AttemptsLeft = 0, int LockedSeconds = 0)
{
    public string Message => Ok ? "" : LockedSeconds > 0 ? $"محاولات كثيرة — انتظر {LockedSeconds} ثانية" :
        AttemptsLeft > 0 ? $"الاسم أو كلمة المرور غير صحيحة (متبقي {AttemptsLeft} محاولات)" : "الاسم أو كلمة المرور غير صحيحة";
}

/// <summary>
/// قفل فتح التطبيق: كلمة المرور قصيرة (حتى 4 خانات) فلا بد من منع التخمين — بعد 5 محاولات خاطئة متتالية إيقاف 30 ثانية،
/// ثم يتضاعف مع كل خطأ إضافي حتى 10 دقائق. العدّاد محفوظ في الهاتف فلا يُصفَّر بإغلاق التطبيق.
/// </summary>
public sealed class LoginGuard
{
    public const int FreeAttempts = 5;
    private readonly LocalStore _store;
    private readonly Func<DateTime> _now;

    public LoginGuard(LocalStore store, Func<DateTime>? now = null)
    {
        _store = store;
        _now = now ?? (() => DateTime.UtcNow);
    }

    private int Fails => int.TryParse(_store.Get("LoginFails"), out var n) ? n : 0;

    private int LockedFor()
    {
        if (!long.TryParse(_store.Get("LoginLockUntil"), out var ticks)) return 0;
        var left = new DateTime(ticks, DateTimeKind.Utc) - _now();
        return left > TimeSpan.Zero ? (int)Math.Ceiling(left.TotalSeconds) : 0;
    }

    public LoginResult TryLogin(SnapshotLogin login, string? username, string? password)
    {
        if (LockedFor() is > 0 and var wait) return new LoginResult(false, 0, wait);
        if (RepLogin.Verify(login, username, password))
        {
            _store.Set("LoginFails", null);
            _store.Set("LoginLockUntil", null);
            return new LoginResult(true);
        }
        var fails = Fails + 1;
        _store.Set("LoginFails", fails.ToString());
        if (fails < FreeAttempts) return new LoginResult(false, FreeAttempts - fails);
        var seconds = Math.Min(600, 30 * (1 << Math.Min(fails - FreeAttempts, 5)));
        _store.Set("LoginLockUntil", _now().AddSeconds(seconds).Ticks.ToString());
        return new LoginResult(false, 0, seconds);
    }
}
