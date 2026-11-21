namespace ERP.Cloud.Api;

/// <summary>
/// ملفات تحديث البرنامج لأجهزة المعمل: يقرأها البرنامج عند فتحه (/update/version.json) وينزّل منها المتغيّر فقط.
/// المجلد يكتبه سكربت «نشر تحديث» على السيرفر (%ProgramData%\ERP-WaterFactory\Updates\current) — لا حاجة لإعادة تشغيل الخدمة.
/// الملفات ليست سرية (حزمة البرنامج نفسها)، فلا يلزمها مفتاح. لا أثر لها إن لم يوجد المجلد (الاستضافة السحابية).
/// </summary>
public static class UpdateEndpoints
{
    public static string DefaultFolder(string commonAppData) => Path.Combine(commonAppData, "ERP-WaterFactory", "Updates", "current");

    public static void Map(WebApplication app, string folder)
    {
        var root = Path.GetFullPath(folder);
        app.MapGet("/update/{**path}", (string? path) =>
        {
            if (string.IsNullOrWhiteSpace(path)) return Results.NotFound();
            var full = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
            // لا خروج من مجلد التحديث (..)
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return Results.NotFound();
            return Results.File(new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), full.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "application/json" : "application/octet-stream", enableRangeProcessing: true);
        });
    }
}
