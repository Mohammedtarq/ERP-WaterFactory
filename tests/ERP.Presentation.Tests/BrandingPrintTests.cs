using ERP.Data.ProjectDb.Entities;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Settings;
using ERP.Presentation.ViewModels.Shell;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>هوية الطباعة المركزية + قواعد إخفاء الأعمدة والصفوف عند الطباعة.</summary>
[Collection("app")]
public class BrandingPrintTests
{
    private readonly AppFixture _f;
    public BrandingPrintTests(AppFixture f) => _f = f;

    [Fact]
    public async Task Branding_is_entered_once_and_used_by_every_printed_document()
    {
        var (shell, dialogs) = await _f.LoginAsync(AppFixture.AdminUser, AppFixture.AdminPassword);
        await shell.IdleAsync();
        var settings = shell.Open<SettingsModuleViewModel>(ModuleCode.SystemSettings);
        var b = settings.Section<BrandingSectionViewModel>();
        settings.SelectedTab = b;
        await settings.LastActivation;
        Assert.Equal("مصنع المياه - البصرة", b.NameAr);                   // قبل الضبط: اسم المشروع
        Assert.Equal(ReportBranding.DefaultColor, b.BrandColor);

        // تحقق: لون غير صالح، صورة غير صالحة
        b.BrandColor = "أزرق";
        await b.SaveCommand.ExecuteAsync();
        Assert.Contains(dialogs.Errors, e => e.Contains("#RRGGBB"));
        dialogs.Errors.Clear();
        var notImage = Path.Combine(Path.GetTempPath(), $"not-image-{Guid.NewGuid():N}.png");
        await File.WriteAllTextAsync(notImage, "hello");
        dialogs.ImageToPick = notImage;
        b.ChooseLogoCommand.Execute(null);
        Assert.Contains(dialogs.Errors, e => e.Contains("PNG أو JPG"));
        Assert.False(b.HasLogo);
        dialogs.Errors.Clear();

        // إدخال الهوية: شعار PNG، هاتفان، عنوان، رقم ضريبي، لون من المقترحات، تذييل
        var png = Path.Combine(Path.GetTempPath(), $"logo-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(png, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 });
        dialogs.ImageToPick = png;
        b.ChooseLogoCommand.Execute(null);
        Assert.True(b.HasLogo);
        b.NameAr = "مصنع مياه البصرة للصناعات الغذائية";
        b.NameEn = "Basra Water Factory";
        b.NewPhone = "0780 000 0000";
        b.AddPhoneCommand.Execute(null);
        b.NewPhone = "0770 111 2222";
        b.AddPhoneCommand.Execute(null);
        Assert.Equal(2, b.Phones.Count);
        b.Address = "البصرة — الزبير";
        b.TaxNumber = "100-200-300";
        b.PickColorCommand.Execute(b.Swatches.Single(s => s.Name == "كحلي"));
        b.FooterText = "شكرًا لتعاملكم معنا";
        await b.SaveCommand.ExecuteAsync();
        Assert.Empty(dialogs.Errors);

        // تُستخدم تلقائيًا في كل مستند يُطبع
        var current = ReportBranding.Current;
        Assert.Equal("مصنع مياه البصرة للصناعات الغذائية", current.CompanyName);
        Assert.Equal(new[] { "0780 000 0000", "0770 111 2222" }, current.Phones);
        Assert.Equal("#0F172A", current.BrandColor);
        Assert.Equal("شكرًا لتعاملكم معنا", current.FooterText);
        Assert.NotNull(current.Logo);
        Assert.Contains("0780 000 0000", current.ContactLine);
        Assert.Contains("الرقم الضريبي: 100-200-300", current.RegistrationLine);

        // تُقرأ من القاعدة عند الدخول التالي
        var reloaded = await ReportBranding.LoadAsync(shell.Session);
        Assert.Equal("Basra Water Factory", reloaded.CompanyNameEn);

        b.PreviewCommand.Execute(null);
        var sample = dialogs.Reports.Last();
        Assert.True(sample.ReceiptCapable);
        Assert.Equal("مصنع مياه البصرة للصناعات الغذائية", sample.Branding!.CompanyName);
        Assert.Empty(_f.Unhandled);
    }

    [Fact]
    public void Hiding_columns_and_zero_rows_produces_a_consistent_printout()
    {
        var r = new ReportDocument { CompanyName = "x", Title = "أرصدة مخزن المنتج التام", ReceiptColumns = new[] { 1, 3, 5 } };
        r.Columns.AddRange(new[] { "#", "الصنف", "التشغيلة", "الرصيد (قطعة)", "ملاحظات", "القيمة" });
        r.Rows.Add(new[] { "1", "ماء 500 مل", "B1", "120", "", "30,000" });
        r.Rows.Add(new[] { "2", "ماء 1.5 لتر", "B2", "0", "", "0" });          // صفري
        r.Rows.Add(new[] { "3", "أغطية", "", "", "", "" });                      // فارغ
        r.Rows.Add(new[] { "4", "ملصقات", "B9", "-5", "عجز", "-1,250 د.ع" });     // سالب ليس صفرًا

        Assert.Equal("أرصدة مخزن المنتج التام", r.Key);
        var v = r.WithVisibility(new HashSet<int> { 4 }, hideZeroRows: true);
        Assert.Equal(new[] { "#", "الصنف", "التشغيلة", "الرصيد (قطعة)", "القيمة" }, v.Columns);
        Assert.Equal(new[] { "1", "4" }, v.Rows.Select(x => x[0]));
        Assert.All(v.Rows, x => Assert.Equal(5, x.Count));
        Assert.Equal(new[] { 1, 3, 4 }, v.ReceiptColumns);                       // فهارس الكاشير تتبع الحذف

        var all = r.WithVisibility(new HashSet<int>(), hideZeroRows: false);
        Assert.Equal(4, all.Rows.Count);
        Assert.Equal("كشف صندوق", new ReportDocument { CompanyName = "x", Title = "كشف صندوق — الصندوق الرئيسي" }.Key);
    }
}
