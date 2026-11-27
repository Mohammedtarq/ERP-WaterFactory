using System;
using System.Windows.Forms;

namespace ERP.Setup
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--silent": o.Silent = true; break;
                    case "--host": o.Host = args[++i]; break;
                    case "--baseurl": o.BaseUrl = args[++i]; break;
                    case "--installdir": o.InstallDir = args[++i]; break;
                    case "--config": o.ConfigPath = args[++i]; break;
                    case "--skip-sql": o.SkipSql = true; break;
                    case "--no-shortcuts": o.NoShortcuts = true; break;
                    case "--no-launch": o.NoLaunch = true; break;
                }
            }

            if (o.Silent)
            {
                // الاختبار الآلي: كلمة المرور من متغير بيئة (لا تُمرَّر في سطر الأوامر)
                o.Password = Environment.GetEnvironmentVariable("ERP_SETUP_PASSWORD") ?? "";
                try
                {
                    var exe = SetupCore.Install(o, (p, t) => Console.WriteLine(p + "% " + t));
                    if (!o.NoLaunch) SetupCore.Launch(exe);
                    return 0;
                }
                catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(o));
            return 0;
        }
    }
}
