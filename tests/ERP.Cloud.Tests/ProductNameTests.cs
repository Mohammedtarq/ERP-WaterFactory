using ERP.Cloud.Contracts;
using Xunit;

namespace ERP.Cloud.Tests;

/// <summary>اسم المنتج الموحد (قرار المالك): «{العبوة} {عدد القطع}×{الحجم} {المتغير}».</summary>
public class ProductNameTests
{
    [Fact]
    public void Unified_name_follows_the_owner_format()
    {
        Assert.Equal("كارتون 40×330 مطعم داون تاون", ProductName.Of("كارتون", 40, 330, "ماء 330", "مطعم داون تاون"));
        Assert.Equal("كارتون 40×330 الرحمة", ProductName.Of("كارتون", 40, 330, "ماء 330", "الرحمة"));
        Assert.Equal("شرنك 20×330 زواج سعيد", ProductName.Of("شرنك", 20, 330, "ماء 330", "زواج سعيد"));
        Assert.Equal("قطعة 330 الرحمة", ProductName.Of("قطعة", 1, 330, "ماء 330", "الرحمة"));
        Assert.Equal("كارتون 6×1.5 لتر", ProductName.Of("كارتون", 6, 1500, "ماء", null));
        Assert.Equal("كارتون 40 — ماء معدني الرحمة", ProductName.Of("كارتون", 40, null, "ماء معدني", "الرحمة"));   // الحجم مجهول
    }

    [Fact]
    public void Variant_and_size_are_found_without_extra_typing()
    {
        Assert.Equal("داون تاون", ProductName.Variant("داون تاون", "مطعم داون تاون", "وصفة داون تاون", false, "الرحمة"));
        Assert.Equal("مطعم داون تاون", ProductName.Variant(null, "مطعم داون تاون", "وصفة داون تاون", false, "الرحمة"));
        Assert.Equal("زواج سعيد", ProductName.Variant(" ", null, "زواج سعيد", false, "الرحمة"));
        Assert.Equal("الرحمة", ProductName.Variant(null, null, null, true, "الرحمة"));
        Assert.Equal("", ProductName.Variant(null, null, null, true, null));                          // لم يُضبط اسم الأساسي
        Assert.Equal("كارتون 40×330", ProductName.Of("كارتون", 40, 330, "ماء", ProductName.Variant(null, null, null, true, null)));

        Assert.Equal(330m, ProductName.ParseSizeMl("ماء الرحمة 330 مل كارتون 40"));
        Assert.Equal(500m, ProductName.ParseSizeMl("ماء 500مل"));
        Assert.Equal(1500m, ProductName.ParseSizeMl("قنينة 1.5 لتر"));
        Assert.Equal(330m, ProductName.ParseSizeMl("ماء ٣٣٠ مل"));
        Assert.Equal(600m, ProductName.ParseSizeMl("ماء 600"));
        Assert.Null(ProductName.ParseSizeMl("ماء معمل الرحمة"));
        Assert.Null(ProductName.ParseSizeMl("ماء 40"));
    }
}
