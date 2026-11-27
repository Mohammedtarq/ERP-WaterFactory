using System.Security.Cryptography;
using System.Text;

namespace ERP.Cloud.Contracts;

/// <summary>اسم دخول المندوب وكلمة مروره كما تصل الهاتف: كلمة المرور مجزّأة بملح، لا نصًّا.</summary>
public record SnapshotLogin(string Username, string Salt, string Hash);

/// <summary>
/// دخول تطبيق المندوب البسيط (قرار المالك): اسم وكلمة مرور من حرف إلى 4 حروف أو أرقام بلا تعقيد.
/// الأمان هنا: الجهاز مربوط أصلًا بمفتاح سري (رمز QR)، وهذا قفل إضافي عند فتح التطبيق؛ كلمة المرور لا تُخزَّن نصًّا،
/// والهاتف يتحقق منها محليًا مع إيقاف مؤقت بعد المحاولات الخاطئة (LoginGuard).
/// </summary>
public static class RepLogin
{
    public const int MaxLength = 4;
    private const int Iterations = 20_000;

    /// <summary>الأرقام العربية الهندية تُحوَّل إلى لاتينية والحروف الإنجليزية لا تتأثر بحالتها؛ فالكتابة من أي لوحة مفاتيح تطابق.</summary>
    public static string Normalize(string? text)
    {
        var chars = (text ?? "").Trim().Select(c => c switch
        {
            >= '٠' and <= '٩' => (char)('0' + (c - '٠')),
            >= '۰' and <= '۹' => (char)('0' + (c - '۰')),
            _ => char.ToLowerInvariant(c)
        }).ToArray();
        return new string(chars);
    }

    /// <summary>من حرف إلى 4 حروف أو أرقام (عربية أو إنجليزية)، بلا فراغات ورموز.</summary>
    public static bool IsValid(string? text)
    {
        var t = Normalize(text);
        return t.Length is >= 1 and <= MaxLength && t.All(char.IsLetterOrDigit);
    }

    public static string NewSalt() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

    public static string Hash(string password, string salt) =>
        Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(Normalize(password)), Convert.FromBase64String(salt), Iterations, HashAlgorithmName.SHA256, 32));

    public static SnapshotLogin Create(string username, string password)
    {
        var salt = NewSalt();
        return new SnapshotLogin(Normalize(username), salt, Hash(password, salt));
    }

    /// <summary>مقارنة بزمن ثابت.</summary>
    public static bool Verify(SnapshotLogin login, string? username, string? password) =>
        Normalize(username) == login.Username
        && CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(Hash(password ?? "", login.Salt)), Convert.FromBase64String(login.Hash));
}
