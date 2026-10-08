using System.Text.RegularExpressions;
using ERP.Data.ProjectDb.Entities;
using ERP.Presentation.ViewModels.Settings;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>
/// شاشة النقل من نظام الرحمة كما يستخدمها المدير: اختيار القاعدة ← تحليل ← تعديل الجرد والربط ← تجربة ومطابقة.
/// التجربة لا تحفظ شيئًا (التنفيذ الفعلي مغطّى في اختبارات التكامل على مشروع نظيف).
/// </summary>
[Collection("app")]
public class RahmaImportScreenTests
{
    private readonly AppFixture _f;
    public RahmaImportScreenTests(AppFixture f) => _f = f;

    [Fact]
    public async Task Analyze_review_and_dry_run_from_the_screen_without_saving()
    {
        var master = Environment.GetEnvironmentVariable("ERP_TEST_MASTER_CONNECTION") ?? throw new InvalidOperationException("ERP_TEST_MASTER_CONNECTION");
        var legacy = $"ALRAHMA_UI{Guid.NewGuid():N}"[..20];
        await CreateLegacyAsync(master, legacy);
        int? addedWarehouse = null;
        await using (var db = _f.NewDb())
            if (!await db.Warehouses.AnyAsync(w => w.WarehouseType == WarehouseType.RawMaterial && w.IsActive))
            {
                var w = new Warehouse { BranchId = await db.Branches.Select(b => b.Id).FirstAsync(), Name = "مواد أولية — اختبار النقل", WarehouseType = WarehouseType.RawMaterial };
                db.Warehouses.Add(w);
                await db.SaveChangesAsync();
                addedWarehouse = w.Id;
            }
        try
        {
            var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
            var settings = shell.Open<SettingsModuleViewModel>(ModuleCode.SystemSettings);
            var imp = settings.Section<RahmaImportSectionViewModel>();
            settings.SelectedTab = imp;
            await settings.IdleAsync();
            await imp.IdleAsync();
            Assert.False(imp.IsImported);
            Assert.False(imp.HasPlan);
            imp.Source = imp.Candidates.Single(c => c.Name == legacy);

            await imp.AnalyzeCommand.ExecuteAsync();
            Assert.Empty(dialogs.Errors);
            Assert.True(imp.HasPlan);
            Assert.Contains(imp.SummaryCards, c => c.Label == "العملاء" && c.Value == "5");
            Assert.Contains(imp.SummaryCards, c => c.Label == "ديون العملاء" && c.Value == "1,250,000 د.ع");
            Assert.Equal(2, imp.Products.Count());
            Assert.Single(imp.LoanEmployees);
            Assert.True(imp.HasWarnings);

            // التنفيذ مقفل قبل تجربة ناجحة
            Assert.False(imp.DryRunPassed);
            await imp.ExecuteCommand.ExecuteAsync();
            Assert.Contains(dialogs.Errors, e => e.Contains("تجربة ومطابقة"));
            dialogs.Errors.Clear();

            // المراجعة: جرد النقد، وربط ملصق مناسبة بعميل ثم إعادته عامًا
            imp.CashBoxes.Single(b => b.Name == "صندوق محمد").CountedAmount = 15_000_000;
            var wedding = imp.Recipes.Single(r => r.SpecialName == "زواج سعيد");
            Assert.Equal(0, wedding.CustomerKey);
            wedding.CustomerKey = imp.CustomerOptions.Single(o => o.Name == "زبون نقدي").Key;
            Assert.Equal("زبون نقدي", wedding.Plan.CustomerName);
            wedding.CustomerKey = 0;
            Assert.Null(wedding.Plan.CustomerLegacyId);
            Assert.Equal(imp.CustomerOptions.Single(o => o.Name == "مطعم الحسون").Key, imp.Recipes.Single(r => r.SpecialName == "مطعم الحسون").CustomerKey);

            await imp.DryRunCommand.ExecuteAsync();
            Assert.Empty(dialogs.Errors);
            Assert.True(imp.DryRunPassed, string.Join("\n", imp.Reconciliation.Where(r => !r.Matches).Select(r => $"{r.Description}: {r.Legacy} ≠ {r.New}")));
            Assert.All(imp.Reconciliation, r => Assert.True(r.Matches));
            Assert.Contains(imp.Reconciliation, r => r.Description == "أرصدة الصناديق (الجرد الفعلي)" && r.New == 18_000_000);
            Assert.StartsWith("✓", imp.StatusMessage);

            imp.PrintCommand.Execute(null);
            var report = dialogs.Reports.Last();
            Assert.Equal("تقرير مطابقة النقل من نظام الرحمة", report.Title);
            Assert.Equal("تجربة — لم يُحفظ شيء", report.Stamp);
            Assert.Contains(report.Rows, r => r[1] == "ديون العملاء (عليهم)" && r[2] == "1,250,000" && r[5] == "✓ متطابق");

            await using var check = _f.NewDb();
            Assert.False(await check.LegacyImports.AnyAsync());
            Assert.False(await check.Items.AnyAsync(i => i.ItemCode.StartsWith("RH-")));
            Assert.False(await check.Customers.AnyAsync(c => c.Name == "مطعم الحسون"));
            Assert.Empty(_f.Unhandled);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await using var conn = new SqlConnection(master);
            await conn.OpenAsync();
            await using (var cmd = new SqlCommand($"ALTER DATABASE [{legacy}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{legacy}];", conn))
                await cmd.ExecuteNonQueryAsync();
            if (addedWarehouse is int id)
            {
                await using var db = _f.NewDb();
                await db.Warehouses.Where(w => w.Id == id).ExecuteDeleteAsync();
            }
        }
    }

    private static async Task CreateLegacyAsync(string master, string name)
    {
        await using var conn = new SqlConnection(master);
        await conn.OpenAsync();
        await using (var c = new SqlCommand($"CREATE DATABASE [{name}] COLLATE Arabic_CI_AS", conn)) await c.ExecuteNonQueryAsync();
        conn.ChangeDatabase(name);
        var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Rahma", "rahma_sample.sql"));
        foreach (var batch in Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline).Where(b => b.Trim().Length > 0))
        {
            await using var cmd = new SqlCommand(batch, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
