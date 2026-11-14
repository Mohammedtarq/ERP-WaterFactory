using ERP.Presentation.Services;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>المتوفر بجانب الصنف في قوائم الاختيار: العدد فقط بعبوة الصنف الكبرى.</summary>
public class StockHintTests
{
    [Fact]
    public void Shows_the_count_of_top_packs_only_with_low_and_out_states()
    {
        Assert.Equal(("120", "ok"), StockHint.Of(4800, 40, 100));
        Assert.Equal(("6 · قليل", "low"), StockHint.Of(240, 40, 300));      // عند حد التنبيه أو دونه
        Assert.Equal(("نفد", "out"), StockHint.Of(0, 40, 100));
        Assert.Equal(("25", "ok"), StockHint.Of(25, 40, null));              // أقل من عبوة: يُعرض بالقطع
        Assert.Equal(("1,200", "ok"), StockHint.Of(1200, 1, null));          // بلا عبوات كبرى
    }
}
