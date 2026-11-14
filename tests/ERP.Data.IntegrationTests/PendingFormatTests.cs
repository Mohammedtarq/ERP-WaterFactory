using ERP.Data.Services;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>عرض «بيع بانتظار الإنتاج» بوحدة البيع في الفاتورة مع القطع بين قوسين (التخزين بالقطعة دائمًا).</summary>
public class PendingFormatTests
{
    [Theory]
    [InlineData(40, "شرنك", 20, "2 شرنك (40 قطعة)")]
    [InlineData(45, "شرنك", 20, "2 شرنك و5 قطع (45 قطعة)")]
    [InlineData(1_200, "كارتون", 600, "2 كارتون (1,200 قطعة)")]
    [InlineData(5, "شرنك", 20, "5 قطعة")]          // أقل من وحدة كاملة: بالقطع فقط
    [InlineData(40, "قطعة", 1, "40 قطعة")]           // البيع بالقطعة
    [InlineData(40, null, 1, "40 قطعة")]             // لا فاتورة / وحدة مجهولة
    public void Quantity_is_shown_in_the_sale_unit_with_pieces_in_brackets(decimal pieces, string? level, decimal per, string expected) =>
        Assert.Equal(expected, PendingProductionService.FormatPacks(pieces, level, per));

    [Fact]
    public void Row_texts_use_the_same_unit_for_sold_settled_and_open()
    {
        var row = new PendingProductionService.OpenShortageRow(1, DateTime.Today, "ماء 330", "مخزن", "SI-1", 40, 0, 40, null, "شرنك", 20);
        Assert.Equal(("2 شرنك (40 قطعة)", "0 قطعة", "2 شرنك (40 قطعة)"), (row.QuantityText, row.SettledText, row.OpenText));
    }
}
