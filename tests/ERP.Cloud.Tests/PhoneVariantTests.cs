using System.Text.Json;
using ERP.Cloud.Contracts;
using ERP.RepApp.Core;
using Xunit;

namespace ERP.Cloud.Tests;

/// <summary>
/// الهاتف مع المتغيرات: الأساسي («الرحمة») وطلب المطعم («داون تاون») في السيارة نفسها منتجان مستقلان بالاسم الموحد،
/// لكل منهما رصيده وسعره، ولا يُباع من أحدهما على حساب الآخر، والوصل بالاسم الموحد.
/// </summary>
public class PhoneVariantTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rep-variants-" + Guid.NewGuid().ToString("N"));
    public PhoneVariantTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Base_and_restaurant_variant_are_separate_products_with_own_stock_price_and_name()
    {
        var store = new LocalStore(Path.Combine(_dir, "rep.db"));
        store.SaveLink(new LinkCode("https://cloud.test", new string('A', 64)));
        // سعر العبوة كما يُكتب: الأساسي 6,000 والطلب الخاص 7,000 (لا عبوة قطعة في الهاتف)
        var carton = new SnapshotLevel(11, "كارتون", 40, 6_000);
        var product = new SnapshotProduct(5, "W330", "ماء 330", 150, new() { carton },
                                          330, "الرحمة", new() { new SnapshotVariant(7, "داون تاون", 175, new() { new SnapshotPackPrice(11, 7_000) }) });
        var customers = new List<SnapshotCustomer>
        {
            new(1, "سوبر ماركت", null, null, "Direct", null, null, 0),
            new(2, "سوق الزبير", null, null, "Direct", null, null, 0)
        };
        var snap = new RepSnapshot(9, "علي", DateTime.UtcNow, true, 2, 0, new() { product }, customers, new(),
                                   new() { new SnapshotStock(5, 200), new SnapshotStock(5, 80, 7) }, CompanyName: "معمل الرحمة");
        store.SaveSnapshot(JsonSerializer.Serialize(snap, new JsonSerializerOptions(JsonSerializerDefaults.Web)), "\"x\"");
        var work = new RepWork(store);

        // القائمة: الأساسي ثم المطعم، كل بالاسم الموحد ورصيده
        var options = work.LoadedOptions();
        Assert.Equal(new[] { "كارتون 40×330 الرحمة", "كارتون 40×330 داون تاون" }, options.Select(o => o.Text));
        Assert.Equal(new[] { 5m, 2m }, options.Select(o => o.Available));

        // السعر بالعبوة كما يُكتب، موحّد لكل الزبائن: الطلب الخاص بسعره، والأساسي بسعره؛ ونسخة قديمة بلا سعر عبوة = القطعة × قطعها
        Assert.Equal(7_000m, RepWork.PackPrice(product, carton, 7));
        Assert.Equal(6_000m, RepWork.PackPrice(product, carton));
        Assert.Equal(40 * 150m, RepWork.PackPrice(product, carton with { Price = null }));

        // لا يُباع من المطعم أكثر من رصيده ولو كان الأساسي يكفي
        var tooMuch = work.CheckSale("CashSale", 1, new[] { new SaleLine(5, 11, 3, 7) });
        Assert.Contains("كارتون 40×330 داون تاون لا يكفي", tooMuch.Error);

        var sale = work.SaveSale("CashSale", 1, new[] { new SaleLine(5, 11, 2, 7), new SaleLine(5, 11, 1) });
        Assert.Equal(2 * 7_000m + 6_000m, sale.Amount);
        Assert.Equal(new[] { "كارتون 40×330 داون تاون", "كارتون 40×330 الرحمة" }, sale.Lines!.Select(l => l.Item));
        var payload = JsonSerializer.Deserialize<SalePayload>(sale.Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(new int?[] { 7, null }, payload.Lines.Select(l => l.CustomRecipeId));   // المعمل يصرف من تشغيلة المتغير نفسه
        var byVariant = work.VanStockByVariant();
        Assert.Equal((160m, 0m), (byVariant[(5, null)], byVariant[(5, 7)]));
        Assert.Equal(160m, work.VanStock()[5]);
        Assert.Contains("كارتون 40×330 داون تاون", work.ReceiptFor(sale)!.TextLines());
        Assert.Contains("بيع نقدي — سوبر ماركت: 2 كارتون 40×330 داون تاون، 1 كارتون 40×330 الرحمة", sale.Summary);
    }
}
