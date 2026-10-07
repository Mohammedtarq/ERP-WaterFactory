using System.Globalization;
using ERP.Data.ProjectDb;
using ERP.Data.ProjectDb.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Data.Services;

/// <summary>
/// عرض الكميات بعبوات الصنف (كارتون، شرنك…) بدل القطع — القطع تبقى للحساب الداخلي فقط.
/// صنف بلا عبوات يُعرض بالقطعة، وما تبقّى دون عبوة كاملة يُذكر «قطعة».
/// </summary>
public sealed class PackFormatter
{
    private readonly Dictionary<int, List<ItemPackagingLevel>> _levels;

    public PackFormatter(IEnumerable<ItemPackagingLevel> levels) =>
        _levels = levels.GroupBy(l => l.ItemId)
            .ToDictionary(g => g.Key, g => g.Where(l => l.EquivalentBaseUnits > 0).OrderByDescending(l => l.EquivalentBaseUnits).ToList());

    /// <summary>عبوات الأصناف المطلوبة (أو كل الأصناف إن لم تُحدَّد).</summary>
    public static async Task<PackFormatter> LoadAsync(ProjectDbContext db, IEnumerable<int>? itemIds = null)
    {
        var q = db.ItemPackagingLevels.AsNoTracking();
        if (itemIds is not null)
        {
            var ids = itemIds.Distinct().ToList();
            q = q.Where(l => ids.Contains(l.ItemId));
        }
        return new PackFormatter(await q.ToListAsync());
    }

    public IReadOnlyList<ItemPackagingLevel> LevelsOf(int itemId) => _levels.GetValueOrDefault(itemId) ?? new List<ItemPackagingLevel>();

    /// <summary>كمية صنف واحد: «12 كارتون + 5 قطعة».</summary>
    public string Of(int itemId, decimal pieces)
    {
        if (pieces < 0) return "-" + Of(itemId, -pieces);
        if (pieces == 0) return "0";
        var parts = new List<string>();
        var rest = pieces;
        foreach (var l in LevelsOf(itemId))
        {
            var n = Math.Floor(rest / l.EquivalentBaseUnits);
            if (n <= 0) continue;
            parts.Add($"{Number(n)} {l.LevelName}");
            rest -= n * l.EquivalentBaseUnits;
        }
        if (rest > 0) parts.Add($"{Number(rest)} قطعة");
        return string.Join(" + ", parts);
    }

    /// <summary>
    /// مجموع أصناف مختلفة: كل صنف بعبوته الكبرى ثم يُجمع حسب اسم العبوة — «6,196 كارتون + 4,380 شرنك + 12 قطعة».
    /// </summary>
    public string Total(IEnumerable<(int ItemId, decimal Pieces)> quantities)
    {
        var packs = new Dictionary<string, decimal>();
        var order = new List<string>();
        decimal loose = 0;
        foreach (var (itemId, pieces) in quantities.GroupBy(q => q.ItemId).Select(g => (g.Key, g.Sum(x => x.Pieces))))
        {
            if (pieces <= 0) { loose += pieces; continue; }
            var top = LevelsOf(itemId).FirstOrDefault(l => l.EquivalentBaseUnits > 1);
            if (top is null) { loose += pieces; continue; }
            var n = Math.Floor(pieces / top.EquivalentBaseUnits);
            if (n > 0)
            {
                if (!packs.ContainsKey(top.LevelName)) order.Add(top.LevelName);
                packs[top.LevelName] = packs.GetValueOrDefault(top.LevelName) + n;
            }
            loose += pieces - n * top.EquivalentBaseUnits;
        }
        var parts = order.Select(name => $"{Number(packs[name])} {name}").ToList();
        if (loose != 0) parts.Add($"{Number(loose)} قطعة");
        return parts.Count == 0 ? "0" : string.Join(" + ", parts);
    }

    private static string Number(decimal n) =>
        n == Math.Floor(n) ? n.ToString("N0", CultureInfo.InvariantCulture) : n.ToString("#,0.###", CultureInfo.InvariantCulture);
}
