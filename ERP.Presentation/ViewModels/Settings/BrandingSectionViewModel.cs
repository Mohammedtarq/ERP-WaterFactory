using System.Collections.ObjectModel;
using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using ERP.Presentation.Mvvm;
using ERP.Presentation.Services;
using ERP.Presentation.ViewModels.Shell;

namespace ERP.Presentation.ViewModels.Settings;

public class PhoneEntry : ObservableObject
{
    private string _value = "";
    public string Value { get => _value; set => SetProperty(ref _value, value); }
}

public record ColorSwatch(string Hex, string Name);

/// <summary>
/// هوية الطباعة المركزية: تُدخل مرة واحدة، وتظهر تلقائيًا في رأس وتذييل كل فاتورة وسند وقيد وتقرير مطبوع.
/// </summary>
public class BrandingSectionViewModel : SectionViewModel
{
    private string _nameAr = "", _brandColor = ReportBranding.DefaultColor, _newPhone = "";
    private string? _nameEn, _address, _taxNumber, _commercialRegister, _footerText;
    private byte[]? _logo;

    public BrandingSectionViewModel(AppSession s, IDialogService d)
        : base(s, d, ModuleCode.SystemSettings, "هوية الطباعة", Icons.Star, "#0F766E", "الشعار واسم الشركة والاتصال ولون العلامة — في كل مستند مطبوع")
    {
        AddPhoneCommand = new RelayCommand(() =>
        {
            if (string.IsNullOrWhiteSpace(NewPhone)) return;
            Phones.Add(new PhoneEntry { Value = NewPhone.Trim() });
            NewPhone = "";
        });
        RemovePhoneCommand = new RelayCommand(p => { if (p is PhoneEntry e) Phones.Remove(e); });
        ChooseLogoCommand = new RelayCommand(ChooseLogo);
        RemoveLogoCommand = new RelayCommand(() => Logo = null);
        PickColorCommand = new RelayCommand(p => { if (p is ColorSwatch c) BrandColor = c.Hex; });
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        PreviewCommand = new RelayCommand(() => Dialogs.ShowReport(SampleReport()));
    }

    protected override bool HasPendingInput => true;   // نموذج يُحرَّر مباشرة

    public string NameAr { get => _nameAr; set => SetProperty(ref _nameAr, value); }
    public string? NameEn { get => _nameEn; set => SetProperty(ref _nameEn, value); }
    public ObservableCollection<PhoneEntry> Phones { get; } = new();
    public string NewPhone { get => _newPhone; set => SetProperty(ref _newPhone, value); }
    public string? Address { get => _address; set => SetProperty(ref _address, value); }
    public string? TaxNumber { get => _taxNumber; set => SetProperty(ref _taxNumber, value); }
    public string? CommercialRegister { get => _commercialRegister; set => SetProperty(ref _commercialRegister, value); }
    public string BrandColor { get => _brandColor; set => SetProperty(ref _brandColor, value); }
    public string? FooterText { get => _footerText; set => SetProperty(ref _footerText, value); }
    public byte[]? Logo { get => _logo; private set { if (SetProperty(ref _logo, value)) OnPropertyChanged(nameof(HasLogo)); } }
    public bool HasLogo => Logo is { Length: > 0 };

    /// <summary>ألوان مقترحة: تركواز وكحلي الواجهة أولًا، ثم ألوان داكنة تُطبع بوضوح.</summary>
    public IReadOnlyList<ColorSwatch> Swatches { get; } = new[]
    {
        new ColorSwatch("#0F766E", "تركواز الواجهة"), new ColorSwatch("#0F172A", "كحلي"), new ColorSwatch("#1D4ED8", "أزرق"),
        new ColorSwatch("#047857", "أخضر"), new ColorSwatch("#7F1D1D", "عنابي"), new ColorSwatch("#B45309", "برتقالي داكن"),
        new ColorSwatch("#4C1D95", "بنفسجي"), new ColorSwatch("#334155", "رمادي")
    };

