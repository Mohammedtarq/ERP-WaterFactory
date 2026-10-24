using System.Security.Cryptography;
using System.Text;

namespace ERP.Cloud.Contracts;

/// <summary>مسارات لوحة المدير (قراءة فقط): الصفحة، الدخول، الملخص — ورفع الملخص والحسابات من خدمة المزامنة.</summary>
public static class ManagerRoutes
{
    public const string Page = "manager";
    public const string Login = "api/manager/login";
    public const string Summary = "api/manager/summary";
    public const string AgentSummary = "api/agent/manager/summary";
    public const string AgentAccounts = "api/agent/manager/accounts";
}

/// <summary>
/// حساب المدير كما يصل الخادم السحابي: كلمة المرور مجزّأة بملح (لا نصًّا). الخادم لا يرى كلمة المرور أبدًا،
/// وحذف الحساب أو إيقافه في المعمل يصل الخادم في الدورة التالية.
/// </summary>
public record AgentManagerAccount(string Username, string Salt, string Hash, bool IsActive);

public record ManagerLoginRequest(string? Username, string? Password);
public record ManagerLoginResponse(string Token, DateTime ExpiresUtc);

public static class ManagerLogin
{
    public const int MinPasswordLength = 8;
    private const int Iterations = 120_000;

    public static string? ValidateUsername(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Trim().Length is < 3 or > 30 || !name.Trim().All(c => char.IsLetterOrDigit(c) || c is '_' or '.')
            ? "اسم المستخدم من 3 إلى 30 حرفًا أو رقمًا (يُسمح بـ _ و .)" : null;

    public static string? ValidatePassword(string? password) =>
        password is null || password.Length < MinPasswordLength ? $"كلمة المرور {MinPasswordLength} أحرف على الأقل" : null;

    public static string NormalizeName(string? name) => (name ?? "").Trim().ToLowerInvariant();

    public static string NewSalt() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

    public static string Hash(string password, string salt) =>
        Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Convert.FromBase64String(salt), Iterations, HashAlgorithmName.SHA256, 32));

    public static bool Verify(string? password, string salt, string hash)
    {
        if (string.IsNullOrEmpty(password) || password.Length > 200) return false;
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(Hash(password, salt)), Convert.FromBase64String(hash)); }
        catch (FormatException) { return false; }
    }
}

// ============================ ملخص المدير ============================
// ملخص جاهز تبنيه حاسبة المعمل وترفعه كل دقيقة؛ لا قاعدة بيانات ولا رواتب ولا سلف فيه.

public record ManagerToday(decimal Sales, decimal CashSales, decimal CreditSales, decimal Collected, decimal Expenses);
public record ManagerBox(string Name, string Type, decimal Balance);
public record ManagerDay(string Date, decimal Sales);
public record ManagerRep(string Name, decimal SalesToday, decimal CashInWallet, DateTime? LastSeenUtc, int PendingRequests, string Status);
public record ManagerLowStock(string Warehouse, string Item, decimal Balance, decimal Min);
public record ManagerStockItem(string Item, decimal Pieces);
public record ManagerDebtor(string Name, decimal Amount);
public record ManagerAlerts(int PendingRepRequests, int PendingPayments, int LowStock);
public record ManagerPerson(string Name, string Detail);
public record ManagerHr(int Present, int Absent, int Late, int OnLeave, List<ManagerPerson> AbsentToday, List<ManagerPerson> OnLeaveToday, int RaisesDue);

public record ManagerSummary(
    DateTime GeneratedAtUtc, string FactoryName, ManagerToday Today, List<ManagerBox> Boxes, List<ManagerDay> Last30,
    List<ManagerRep> Reps, List<ManagerLowStock> LowStock, List<ManagerStockItem> Stock, List<ManagerDebtor> Debtors,
    decimal TotalReceivables, ManagerAlerts Alerts, ManagerHr Hr);
