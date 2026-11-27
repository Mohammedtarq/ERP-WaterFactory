using ERP.Data.ProjectDb.Entities;
using ERP.Presentation.Mvvm;
using ERP.Presentation.ViewModels.Shell;
using ERP.Presentation.ViewModels.Warehouse;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>
/// بحث المخزن (الصندوق في صف التبويبات): يصفّي أرصدة المخزن بالصنف أو الكود أو التشغيلة مع توحيد الهمزات والتاء المربوطة،
/// وينقل إلى تبويب الأرصدة، والطباعة تحمل النتائج المعروضة فقط.
/// </summary>
[Collection("app")]
public class WarehouseSearchTests
{
    private readonly AppFixture _f;
    public WarehouseSearchTests(AppFixture f) => _f = f;

    [Fact]
    public void Arabic_normalization_unifies_alef_forms_teh_marbuta_and_diacritics()
    {
        Assert.Equal(ArabicText.Normalize("أسامة"), ArabicText.Normalize("اسامه"));
        Assert.Equal("اكاديميه", ArabicText.Normalize("أكاديمية"));
        Assert.Equal("مدرسه", ArabicText.Normalize("مَدْرَسَة"));
        Assert.Equal("", ArabicText.Normalize("   "));
    }

    [Fact]
    public async Task Search_filters_balances_by_name_code_and_batch_jumps_to_the_balances_tab_and_prints_the_results()
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var wh = shell.Open<WarehouseModuleViewModel>(ModuleCode.Warehouse);
        await shell.IdleAllAsync();
        var items = wh.Section<ItemsSectionViewModel>();
        async Task Open(SectionViewModel s) { wh.SelectedTab = s; await wh.LastActivation; await s.IdleAsync(); }
        void NoErrors(string step) => Assert.True(dialogs.Errors.Count == 0, $"{step}: " + string.Join(" | ", dialogs.Errors));

        await Open(items);
        foreach (var (code, name) in new[] { ("SRCH-1", "ليبل أكاديمية الأمل"), ("SRCH-2", "غطاء أبيض بحث") })
        {
            await items.NewCommand.ExecuteAsync();
            items.Editor!.ItemCode = code;
            items.Editor.ItemName = name;
            items.Editor.SourcingMethod = SourcingMethod.Purchased;
            await items.SaveCommand.ExecuteAsync();
            await items.IdleAsync();
        }
        NoErrors("الأصناف");

        var raw = wh.Workspaces.First(w => w.WarehouseType == WarehouseType.RawMaterial);
        await Open(raw);
        foreach (var (code, batch) in new[] { ("SRCH-1", "BATCH-AMAL"), ("SRCH-2", "BATCH-GHT") })
        {
            raw.Operation = raw.OperationOptions.Single(o => o.Value == StockDocumentType.Receipt);
            raw.PartyName = "مورد البحث";
            raw.LineItem = raw.ItemsLookup.Single(i => i.ItemCode == code);
            await raw.IdleAsync();
            raw.LineQuantity = 100;
            raw.LineNewBatch = batch;
            raw.LineExpiry = DateTime.Today.AddYears(1);
            await raw.AddLineCommand.ExecuteAsync();
            await raw.SaveCommand.ExecuteAsync();
            await raw.IdleAsync();
        }
        NoErrors("الاستلام");
        Assert.Equal(raw.Balances.Count, raw.VisibleBalances.Count);        // بلا بحث: كل الأرصدة
        Assert.False(raw.HasSearch);

        // بالاسم مع همزة مختلفة، فينتقل لتبويب الأرصدة
        raw.SelectedTabIndex = 0;
        raw.SearchText = "اكاديميه";
        Assert.Equal(1, raw.SelectedTabIndex);
        var hit = Assert.Single(raw.VisibleBalances);
        Assert.Equal("SRCH-1", hit.ItemCode);
        Assert.True(raw.HasSearch);
        Assert.Equal($"1 من {raw.Balances.Count}", raw.BalancesCountText);

        // بالكود، وبالتشغيلة، وبكلمتين معًا
        raw.SearchText = "srch-2";
        Assert.Equal("SRCH-2", Assert.Single(raw.VisibleBalances).ItemCode);
        raw.SearchText = "BATCH-AMAL";
        Assert.Equal("SRCH-1", Assert.Single(raw.VisibleBalances).ItemCode);
        raw.SearchText = "غطاء بحث";
        Assert.Equal("SRCH-2", Assert.Single(raw.VisibleBalances).ItemCode);
        raw.SearchText = "غطاء اكاديميه";
        Assert.Empty(raw.VisibleBalances);

        // الطباعة: النتائج المعروضة وحدها مع بيان التصفية
        raw.SearchText = "الأمل";
        raw.PrintBalancesCommand.Execute(null);
        var report = dialogs.Reports.Last();
        Assert.Single(report.Rows);
        Assert.Contains(report.HeaderFields, f => f.Label == "تصفية البحث" && f.Value == "الأمل");

        // المسح يعيد كل الأرصدة، وبقاؤها بعد التحديث يحترم البحث
        raw.ClearSearchCommand.Execute(null);
        Assert.Equal(raw.Balances.Count, raw.VisibleBalances.Count);
        Assert.Equal("", raw.BalancesCountText);
        raw.SearchText = "SRCH-1";
        await raw.RefreshCommand.ExecuteAsync();
        await raw.IdleAsync();
        Assert.Single(raw.VisibleBalances);

        Assert.Empty(dialogs.Errors);
        await shell.IdleAllAsync();
        Assert.Empty(_f.Unhandled);
    }
}
