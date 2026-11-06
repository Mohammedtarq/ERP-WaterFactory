using ERP.Cloud.Contracts;
using ERP.RepApp.Core;
using Xunit;

namespace ERP.Cloud.Tests;

/// <summary>دخول تطبيق المندوب: اسم وكلمة مرور حتى 4 خانات بلا تعقيد، مجزّأة بملح، مع إيقاف مؤقت للتخمين.</summary>
public class RepLoginTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("replogin").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Theory]
    [InlineData("a", true)]
    [InlineData("12", true)]
    [InlineData("ab12", true)]
    [InlineData("علي", true)]
    [InlineData("١٢٣٤", true)]
    [InlineData("12345", false)]     // أكثر من 4
    [InlineData("", false)]
    [InlineData("a b", false)]       // فراغ
    [InlineData("a-1", false)]       // رمز
    public void Accepts_one_to_four_letters_or_digits_only(string text, bool valid) => Assert.Equal(valid, RepLogin.IsValid(text));

    [Fact]
    public void Password_is_hashed_with_salt_and_matches_in_any_digit_script_and_letter_case()
    {
        var a = RepLogin.Create("Ali", "١٢٣٤");
        var b = RepLogin.Create("Ali", "1234");
        Assert.Equal("ali", a.Username);
        Assert.NotEqual(a.Hash, b.Hash);                       // ملح مختلف لكل حفظ
        Assert.DoesNotContain("1234", a.Hash);
        Assert.True(RepLogin.Verify(a, "ALI", "1234"));         // أرقام لاتينية لما حُفظ بالهندية، وحالة الحروف
        Assert.True(RepLogin.Verify(b, "ali", "١٢٣٤"));
        Assert.False(RepLogin.Verify(a, "ali", "1235"));
        Assert.False(RepLogin.Verify(a, "omar", "1234"));
    }

    [Fact]
    public void Guard_locks_after_five_wrong_attempts_and_survives_restarts()
    {
        var login = RepLogin.Create("ali", "12");
        var now = new DateTime(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc);
        var store = new LocalStore(Path.Combine(_dir, "guard.db"));
        var guard = new LoginGuard(store, () => now);

        for (var i = 1; i <= 4; i++)
        {
            var r = guard.TryLogin(login, "ali", "99");
            Assert.False(r.Ok);
            Assert.Equal(5 - i, r.AttemptsLeft);
        }
        var locked = guard.TryLogin(login, "ali", "99");                  // الخامسة: إيقاف 30 ثانية
        Assert.Equal(30, locked.LockedSeconds);
        Assert.False(guard.TryLogin(login, "ali", "12").Ok);               // حتى الصحيحة مرفوضة أثناء الإيقاف

        var reopened = new LoginGuard(new LocalStore(Path.Combine(_dir, "guard.db")), () => now.AddSeconds(10));   // إغلاق التطبيق لا يصفّر العدّاد
        Assert.True(reopened.TryLogin(login, "ali", "12") is { Ok: false, LockedSeconds: > 0 });

        var after = new LoginGuard(store, () => now.AddSeconds(31));
        Assert.True(after.TryLogin(login, "ali", "12").Ok);                // انتهى الإيقاف: الصحيحة تدخل وتُصفّر العدّاد
        Assert.Equal(4, after.TryLogin(login, "ali", "00").AttemptsLeft);
    }

    [Fact]
    public void Lock_time_doubles_with_each_extra_failure_up_to_ten_minutes()
    {
        var login = RepLogin.Create("ali", "12");
        var now = new DateTime(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc);
        var guard = new LoginGuard(new LocalStore(Path.Combine(_dir, "g2.db")), () => now);
        var seconds = new List<int>();
        for (var i = 0; i < 12; i++)
        {
            var r = guard.TryLogin(login, "ali", "99");
            if (r.LockedSeconds > 0) { seconds.Add(r.LockedSeconds); now = now.AddSeconds(r.LockedSeconds + 1); }
        }
        Assert.Equal(new[] { 30, 60, 120, 240, 480, 600, 600, 600 }, seconds);
    }
}
