using ERP.Cloud.Contracts;
using ERP.RepApp.Core;
using Xunit;

namespace ERP.Cloud.Tests;

/// <summary>قفل دخول تطبيق المندوبين: 5 محاولات خاطئة ثم إيقاف مؤقت بعدّاد، وتصفير العدّاد بالدخول الصحيح.</summary>
public class LoginGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rep-login-" + Guid.NewGuid().ToString("N"));
    public LoginGuardTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Five_wrong_attempts_then_a_timed_lock_with_countdown_and_a_correct_login_resets_it()
    {
        var store = new LocalStore(Path.Combine(_dir, "rep.db"));
        var now = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        var guard = new LoginGuard(store, () => now);
        var login = RepLogin.Create("ab", "99");

        Assert.Equal(LoginGuard.FreeAttempts, guard.AttemptsLeft);
        Assert.Equal(5, LoginGuard.FreeAttempts);
        for (var i = 1; i <= 4; i++)
        {
            var wrong = guard.TryLogin(login, "ab", "00");
            Assert.False(wrong.Ok);
            Assert.Equal(5 - i, wrong.AttemptsLeft);
            Assert.Contains($"متبقي {5 - i} من 5", wrong.Message);
            Assert.Equal(5 - i, guard.AttemptsLeft);
        }

        var fifth = guard.TryLogin(login, "ab", "00");
        Assert.Equal(30, fifth.LockedSeconds);
        Assert.Contains("0:30", fifth.Message);
        Assert.Equal(30, guard.LockedSeconds);
        Assert.False(guard.TryLogin(login, "ab", "99").Ok);              // أثناء الإيقاف حتى الصحيحة مرفوضة

        now = now.AddSeconds(31);                                         // انتهى الإيقاف: محاولة واحدة قبل إيقاف أطول
        Assert.Equal(0, guard.LockedSeconds);
        Assert.Equal(1, guard.AttemptsLeft);
        var again = guard.TryLogin(login, "ab", "00");
        Assert.Equal(60, again.LockedSeconds);                            // يتضاعف
        now = now.AddSeconds(61);
        Assert.True(guard.TryLogin(login, "AB", "99").Ok);                // الكتابة بحالة مختلفة تطابق
        Assert.Equal(LoginGuard.FreeAttempts, guard.AttemptsLeft);        // وصُفّر العدّاد
        Assert.Equal("4:32", LoginResult.Clock(272));
    }
}
