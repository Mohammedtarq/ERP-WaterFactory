using System.Text.RegularExpressions;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

public class CompanyProfileService
{
    public const int MaxLogoBytes = 2 * 1024 * 1024;
    private readonly ProjectDbContext _db;
    public CompanyProfileService(ProjectDbContext db) => _db = db;

    /// <summary>الهوية المحفوظة، أو null إن لم تُضبط بعد (تُستخدم عندها اسم المشروع).</summary>
    public Task<CompanyProfile?> GetAsync() => _db.CompanyProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == 1);

    public async Task<FinanceOperationResult> SaveAsync(CompanyProfile p)
    {
        if (string.IsNullOrWhiteSpace(p.NameAr)) return FinanceOperationResult.Fail("اكتب اسم الشركة بالعربية");
        if (!Regex.IsMatch(p.BrandColor ?? "", "^#[0-9A-Fa-f]{6}$")) return FinanceOperationResult.Fail("لون العلامة يجب أن يكون بصيغة #RRGGBB");
        if (p.Logo is { Length: > MaxLogoBytes }) return FinanceOperationResult.Fail("حجم الشعار أكبر من 2 ميغابايت — صغّر الصورة");
        if (p.Logo is { Length: > 0 } && !IsImage(p.Logo)) return FinanceOperationResult.Fail("الشعار يجب أن يكون صورة PNG أو JPG");

        var existing = await _db.CompanyProfiles.FirstOrDefaultAsync(x => x.Id == 1);
        if (existing is null) _db.CompanyProfiles.Add(existing = new CompanyProfile());
        existing.NameAr = p.NameAr.Trim();
        existing.NameEn = Clean(p.NameEn);
        existing.Phones = string.Join("\n", (p.Phones ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (existing.Phones.Length == 0) existing.Phones = null;
        existing.Address = Clean(p.Address);
        existing.TaxNumber = Clean(p.TaxNumber);
        existing.CommercialRegister = Clean(p.CommercialRegister);
        existing.BrandColor = p.BrandColor!.ToUpperInvariant();
        existing.FooterText = Clean(p.FooterText);
        existing.Logo = p.Logo is { Length: > 0 } ? p.Logo : null;
        existing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return FinanceOperationResult.Ok();
    }

    /// <summary>توقيع PNG أو JPEG.</summary>
    public static bool IsImage(byte[] b) =>
        (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) ||
        (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF);

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
