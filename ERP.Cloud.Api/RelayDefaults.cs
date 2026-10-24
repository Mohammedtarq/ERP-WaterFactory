namespace ERP.Cloud.Api;

/// <summary>
/// قيم الخادم المحلي الافتراضية على حاسبة المعمل (Windows): تعمل الخدمة بلا أي إعداد، حتى لو لم يُقرأ ملف relay.json.
/// القاعدة وملف المفتاح والمنفذ في %ProgramData%\ERP-WaterFactory\LocalServer — وهي نفس مواضع المثبّت وخدمة المزامنة.
/// لا تتجاوز قيمة مضبوطة صراحةً (متغير بيئة أو relay.json).
/// </summary>
public static class RelayDefaults
{
    public const int Port = 5080;

    public static string FolderOf(string commonAppData) => Path.Combine(commonAppData, "ERP-WaterFactory", "LocalServer");

    /// <returns>القيم الناقصة فقط.</returns>
    public static Dictionary<string, string?> Missing(IConfiguration config, string folder)
    {
        var d = new Dictionary<string, string?>();
        var hasDb = !string.IsNullOrWhiteSpace(config["Relay:SqlitePath"]) || !string.IsNullOrWhiteSpace(config.GetConnectionString("Relay"));
        if (!hasDb) d["Relay:SqlitePath"] = Path.Combine(folder, "relay.db");
        if (string.IsNullOrWhiteSpace(config["Relay:AgentKey"]) && string.IsNullOrWhiteSpace(config["Relay:AgentKeyFile"]))
            d["Relay:AgentKeyFile"] = Path.Combine(folder, "agent-key.txt");
        if (string.IsNullOrWhiteSpace(config["Relay:Listen"])) d["Relay:Listen"] = $"http://0.0.0.0:{Port}";
        return d;
    }
}
