using ERP.Data.ProjectDb.Entities;
using ERP.Data.Services;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>الكمية رقمًا فقط للمنتج التام: الاسم الموحد يحمل نوع العبوة، فلا تكرار «كارتون 40×330 | 10 كارتون».</summary>
public class PackCountTests
{
    private static PackFormatter Packs() => new(new[]
    {
        new ItemPackagingLevel { ItemId = 1, LevelName = "قطعة", EquivalentBaseUnits = 1 },
        new ItemPackagingLevel { ItemId = 1, LevelName = "شرنك", EquivalentBaseUnits = 20 },
        new ItemPackagingLevel { ItemId = 2, LevelName = "قطعة", EquivalentBaseUnits = 1 }
    });

    [Fact]
    public void Count_is_a_plain_number_of_top_packs_with_loose_pieces_only_when_needed()
    {
        var p = Packs();
        Assert.Equal("10", p.Count(1, 200));
        Assert.Equal("1,000", p.Count(1, 20_000));
        Assert.Equal("10 + 5 قطعة", p.Count(1, 205));
        Assert.Equal("5 قطعة", p.Count(1, 5));
        Assert.Equal("-3", p.Count(1, -60));
        Assert.Equal("7", p.Count(2, 7));              // صنف بلا عبوة كبرى: بالقطعة
        Assert.Equal("10 شرنك", p.Of(1, 200));          // الدالة القديمة للمواد الأولية وغيرها كما هي
    }
}
