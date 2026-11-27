using ERP.Data.ProjectDb.Entities;
using ERP.Presentation.ViewModels.Sales;
using ERP.Presentation.ViewModels.Shell;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>
/// حدود تنبيه الأسماء الخاصة (المرحلة الأولى المتفق عليها): حد يدوي بالعبوة لكل اسم خاص وللأساسي،
/// الاسم بلا حد لا يُتابَع، والاسم تحت حدّه يظهر في الجرس ويفتح الشاشة.
/// </summary>
[Collection("app")]
public class VariantAlertsTests
{
    private readonly AppFixture _f;
    public VariantAlertsTests(AppFixture f) => _f = f;

    [Fact]
    public async Task Manual_limit_per_special_name_in_packs_shows_in_the_bell_and_untracked_names_stay_silent()
    {
        int recipeId, quietId;
        await using (var db = _f.NewDb())
        {
            var admin = await db.Users.Select(u => u.Id).FirstAsync();
            var product = new Item { ItemCode = "VA-330", ItemName = "ماء 330 مل (حدود الأسماء)", SizeMl = 330, SourcingMethod = SourcingMethod.Manufactured };
            db.Items.Add(product);
            await db.SaveChangesAsync();
            var piece = new ItemPackagingLevel { ItemId = product.Id, LevelName = "قطعة", EquivalentBaseUnits = 1 };
            db.ItemPackagingLevels.Add(piece);
            await db.SaveChangesAsync();
            db.ItemPackagingLevels.Add(new ItemPackagingLevel { ItemId = product.Id, LevelName = "شرنك", ParentLevelId = piece.Id, ContainsQuantity = 20, EquivalentBaseUnits = 20 });
            var recipe = new CustomRecipe { FinishedItemId = product.Id, Name = "زواج سعيد — حدود", ShortName = "زواج سعيد (حدود)" };
            var quiet = new CustomRecipe { FinishedItemId = product.Id, Name = "كافيه هادئ — حدود", ShortName = "كافيه هادئ (حدود)" };
            db.CustomRecipes.AddRange(recipe, quiet);
            await db.SaveChangesAsync();
            var batch = new ItemBatch { ItemId = product.Id, BatchNumber = "VA-ZS", CustomRecipeId = recipe.Id };
            var quietBatch = new ItemBatch { ItemId = product.Id, BatchNumber = "VA-Q", CustomRecipeId = quiet.Id };
            db.ItemBatches.AddRange(batch, quietBatch);
            await db.SaveChangesAsync();
            db.StockTransactions.AddRange(
                // 4 شرنك من زواج سعيد في المنتج التام، وبيع 6 شرنك خلال الأسابيع الماضية
                new StockTransaction { ItemId = product.Id, WarehouseId = _f.MainWarehouseId, BatchId = batch.Id, QuantityBaseUnits = 200, TransactionType = StockTransactionType.Receipt, CreatedByUserId = admin },
                new StockTransaction { ItemId = product.Id, WarehouseId = _f.MainWarehouseId, BatchId = batch.Id, QuantityBaseUnits = -120, TransactionType = StockTransactionType.SalesIssue, CreatedByUserId = admin },
                new StockTransaction { ItemId = product.Id, WarehouseId = _f.MainWarehouseId, BatchId = quietBatch.Id, QuantityBaseUnits = 20, TransactionType = StockTransactionType.Receipt, CreatedByUserId = admin });
            await db.SaveChangesAsync();
            (recipeId, quietId) = (recipe.Id, quiet.Id);
        }

        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var sales = shell.Open<SalesModuleViewModel>(ModuleCode.Sales);
        await sales.IdleAsync();
        var screen = sales.VariantAlerts;
        sales.SelectedTab = screen;
        await sales.IdleAsync();
        await sales.LastActivation;
        await screen.IdleAsync();

        var wedding = screen.Rows.Single(r => r.Row.RecipeId == recipeId);
        Assert.Equal("زواج سعيد (حدود)", wedding.VariantName);
        Assert.Equal("شرنك", wedding.PackName);                       // الحد بالعبوة لا بالقطعة
        Assert.Equal("4 شرنك", wedding.BalanceText);
        Assert.Equal("غير متابَع", wedding.StatusText);
        Assert.Contains(screen.Rows, r => r.Row.ItemId == wedding.Row.ItemId && r.Row.RecipeId is null);   // الأساسي له سطر أيضًا
        Assert.Equal("37.3 يوم", wedding.DaysText);                    // 80 قطعة ÷ (120 ÷ 56) يوم
        Assert.Contains("اسم خاص بلا حدّ", screen.UntrackedText);

        // بلا حدود لا ينبّه شيء من هذه الأسماء
        await shell.RefreshAlertsAsync();
        Assert.DoesNotContain(shell.Alerts, a => a.Key == "variant-low" && a.Detail.Contains("زواج سعيد (حدود)"));

        // حد 10 شرنك لزواج سعيد ← تحت الحد وفي الجرس؛ الهادئ بلا حد يبقى صامتًا
        wedding.MinPacks = 10;
        Assert.True(wedding.IsLow);
        await screen.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.Contains("حُفظت", screen.StatusMessage);
        wedding = screen.Rows.Single(r => r.Row.RecipeId == recipeId);
        Assert.Equal("تحت الحد", wedding.StatusText);
        Assert.Equal(10, wedding.MinPacks);

        await shell.RefreshAlertsAsync();
        var alert = Assert.Single(shell.Alerts, a => a.Key == "variant-low");
        Assert.Contains("زواج سعيد (حدود): 4 شرنك من 10 شرنك", alert.Detail);
        Assert.DoesNotContain("كافيه هادئ", alert.Detail);
        Assert.Same(screen, shell.OpenAlert(alert));                   // الضغط على التنبيه يفتح الشاشة

        // تصفية «تحت الحد» وإلغاء المتابعة بإفراغ الحد
        screen.Show = screen.ShowOptions[1];
        Assert.Contains(screen.VisibleRows, r => r.Row.RecipeId == recipeId);
        Assert.DoesNotContain(screen.VisibleRows, r => r.Row.RecipeId == quietId);
        screen.Show = screen.ShowOptions[0];
        screen.Rows.Single(r => r.Row.RecipeId == recipeId).MinPacks = null;
        await screen.SaveCommand.ExecuteAsync();
        await shell.RefreshAlertsAsync();
        Assert.DoesNotContain(shell.Alerts, a => a.Key == "variant-low" && a.Detail.Contains("زواج سعيد (حدود)"));
    }
}
