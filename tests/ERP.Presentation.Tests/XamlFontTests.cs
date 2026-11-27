using System.Xml.Linq;
using Xunit;

namespace ERP.Presentation.Tests;

/// <summary>
/// خط الأيقونات على MenuItem نفسه يرثه كل ما تحته من عناصر القائمة المنسدلة فيظهر نصها العربي مربعات.
/// يوضع على TextBlock داخل الرأس فقط.
/// </summary>
public class XamlFontTests
{
    [Fact]
    public void No_menu_item_carries_the_icon_font_because_its_submenu_would_inherit_it()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Xaml");
        var offenders = Directory.GetFiles(root, "*.xaml", SearchOption.AllDirectories)
            .SelectMany(f => XDocument.Load(f).Descendants().Where(e => e.Name.LocalName == "MenuItem"
                                                                     && (e.Attribute("FontFamily")?.Value.Contains("IconFont") ?? false))
                                      .Select(_ => Path.GetRelativePath(root, f)))
            .ToList();
        Assert.True(offenders.Count == 0, "MenuItem بخط الأيقونات: " + string.Join(", ", offenders));
    }
}