    public RelayCommand AddPhoneCommand { get; }
    public RelayCommand RemovePhoneCommand { get; }
    public RelayCommand ChooseLogoCommand { get; }
    public RelayCommand RemoveLogoCommand { get; }
    public RelayCommand PickColorCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand PreviewCommand { get; }

    public override async Task LoadAsync()
    {
        await using var db = Session.NewDb();
        var p = await new CompanyProfileService(db).GetAsync();
        NameAr = p?.NameAr ?? Session.ProjectName;
        NameEn = p?.NameEn;
        Phones.Clear();
        foreach (var ph in (p?.Phones ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            Phones.Add(new PhoneEntry { Value = ph });
        Address = p?.Address;
        TaxNumber = p?.TaxNumber;
        CommercialRegister = p?.CommercialRegister;
        BrandColor = p?.BrandColor ?? ReportBranding.DefaultColor;
        FooterText = p?.FooterText;
        Logo = p?.Logo;
    }

    private void ChooseLogo()
    {
        var path = Dialogs.PickImageFile();
        if (path is null) return;
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (IOException ex) { Dialogs.Error("تعذّر قراءة الصورة: " + ex.Message); return; }
        if (bytes.Length > CompanyProfileService.MaxLogoBytes) { Dialogs.Error("حجم الشعار أكبر من 2 ميغابايت — صغّر الصورة"); return; }
        if (!CompanyProfileService.IsImage(bytes)) { Dialogs.Error("اختر صورة PNG أو JPG"); return; }
        Logo = bytes;
    }

    private async Task SaveAsync()
    {
        if (!Require(CanEdit, "تعديل هوية الطباعة")) return;
        await using var db = Session.NewDb();
        var r = await new CompanyProfileService(db).SaveAsync(new CompanyProfile
        {
            NameAr = NameAr, NameEn = NameEn, Phones = string.Join("\n", Phones.Select(p => p.Value)), Address = Address,
            TaxNumber = TaxNumber, CommercialRegister = CommercialRegister, BrandColor = BrandColor.Trim(), FooterText = FooterText, Logo = Logo
        });
        if (!r.Success) { Dialogs.Error(r.ErrorMessage!); return; }
        await ReportBranding.RefreshAsync(Session);
        StatusMessage = "حُفظت هوية الطباعة — تظهر الآن في كل المستندات المطبوعة";
    }

    /// <summary>مستند تجريبي بالهوية المدخلة (قبل الحفظ) لمعاينة الشكل.</summary>
    public ReportDocument SampleReport()
    {
        var r = new ReportDocument
        {
            CompanyName = NameAr, Title = "فاتورة مبيعات (نموذج)", PrintedBy = Session.FullName, Key = "BrandingSample",
            ReceiptCapable = true, ReceiptColumns = new[] { 1, 3, 5, 6 },
            Branding = new ReportBranding
            {
                CompanyName = NameAr, CompanyNameEn = NameEn, Phones = Phones.Select(p => p.Value).ToList(), Address = Address,
                TaxNumber = TaxNumber, CommercialRegister = CommercialRegister, BrandColor = BrandColor, FooterText = FooterText, Logo = Logo
            }
        };
        r.Field("رقم الفاتورة", "SI-2026-000123").Field("التاريخ", DateTime.Today.ToString("yyyy/MM/dd"))
         .Field("العميل", "محل أبو حيدر").Field("طريقة الدفع", "نقدي");
        r.Columns.AddRange(new[] { "#", "الصنف", "الوحدة", "الكمية", "القطع", "السعر", "المبلغ" });
        r.Rows.Add(new[] { "1", "ماء 500 مل (W-500)", "كارتون", "10", "120", "2,400", "24,000" });
        r.Rows.Add(new[] { "2", "ماء 1.5 لتر (W-1500)", "شرنك", "5", "30", "3,000", "15,000" });
        r.Total("المجموع", "39,000 د.ع").Total("الإجمالي", "39,000 د.ع", true).Total("الإجمالي كتابةً", ArabicNumberWords.Amount(39000));
        r.Signatures.AddRange(new[] { "المستلم", "المحاسب" });
        return r;
    }
}
