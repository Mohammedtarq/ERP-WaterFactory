using ERP.Presentation.ViewModels.Production;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>معالج «متغير جديد»: يعرض ما يتغير عادة (السدادة والليبل) فقط، والباقي (الأمبولة والكارتون) خلف «إظهار». السدادة بلا «صنف جديد باسم».</summary>
public class VariantWizardRolesTests
{
    [Theory]
    [InlineData("غطاء", "غطاء أخضر", true, false, true)]       // سدادة: تغيير بلا صنف جديد
    [InlineData("سدادة", "سدادة 28 مم", true, false, true)]
    [InlineData("لاصق", "ليبل الرحمة", true, true, true)]      // ليبل: تغيير + صنف جديد باسم الزبون
    [InlineData("ليبل", "ليبل عام (الرحمة)", true, true, true)]
    [InlineData("امبولة", "امبولة 330", false, false, true)]    // لا تتغير: مخفية
    [InlineData("كارتون", "كارتون 40", false, false, true)]
    public void Only_cap_and_label_are_shown_by_default(string role, string baseName, bool changeable, bool isLabel, bool allowNewForNonCap)
    {
        var row = new VariantRoleRow(role, baseName, 1, true);
        Assert.Equal(changeable, row.Changeable);
        Assert.Equal(isLabel, row.LooksLikeLabel);
        Assert.Equal(!row.IsCap && allowNewForNonCap, row.AllowNew);
    }

    [Fact]
    public void Cap_never_offers_a_new_item_but_the_label_does()
    {
        Assert.False(new VariantRoleRow("غطاء", "غطاء أخضر", 1, true).AllowNew);
        Assert.True(new VariantRoleRow("لاصق", "ليبل الرحمة", 1, true).AllowNew);
        // مكوّن بلا دور يُعرَّف من اسم مادته
        Assert.True(new VariantRoleRow("ليبل الرحمة", "ليبل الرحمة", 2, false).Changeable);
        Assert.False(new VariantRoleRow("مطعم بيت الشناشيل", "مطعم بيت الشناشيل", 3, false).Changeable);
    }
}
