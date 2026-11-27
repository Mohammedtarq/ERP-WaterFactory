using ERP.Cloud.Contracts;

namespace ERP.Data.Services;

/// <summary>
/// ملفات نشر الخادم السحابي جاهزة من البرنامج — لا يُكتب فيها شيء يدويًا:
/// الخادم نفسه (app)، وDocker Compose بخادم Caddy يستخرج شهادة https تلقائيًا للنطاق، و.env بالنطاق ومفتاح المعمل،
/// ودليل خطوات قصير. تُرفع إلى أي خادم Linux عليه Docker ثم أمر واحد: docker compose up -d.
/// القاعدة ملف SQLite في مجلد data (يكفيه أصغر خادم)، والهواتف لا تتأثر بإعادة النشر.
/// </summary>
public static class CloudDeployBundle
{
    /// <returns>رسالة خطأ عربية، أو null عند النجاح.</returns>
    public static string? Validate(string? cloudUrl, string? agentKey)
    {
        if (string.IsNullOrEmpty(agentKey)) return "فعّل المزامنة أولًا (يُولَّد مفتاح المعمل تلقائيًا)";
        if (CloudUrl.Validate(cloudUrl) is string bad) return bad;
        var u = new Uri(cloudUrl!.Trim());
        if (u.Scheme != Uri.UriSchemeHttps || u.HostNameType != UriHostNameType.Dns)
            return "الخادم السحابي يحتاج نطاقًا باسم (مثل https://rep.alrahma-water.com) لتُستخرج شهادة https تلقائيًا";
        if (!u.IsDefaultPort || u.AbsolutePath != "/") return "اكتب النطاق وحده بلا منفذ أو مسار (مثل https://rep.alrahma-water.com)";
        return null;
    }

    /// <summary>يكتب الملفات في folder (يُنشأ). appSource: مجلد الخادم المنشور بجانب البرنامج (CloudServer) إن وُجد.</summary>
    public static void Write(string folder, string cloudUrl, string agentKey, string? appSource)
    {
        if (Validate(cloudUrl, agentKey) is string bad) throw new InvalidOperationException(bad);
        var domain = new Uri(cloudUrl.Trim()).Host;
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, "data"));
        File.WriteAllText(Path.Combine(folder, ".env"), $"""
            # يكتبه البرنامج — لا تشاركه: فيه مفتاح المعمل
            DOMAIN={domain}
            RELAY_AGENT_KEY={agentKey}

            """.Replace("\r\n", "\n"));
        File.WriteAllText(Path.Combine(folder, "docker-compose.yml"), """
            # الخادم السحابي لتطبيق المندوبين — نظام معمل المياه
            services:
              relay:
                image: mcr.microsoft.com/dotnet/aspnet:9.0
                restart: unless-stopped
                working_dir: /app
                command: ["dotnet", "ERP.Cloud.Api.dll"]
                environment:
                  ASPNETCORE_URLS: http://+:8080
                  Relay__AgentKey: ${RELAY_AGENT_KEY}
                  Relay__SqlitePath: /data/relay.db
                volumes:
                  - ./app:/app:ro
                  - ./data:/data
              caddy:
                image: caddy:2
                restart: unless-stopped
                ports:
                  - "80:80"
                  - "443:443"
                environment:
                  DOMAIN: ${DOMAIN}
                volumes:
                  - ./Caddyfile:/etc/caddy/Caddyfile:ro
                  - caddy_data:/data
                depends_on:
                  - relay
            volumes:
              caddy_data:

            """.Replace("\r\n", "\n"));
        File.WriteAllText(Path.Combine(folder, "Caddyfile"), """
            {$DOMAIN} {
                encode gzip
                reverse_proxy relay:8080
            }

            """.Replace("\r\n", "\n"));
        File.WriteAllText(Path.Combine(folder, "اقرأني - نشر الخادم السحابي.md"), $"""
            # نشر الخادم السحابي لتطبيق المندوبين

            الملفات هنا جاهزة من البرنامج: لا يُعدَّل فيها شيء.

            1. خادم Linux صغير عليه Docker (أي مزوّد: 1 معالج و1GB ذاكرة تكفي).
            2. من لوحة النطاق: سجلّ **A** للاسم `{domain}` يشير إلى عنوان الخادم.
            3. ارفع هذا المجلد كاملًا إلى الخادم (مثلًا بـ WinSCP)، ثم داخله نفّذ:

               ```
               docker compose up -d
               ```

            4. افتح في المتصفح `https://{domain}/api/health` — يجب أن يظهر `"ok":true`.
            5. في البرنامج: «الإعدادات ← المزامنة» ← «اختبار الاتصال». خدمة المزامنة في المعمل تتصل به وحدها،
               وتصل الهواتف عناوينها الجديدة مع نسخة العمل دون إعادة ربط.

            - الشهادة (https) يستخرجها Caddy ويجددها تلقائيًا.
            - البيانات في مجلد `data` (حركات بانتظار المعمل فقط؛ الأصل في قاعدة المعمل).
            - توليد مفتاح معمل جديد يتطلب ملفات نشر جديدة: أعد تجهيزها وانسخ `.env` فوق القديم ثم `docker compose up -d`.
            - تحديث الخادم: انسخ مجلد `app` الجديد ثم `docker compose restart relay`.

            """.Replace("\r\n", "\n"));
        var app = Path.Combine(folder, "app");
        if (appSource is not null && Directory.Exists(appSource))
        {
            foreach (var file in Directory.EnumerateFiles(appSource, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(app, Path.GetRelativePath(appSource, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }
        else Directory.CreateDirectory(app);
    }
}
