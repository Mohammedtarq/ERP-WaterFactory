using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace ERP.Setup
{
    /// <summary>منطق المثبّت (بلا واجهة): فحص السيرفر وكلمة المرور، تنزيل الملفات والتحقق من بصماتها، النسخ، الاختصارات، حفظ الإعداد.</summary>
    public static class SetupCore
    {
        public const string DefaultHost = "192.168.0.200";
        public const string SqlUser = "erp_app";
        public const string ControlDb = "ERP_ControlDB";
        public const int UpdatePort = 5080;

        public class FileEntry { public string Path; public string Sha256; public long Size; }

        /// <summary>«192.168.0.200» أو «192.168.0.200\SQLEXPRESS» ← اسم جهاز السيرفر فقط.</summary>
        public static string HostOf(string input)
        {
            var s = (input ?? "").Trim();
            if (s.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) s = s.Substring(4);
            return s.Split('\\')[0].Split(',')[0].Trim();
        }

        /// <summary>يكمل اسم النسخة تلقائيًا: العنوان وحده يعني SQLEXPRESS.</summary>
        public static string SqlServerOf(string input)
        {
            var s = (input ?? "").Trim();
            return s.IndexOf('\\') >= 0 ? s : s + "\\SQLEXPRESS";
        }

        public static string BuildConnectionString(string sqlServer, string password)
        {
            var b = new SqlConnectionStringBuilder
            {
                DataSource = sqlServer,
                InitialCatalog = ControlDb,
                UserID = SqlUser,
                Password = password,
                TrustServerCertificate = true,
                MultipleActiveResultSets = false,
                ConnectTimeout = 15
            };
            return b.ConnectionString;
        }

        public static string UpdateBaseUrl(string host) { return "http://" + host + ":" + UpdatePort + "/update/"; }

        /// <summary>null = تم الاتصال، وإلا رسالة عربية مفهومة (كلمة المرور خاطئة، أو السيرفر لا يُصل إليه).</summary>
        public static string TestSql(string connectionString)
        {
            try
            {
                using (var c = new SqlConnection(connectionString))
                {
                    c.Open();
                    return null;
                }
            }
            catch (SqlException ex)
            {
                if (ex.Number == 18456 || ex.Number == 4060) return "كلمة المرور غير صحيحة، أعد المحاولة.";
                return "تعذّر الاتصال بقاعدة البيانات على السيرفر. تأكد من العنوان ومن أن السيرفر يعمل.";
            }
            catch (Exception)
            {
                return "تعذّر الاتصال بقاعدة البيانات على السيرفر. تأكد من العنوان ومن أن السيرفر يعمل.";
            }
        }

        private static string GetText(string url)
        {
            using (var w = new WebClient())
            {
                var bytes = w.DownloadData(url);
                return new UTF8Encoding(false).GetString(bytes).TrimStart('﻿');
            }
        }

        public static List<FileEntry> FetchManifest(string baseUrl, out string version)
        {
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var offer = ser.Deserialize<Dictionary<string, object>>(GetText(baseUrl + "version.json"));
            version = offer.ContainsKey("version") ? Convert.ToString(offer["version"]) : "";
            var items = ser.Deserialize<List<Dictionary<string, object>>>(GetText(baseUrl + "manifest.json"));
            var list = new List<FileEntry>();
            foreach (var m in items)
                list.Add(new FileEntry { Path = Convert.ToString(m["path"]), Sha256 = Convert.ToString(m["sha256"]), Size = Convert.ToInt64(m["size"]) });
            if (list.Count == 0) throw new InvalidOperationException("لا ملفات منشورة على السيرفر");
            return list;
        }

        public static string Sha256Of(string file)
        {
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(file))
            {
                var h = sha.ComputeHash(f);
                var sb = new StringBuilder();
                foreach (var b in h) sb.Append(b.ToString("X2"));
                return sb.ToString();
            }
        }

        /// <summary>ينزّل كل الملفات إلى مجلد مؤقت ويتحقق من بصمة كل ملف قبل أن يُمسّ أي شيء على الجهاز.</summary>
        public static void Download(string baseUrl, List<FileEntry> files, string stage, Action<int, string> progress)
        {
            long total = 0, done = 0;
            foreach (var f in files) total += f.Size;
            if (total == 0) total = 1;
            foreach (var f in files)
            {
                var rel = f.Path.Replace('/', '\\');
                var dst = Path.Combine(stage, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                var segs = f.Path.Split('/');
                for (int i = 0; i < segs.Length; i++) segs[i] = Uri.EscapeDataString(segs[i]);
                var url = baseUrl + "files/" + string.Join("/", segs);
                using (var w = new WebClient()) w.DownloadFile(url, dst);
                if (!string.Equals(Sha256Of(dst), f.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("الملف " + f.Path + " وصل ناقصًا أو تالفًا. أعد المحاولة.");
                done += f.Size;
                progress((int)(done * 100 / total), "جارٍ التنزيل من السيرفر…");
            }
        }

        public static void Log(string text)
        {
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "ERP-Setup.log"), DateTime.Now.ToString("s") + " " + text + Environment.NewLine); } catch (Exception) { }
        }

        /// <summary>يغلق البرنامج إن كان مفتوحًا من مجلد التثبيت (وإلا تُقفل ملفاته فيفشل النسخ). التثبيت فوق نسخة قديمة هو الحالة المعتادة.</summary>
        public static void CloseRunningApp(string installDir)
        {
            foreach (var p in Process.GetProcessesByName("ERP.Desktop"))
            {
                try
                {
                    var path = p.MainModule != null ? p.MainModule.FileName : "";
                    if (path.StartsWith(installDir, StringComparison.OrdinalIgnoreCase))
                    {
                        p.CloseMainWindow();
                        if (!p.WaitForExit(5000)) { p.Kill(); p.WaitForExit(5000); }
                    }
                }
                catch (Exception ex) { Log("CloseRunningApp: " + ex.Message); }
            }
        }

        public static void CopyAll(string stage, string installDir)
        {
            Directory.CreateDirectory(installDir);
            // التحديث التلقائي يستبدل الملفات من حساب المستخدم نفسه: صلاحية التعديل لمجموعة Users (قبل النسخ فترثها الملفات)
            Run("icacls.exe", "\"" + installDir + "\" /grant *S-1-5-32-545:(OI)(CI)M /Q");
            foreach (var file in Directory.GetFiles(stage, "*", SearchOption.AllDirectories))
            {
                var rel = file.Substring(stage.TrimEnd('\\').Length + 1);
                var dst = Path.Combine(installDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(file, dst, true);
            }
        }

        private static void Run(string exe, string args)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false });
                if (p != null) p.WaitForExit(30000);
            }
            catch (Exception) { }
        }

        /// <summary>اختصار على سطح المكتب وقائمة البرامج. WScript.Shell لا يحفظ اسمًا عربيًا على نظام لغته غير عربية: يُحفظ بإنجليزي ثم يُعاد تسميته.</summary>
        public static void CreateShortcuts(string exe, string workDir)
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic shell = Activator.CreateInstance(shellType);
            foreach (var folder in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
            })
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
                var temp = Path.Combine(folder, "ERP-WaterFactory.lnk");
                var lnk = shell.CreateShortcut(temp);
                lnk.TargetPath = exe;
                lnk.WorkingDirectory = workDir;
                lnk.Save();
                var final = Path.Combine(folder, "نظام معمل المياه.lnk");
                if (File.Exists(final)) File.Delete(final);
                File.Move(temp, final);
            }
        }

        public static string DefaultConfigPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ERP-WaterFactory", "appsettings.json");
        }

        /// <summary>نفس ملف الإعداد الذي يكتبه معالج الإعداد في البرنامج (%AppData%\ERP-WaterFactory\appsettings.json). الأول القديم يُحفظ نسخة احتياطية.</summary>
        public static void SaveConfig(string path, string connectionString)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (File.Exists(path))
            {
                var bak = Path.Combine(Path.GetDirectoryName(path), "appsettings.old.json");
                if (!File.Exists(bak)) File.Copy(path, bak);
            }
            var json = new JavaScriptSerializer().Serialize(new Dictionary<string, string> { { "ControlDbConnectionString", connectionString } });
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }

        /// <summary>يشغّل البرنامج بصلاحية المستخدم العادي (لا بصلاحية المدير التي يعمل بها المثبّت) عبر explorer.</summary>
        public static void Launch(string exe)
        {
            try { Process.Start("explorer.exe", "\"" + exe + "\""); } catch (Exception) { }
        }

        /// <summary>التثبيت كاملًا. يرمي استثناءً برسالة عربية عند الفشل، ولا يمس الجهاز إن فشل التنزيل أو التحقق.</summary>
        public static string Install(Options o, Action<int, string> progress)
        {
            var baseUrl = o.BaseUrl ?? UpdateBaseUrl(HostOf(o.Host));
            var sqlServer = SqlServerOf(o.Host);
            var conn = BuildConnectionString(sqlServer, o.Password);

            if (!o.SkipSql)
            {
                progress(2, "فحص الاتصال بالسيرفر…");
                var bad = TestSql(conn);
                if (bad != null) throw new InvalidOperationException(bad);
            }

            progress(5, "قراءة ما نشره السيرفر…");
            string version;
            List<FileEntry> files;
            try { files = FetchManifest(baseUrl, out version); }
            catch (WebException) { throw new InvalidOperationException("تعذّر الوصول إلى السيرفر لتنزيل البرنامج. تأكد من العنوان ومن أن البرنامج على السيرفر جاهز للتحديث التلقائي."); }

            var stage = Path.Combine(Path.GetTempPath(), "erp-setup-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(stage);
                try { Download(baseUrl, files, stage, (p, t) => progress(5 + p * 85 / 100, t)); }
                catch (WebException) { throw new InvalidOperationException("انقطع الاتصال أثناء التنزيل. أعد المحاولة."); }

                progress(92, "تثبيت البرنامج…");
                CloseRunningApp(o.InstallDir);
                try { CopyAll(stage, o.InstallDir); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Log("CopyAll: " + ex);
                    throw new InvalidOperationException("تعذّر نسخ الملفات. أغلق البرنامج إن كان مفتوحًا ثم أعد المحاولة.\n(" + ex.Message + ")");
                }
                var exe = Path.Combine(o.InstallDir, "ERP.Desktop.exe");
                if (!File.Exists(exe)) throw new InvalidOperationException("الحزمة المنشورة على السيرفر لا تحوي ERP.Desktop.exe");

                if (!o.NoShortcuts) CreateShortcuts(exe, o.InstallDir);
                progress(97, "حفظ إعداد الاتصال…");
                SaveConfig(o.ConfigPath ?? DefaultConfigPath(), conn);
                progress(100, "اكتمل التثبيت");
                return exe;
            }
            finally
            {
                try { Directory.Delete(stage, true); } catch (Exception) { }
            }
        }
    }

    public class Options
    {
        public string Host = SetupCore.DefaultHost;
        public string Password = "";
        public string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ERP-WaterFactory");
        public string BaseUrl;      // للاختبار الآلي فقط
        public string ConfigPath;   // للاختبار الآلي فقط
        public bool SkipSql;        // للاختبار الآلي فقط (لا SQL Server على جهاز البناء)
        public bool NoShortcuts;
        public bool NoLaunch;
        public bool Silent;
    }
}
