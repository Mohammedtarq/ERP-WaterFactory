using ERP.Data.ProjectDb.Entities;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Sales;
using ERP.Presentation.ViewModels.Shell;
using ERP.Presentation.ViewModels.Suppliers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>إرسال كشف الحساب على WhatsApp: تصحيح الرقم العراقي، ونص الكشف في الرابط، ورسالة واضحة للرقم الخاطئ.</summary>
[Collection("app")]
public class WhatsAppStatementTests
{
    private readonly AppFixture _f;
    public WhatsAppStatementTests(AppFixture f) => _f = f;

    [Theory]
    [InlineData("07701234567", "9647701234567")]
    [InlineData("7701234567", "9647701234567")]
    [InlineData("+964 770 123 4567", "9647701234567")]
    [InlineData("00964-7701234567", "9647701234567")]
    [InlineData("٠٧٧٠١٢٣٤٥٦٧", "9647701234567")]
    [InlineData("07801111111، 07902222222", "9647801111111")]
    [InlineData("040123456", null)]
    [InlineData("0770123", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Iraqi_numbers_are_normalized(string? input, string? expected) =>
        Assert.Equal(expected, WhatsAppLink.NormalizeIraqPhone(input));

    private static async Task Open(ModuleViewModel module, SectionViewModel section)
    {
        module.SelectedTab = section;
        await module.IdleAsync();
        await section.IdleAsync();
    }

    [Fact]
    public async Task Statement_opens_whatsapp_with_balance_or_explains_the_bad_number()
    {
        await using (var db = _f.NewDb())
        {
            await db.Customers.Where(c => c.Id == _f.SubCustomerId).ExecuteUpdateAsync(u => u.SetProperty(c => c.Phone, "0770 123 4567"));
            await db.Customers.Where(c => c.Id == _f.AgentId).ExecuteUpdateAsync(u => u.SetProperty(c => c.Phone, (string?)null));
            await db.Suppliers.Where(x => x.Id == _f.SupplierId).ExecuteUpdateAsync(u => u.SetProperty(x => x.Phone, "07801112233"));
        }
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        var sales = shell.Open<SalesModuleViewModel>(ModuleCode.Sales);
        var st = sales.Statement;
        await Open(sales, st);

        await st.WhatsAppCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("اختر العميل"));
        dialogs.Errors.Clear();

        st.Customer = st.Customers.Single(c => c.Id == _f.AgentId);
        await st.IdleAsync();
        await st.WhatsAppCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("غير صحيح") && e.Contains("بطاقة العميل"));
        Assert.Empty(dialogs.Urls);
        dialogs.Errors.Clear();

        st.Customer = st.Customers.Single(c => c.Id == _f.SubCustomerId);
        await st.IdleAsync();
        await st.WhatsAppCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        var url = Assert.Single(dialogs.Urls);
        Assert.StartsWith("https://wa.me/9647701234567?text=", url);
        var text = Uri.UnescapeDataString(url[(url.IndexOf("text=") + 5)..]);
        Assert.Contains(st.Customer!.Name, text);
        Assert.Contains(st.BalanceText, text);
        Assert.Contains("WhatsApp", st.StatusMessage);

        var supModule = shell.Open<SuppliersModuleViewModel>(ModuleCode.Suppliers);
        var sup = supModule.Section<SupplierStatementSectionViewModel>();
        await Open(supModule, sup);
        sup.Supplier = sup.SuppliersLookup.Single(x => x.Id == _f.SupplierId);
        await sup.IdleAsync();
        await sup.WhatsAppCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);
        Assert.StartsWith("https://wa.me/9647801112233?text=", dialogs.Urls.Last());
    }
}
