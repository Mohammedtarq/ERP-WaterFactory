using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Data.Setup;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>
/// تثبيت كامل ببيانات تجريبية + يوم تجربة بشكل بيانات المعمل (يُستعمل لرسم الشاشات ممتلئة):
/// المستندات تُرحَّل، ومخزن التالف مختلط، والأسماء ظاهرة في الأرصدة وكشف الحركة والمستندات لكل نوع مخزن.
/// </summary>
public class TrialDayDataTests : IAsyncLifetime
{
    private static string Master => Environment.GetEnvironmentVariable("ERP_TEST_MASTER_CONNECTION")
        ?? throw new InvalidOperationException("ERP_TEST_MASTER_CONNECTION غير معيّن");
    private readonly string _s = Guid.NewGuid().ToString("N")[..8];
    private string Control => $"ERP_TdCtl_{_s}";
    private string Project => $"ERP_TdPrj_{_s}";

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var conn = new SqlConnection(Master);
        await conn.OpenAsync();
        foreach (var db in new[] { Control, Project })
        {
            await using var cmd = new SqlCommand($"IF DB_ID('{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END", conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Trial_day_fills_every_warehouse_type_with_named_rows()
    {
        var controlCs = new SqlConnectionStringBuilder(Master) { InitialCatalog = Control }.ConnectionString;
        var r = await new ProvisioningService().InstallAsync(new InstallRequest(controlCs, "مصنع", Project, "المدير", "admin", "Admin@123", DemoData: true));
        Assert.True(r.Success, r.ErrorMessage);
        await TrialDayData.SeedAsync(r.ProjectConnectionString);

        await using var db = new ProjectDbContext(new DbContextOptionsBuilder<ProjectDbContext>().UseSqlServer(r.ProjectConnectionString).Options);
        var svc = new WarehouseDocumentService(db);
        int Wh(WarehouseType t) => db.Warehouses.First(w => w.WarehouseType == t).Id;

        // المواد الأولية: الاسم الكامل لكل متغير، والرصيد بفواصل الآلاف
        var raw = await svc.GetBalancesAsync(Wh(WarehouseType.RawMaterial));
        Assert.All(raw, b => Assert.False(b.IsFinished));
        Assert.Contains(raw, b => b.DisplayName == "سدادة — أسود" && b.Breakdown == "1,390,932 قطعة");
        Assert.Contains(raw, b => b.DisplayName == "ليبل - مطعم الحسون" && b.Breakdown == "15,600 قطعة");
        Assert.Contains(raw, b => b.DisplayName == "سدادة — ذهبي" && b.Breakdown == "48,278 قطعة");

        // المنتج التام: الاسم الموحد بالمتغير الخاص
        var fg = await svc.GetBalancesAsync(Wh(WarehouseType.FinishedGoods));
        Assert.All(fg, b => Assert.True(b.IsFinished));
        Assert.Contains(fg, b => b.VariantName == "مطعم الحسون" && b.Breakdown == "38 كارتون" && b.DisplayName.Contains("مطعم الحسون"));
        Assert.Contains(fg, b => b.VariantName == "زواج سعيد" && b.Breakdown == "30 شرنك");

        // التالف مختلط: تام بالاسم الموحد ومادة أولية باسمها
        var damaged = await svc.GetBalancesAsync(Wh(WarehouseType.Damaged));
        Assert.Contains(damaged, b => b.IsFinished && b.DisplayName.Contains("مطعم الحسون") && b.Breakdown == "2 كارتون");
        Assert.Contains(damaged, b => !b.IsFinished && b.DisplayName == "سدادة — ذهبي" && b.Breakdown == "1,200 قطعة");

        // كشف الحركة لكل الأصناف: رصيد جارٍ لكل صنف، ورصيد البداية الافتتاحي مسمّى
        var ledger = await svc.GetLedgerAsync(Wh(WarehouseType.RawMaterial), null, DateTime.Today.AddDays(-1), DateTime.Today);
        Assert.All(ledger, l => Assert.False(string.IsNullOrEmpty(l.BalanceText)));
        Assert.Contains(ledger, l => l.DisplayName == "ليبل - مطعم الحسون" && l.Out == 400 && l.BalanceText == "15,600 قطعة");
        Assert.Contains(ledger, l => l.Reference == "رصيد افتتاحي");
        Assert.DoesNotContain(ledger, l => l.Reference.Contains('#'));

        // حد اسم خاص: الحسون تحت حدّه (38 من 50 كرتون)، وزواج سعيد غير متابَع
        var names = await new VariantAlertService(db).ListAsync();
        Assert.Contains(names, n => n.VariantName == "مطعم الحسون" && n.IsLow && n.MinText == "50 كارتون");
        Assert.Contains(names, n => n.VariantName == "زواج سعيد" && !n.IsTracked);

        // المستندات: متعدد الأسطر يُلخَّص ويُفتح لكل سطر باسمه؛ سطر واحد يُكتب باسمه
        var docs = await svc.GetDocumentsAsync(Wh(WarehouseType.RawMaterial), DateTime.Today, DateTime.Today);
        var receipt = docs.Single(d => d.DocumentType == StockDocumentType.Receipt);
        Assert.Equal("▾ 5 أسطر", receipt.QuantityText.Split(" · ")[0]);
        Assert.Contains(receipt.LineSummaries, l => l.Name == "سدادة — أبيض" && l.Quantity == "1,322,240 قطعة");
        var damagedDoc = docs.Single(d => d.DocumentType == StockDocumentType.Damaged);
        Assert.Equal("1,200 قطعة — سدادة — ذهبي", damagedDoc.QuantityText);
        var fgDocs = await svc.GetDocumentsAsync(Wh(WarehouseType.FinishedGoods), DateTime.Today, DateTime.Today);
        var fgReceipt = fgDocs.Single(d => d.DocumentType == StockDocumentType.Receipt);
        Assert.Contains(fgReceipt.LineSummaries, l => l.Name.Contains("زواج سعيد") && l.Quantity == "30 شرنك");
    }
}
