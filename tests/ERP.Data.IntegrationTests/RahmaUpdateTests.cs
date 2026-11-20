using ERP.Data.Import;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Security;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>«تحديث من الرحمة»: يضيف الجديد ويحدّث المتغيّر بالمعرّفات المحفوظة، بلا تكرار، وبتراجع عن آخر تحديث.</summary>
public class RahmaUpdateTests
{
    private static async Task LegacySqlAsync(RahmaFixture f, string sql)
    {
        await using var conn = new SqlConnection(f.LegacyCs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Update_adds_new_changes_existing_never_duplicates_and_can_be_undone()
    {
        var f = new RahmaFixture();
        await f.InitializeAsync();
        try
        {
            Assert.True(f.Install.Success, f.Install.ErrorMessage);
            var plan = await RahmaLegacyReader.AnalyzeAsync(f.LegacyCs);
            plan.Employees.Single(e => e.LegacyId == 10).LoanInstallment = 75_000;
            await using var db = f.NewDb();

            // قبل النقل الأول: لا يوجد ما نحدّثه
            var svc = new RahmaUpdateService(db);
            Assert.Contains("النقل الأول", (await svc.PreviewAsync(plan)).Error);

            var imported = await new RahmaImporter(db).ExecuteAsync(plan, f.AdminLocalId, commit: true);
            Assert.True(imported.Success, imported.Error);

            // مطابق تمامًا: كل شيء «بلا تغيير» ولا شيء يُطبَّق
            plan = await RahmaLegacyReader.AnalyzeAsync(f.LegacyCs);
            var same = await svc.PreviewAsync(plan);
            Assert.Null(same.Error);
            Assert.Equal(0, same.ToApply);
            Assert.Contains("لا يوجد ما يُحدَّث", (await svc.ApplyAsync(plan, f.AdminLocalId)).Error);

            // تغيّرت الرحمة: هاتف عميل، وعميل جديد، وزبون بالاسم نفسه موجود يدويًا في الجديد، ومورد جديد
            await LegacySqlAsync(f, @"UPDATE Customer SET phone = N'07709999999' WHERE id = 1;
                INSERT Customer (id, customer_name, phone, add_at, deon, address) VALUES (6, N'عميل جديد من الرحمة', N'0771', '2025-01-01', 0, N'الزبير');
                INSERT Customer (id, customer_name, phone, add_at, deon, address) VALUES (7, N'زبون موجود يدويًا', N'', '2025-01-01', 0, NULL);
                INSERT Supplier VALUES (4, N'مورد جديد', N'0780', 0);");
            db.Customers.Add(new Customer { Name = "زبون موجود يدويًا" });
            // وسعر منتج في الجديد خالف الرحمة
            var item = await db.Items.SingleAsync(i => i.ItemCode == "RH-330-C40");
            var rahmaPrice = item.SalePrice;
            item.SalePrice = rahmaPrice + 5;
            await db.SaveChangesAsync();
            var customersBefore = await db.Customers.CountAsync();

            plan = await RahmaLegacyReader.AnalyzeAsync(f.LegacyCs);
            var preview = await svc.PreviewAsync(plan);
            Assert.Contains(preview.Rows, r => r.Section == "العملاء" && r.Name == "مطعم الحسون" && r.Action == RahmaUpdateAction.Changed && r.Details.Contains("07709999999"));
            Assert.Contains(preview.Rows, r => r.Name == "عميل جديد من الرحمة" && r.Action == RahmaUpdateAction.New);
            Assert.Contains(preview.Rows, r => r.Name == "زبون موجود يدويًا" && r.Action == RahmaUpdateAction.SkippedByName);
            Assert.Contains(preview.Rows, r => r.Section == "الموردون" && r.Name == "مورد جديد" && r.Action == RahmaUpdateAction.New);
            Assert.Contains(preview.Rows, r => r.Section == "أسعار المنتجات" && r.Action == RahmaUpdateAction.Changed);
            Assert.Equal(customersBefore, await db.Customers.CountAsync());                    // المعاينة لا تكتب

            // غير الأدمن مرفوض
            var role = await db.Roles.FirstAsync(r => r.Name == "محاسب");
            var clerk = new User { Username = "upd_clerk", PasswordHash = PasswordHasher.Hash("x"), RoleId = role.Id };
            db.Users.Add(clerk);
            await db.SaveChangesAsync();
            Assert.Contains("للأدمن", (await svc.ApplyAsync(plan, clerk.Id)).Error);

            // نسخة احتياطية فاشلة تُلغي كل شيء
            var blocked = await svc.ApplyAsync(plan, f.AdminLocalId, () => throw new IOException("لا مساحة"));
            Assert.Contains("النسخة الاحتياطية", blocked.Error);
            Assert.Equal(customersBefore, await db.Customers.CountAsync());

            // التطبيق
            var result = await svc.ApplyAsync(plan, f.AdminLocalId, () => Task.FromResult<string?>("C:\\ERP-Backup\\pre-update.bak"));
            Assert.True(result.Success, result.Error);
            Assert.Equal(customersBefore + 1, await db.Customers.CountAsync());               // عميل واحد جديد فقط (المتخطّى لم يتكرر)
            Assert.Equal("07709999999", (await db.Customers.SingleAsync(c => c.Name == "مطعم الحسون")).Phone);
            Assert.Equal("الزبير", (await db.Customers.SingleAsync(c => c.Name == "عميل جديد من الرحمة")).Address);
            Assert.Contains(await db.Suppliers.ToListAsync(), s => s.Name == "مورد جديد" && s.Phone == "0780");
            Assert.Equal(rahmaPrice, (await db.Items.SingleAsync(i => i.ItemCode == "RH-330-C40")).SalePrice);
            Assert.Equal("C:\\ERP-Backup\\pre-update.bak", (await svc.LastActiveRunAsync())!.PreBackupPath);

            // مرة ثانية بلا جديد: لا تكرار
            var again = await svc.PreviewAsync(await RahmaLegacyReader.AnalyzeAsync(f.LegacyCs));
            Assert.Equal(0, again.ToApply);

            // التراجع: يلزم سبب وأدمن
            Assert.Contains("سبب", (await svc.UndoLastAsync(f.AdminLocalId, " ")).Error);
            Assert.Contains("للأدمن", (await svc.UndoLastAsync(clerk.Id, "جرّبت")).Error);
            // عدّل المستخدم هاتفًا بعد التحديث ← لا يُستبدل بصمت
            var hassoun = await db.Customers.SingleAsync(c => c.Name == "مطعم الحسون");
            hassoun.Phone = "0700000000";
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var undo = await svc.UndoLastAsync(f.AdminLocalId, "تحديث بالخطأ");
            Assert.True(undo.Success, undo.Error);
            Assert.Contains(undo.Conflicts, c => c.Contains("مطعم الحسون"));
            Assert.Equal("0700000000", (await db.Customers.SingleAsync(c => c.Name == "مطعم الحسون")).Phone);
            Assert.False(await db.Customers.AnyAsync(c => c.Name == "عميل جديد من الرحمة"));       // أُزيل ما أُضيف
            Assert.False(await db.Suppliers.AnyAsync(s => s.Name == "مورد جديد"));
            Assert.Equal(rahmaPrice + 5, (await db.Items.SingleAsync(i => i.ItemCode == "RH-330-C40")).SalePrice);   // رجع للقيمة القديمة
            Assert.Null(await svc.LastActiveRunAsync());
            Assert.Contains("لا يوجد تحديث", (await svc.UndoLastAsync(f.AdminLocalId, "مرة ثانية")).Error);
            Assert.NotNull((await svc.HistoryAsync()).Single().UndoneAt);

            // بعد التراجع يُعاد اقتراح الجديد (لأن الخريطة أُزيلت) — بلا تكرار للعميل المتخطّى
            var after = await svc.PreviewAsync(await RahmaLegacyReader.AnalyzeAsync(f.LegacyCs));
            Assert.Contains(after.Rows, r => r.Name == "عميل جديد من الرحمة" && r.Action == RahmaUpdateAction.New);
        }
        finally { await f.DisposeAsync(); }
    }
}

/// <summary>«استعادة نسخة الرحمة»: ملف .bak يُستعاد كقاعدة منفصلة جديدة، وملف غير الرحمة يُرفض ولا يبقى منه شيء.</summary>
public class RahmaRestoreTests
{
    [Fact]
    public void Suggested_name_comes_from_the_backup_file_name_or_the_date()
    {
        Assert.Equal("ALRAHMA_2026_11_15_1607", RahmaRestoreService.SuggestName(@"F:\Rahma\ALRAHMA2026-11-15-1607.bak", DateTime.Now));
        Assert.Equal("ALRAHMA_2026_11_20_1015", RahmaRestoreService.SuggestName("/x/other.bak", new DateTime(2026, 11, 20, 10, 15, 0)));
    }

    private static async Task<bool> DbExistsAsync(string master, string name)
    {
        await using var conn = new SqlConnection(master);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT DB_ID(@n)", conn);
        cmd.Parameters.AddWithValue("@n", name);
        return await cmd.ExecuteScalarAsync() is not (null or DBNull);
    }

    [Fact]
    public async Task Restore_makes_a_separate_database_rejects_non_rahma_files_and_never_overwrites()
    {
        var f = new RahmaFixture();
        await f.InitializeAsync();
        var restored = new List<string>();
        var master = new SqlConnectionStringBuilder(f.LegacyCs) { InitialCatalog = "master" }.ConnectionString;
        try
        {
            var backup = new ERP.Data.Services.BackupService(master);
            var folder = await backup.GetDefaultFolderAsync();
            Assert.False(string.IsNullOrEmpty(folder));
            var rahmaBak = await backup.BackupAsync(f.LegacyDb, folder!, "rahma");
            var systemBak = await backup.BackupAsync($"ERP_Rahma_{f.Suffix}", folder!, "system");

            // نسخة رحمة صحيحة: قاعدة جديدة بجداول الرحمة، تظهر في قائمة المصادر، ويمكن تحليلها
            var ok = await RahmaRestoreService.RestoreAsync(master, rahmaBak);
            Assert.True(ok.Success, ok.Error);
            restored.Add(ok.DatabaseName);
            Assert.StartsWith("ALRAHMA_", ok.DatabaseName);
            Assert.Contains(await RahmaLegacyReader.FindCandidatesAsync(master), c => c.Name == ok.DatabaseName);
            var plan = await RahmaLegacyReader.AnalyzeAsync(new SqlConnectionStringBuilder(master) { InitialCatalog = ok.DatabaseName }.ConnectionString);
            Assert.Equal(5, plan.Customers.Count);

            // الاستعادة مرة ثانية لا تستبدل الأولى: اسم جديد
            var again = await RahmaRestoreService.RestoreAsync(master, rahmaBak);
            Assert.True(again.Success, again.Error);
            restored.Add(again.DatabaseName);
            Assert.NotEqual(ok.DatabaseName, again.DatabaseName);

            // قاعدة النظام الأصلية لم تُمس
            Assert.True(await DbExistsAsync(master, f.LegacyDb));
            Assert.True(await DbExistsAsync(master, $"ERP_Rahma_{f.Suffix}"));

            // نسخة قاعدة النظام (ليست الرحمة): ترفض ولا تترك قاعدة مستعادة
            var before = (await RahmaLegacyReader.FindCandidatesAsync(master)).Count;
            var bad = await RahmaRestoreService.RestoreAsync(master, systemBak);
            Assert.False(bad.Success);
            Assert.Contains("ليس نسخة من نظام الرحمة", bad.Error);
            Assert.Equal(before, (await RahmaLegacyReader.FindCandidatesAsync(master)).Count);

            // ملف غير موجود / ليس .bak / فارغ
            Assert.Contains("SQL Server", (await RahmaRestoreService.RestoreAsync(master, folder!.TrimEnd('/', '\\') + "/no-such-file.bak")).Error);
            Assert.Contains(".bak", (await RahmaRestoreService.RestoreAsync(master, "x.txt")).Error);
            Assert.Contains("اختر", (await RahmaRestoreService.RestoreAsync(master, " ")).Error);
        }
        finally
        {
            await using var conn = new SqlConnection(master);
            await conn.OpenAsync();
            foreach (var db in restored)
            {
                await using var cmd = new SqlCommand($"IF DB_ID('{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END", conn);
                await cmd.ExecuteNonQueryAsync();
            }
            await f.DisposeAsync();
        }
    }
}
