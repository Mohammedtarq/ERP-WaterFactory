using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ERP.Presentation.ViewModels.Shell;
using Xunit;
using Xunit.Abstractions;

namespace ERP.Presentation.Tests;

/// <summary>
/// WPF لا يتحقق من مسارات {Binding ...} إلا وقت التشغيل (الخطأ يظهر كحقل فارغ بصمت).
/// هذا الاختبار يقرأ كل ملفات XAML ويتتبع سياق البيانات (DataContext) لكل عنصر:
/// جذر الشاشة = نوع d:DesignInstance، وداخل أي عنصر له ItemsSource = نوع عنصر المجموعة،
/// و ElementName=Root = نوع الجذر — ثم يتحقق أن كل خاصية في كل مسار موجودة فعلًا.
/// </summary>
public class XamlBindingTests
{
    private static readonly Assembly[] Assemblies = { typeof(MainShellViewModel).Assembly, typeof(ERP.Data.Services.SalesService).Assembly };
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Design = "http://schemas.microsoft.com/expression/blend/2008";
    private readonly ITestOutputHelper _out;

    public XamlBindingTests(ITestOutputHelper output) => _out = output;

    private static string XamlDir => Path.Combine(AppContext.BaseDirectory, "Xaml");

    public static IEnumerable<object[]> ViewFiles() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Xaml"), "*.xaml", SearchOption.AllDirectories)
                 .Where(f => XDocument.Load(f).Root!.Attribute(Design + "DataContext") is not null)
                 .Select(f => new object[] { Path.GetRelativePath(Path.Combine(AppContext.BaseDirectory, "Xaml"), f) });

    [Theory]
    [MemberData(nameof(ViewFiles))]
    public void Every_binding_path_exists_on_its_view_model(string relativePath)
    {
        var doc = XDocument.Load(Path.Combine(XamlDir, relativePath));
        var root = doc.Root!;
        var rootType = ResolveDesignType(root);
        var errors = new List<string>();
        int checkedCount = 0;

        void Walk(XElement el, Type? context)
        {
            // سمات العنصر نفسه تُقيَّم في سياق الأب (بما فيها ItemsSource و SelectedItem)
            Type? itemContext = context;
            foreach (var attr in el.Attributes())
            {
                var value = attr.Value.Trim();
                if (!value.StartsWith("{Binding")) continue;
                var (path, element, relative) = ParseBinding(value);
                if (relative || path is null) continue;

                Type? start = element == "Root" ? rootType : context;
                // وكيل الربط (BindingProxy): Data = سياق الجذر، لأعمدة الجدول خارج الشجرة المرئية
                if (value.Contains("Source={StaticResource Proxy}"))
                {
                    if (!path.StartsWith("Data.")) continue;
                    path = path["Data.".Length..];
                    start = rootType;
                    element = null;
                }
                if (element == "Root")
                {
                    if (!path.StartsWith("DataContext.")) continue;
                    path = path["DataContext.".Length..];
                }
                else if (element is not null) continue;
                if (start is null || start == typeof(object)) continue;

                checkedCount++;
                var resultType = ResolvePath(start, path, out var error);
                if (error is not null) errors.Add($"<{el.Name.LocalName} {attr.Name.LocalName}=\"{value}\">: {error}");

                if (attr.Name.LocalName == "ItemsSource")
                    itemContext = resultType is null ? typeof(object) : ElementType(resultType);
            }

            // DisplayMemberPath / SelectedValuePath تُقرأ من نوع عناصر القائمة
            foreach (var name in new[] { "DisplayMemberPath", "SelectedValuePath" })
                if (el.Attribute(name) is { } a && itemContext is not null && itemContext != typeof(object) && itemContext != context)
                {
                    checkedCount++;
                    ResolvePath(itemContext, a.Value, out var error);
                    if (error is not null) errors.Add($"<{el.Name.LocalName} {name}=\"{a.Value}\">: {error}");
                }

            foreach (var child in el.Elements()) Walk(child, itemContext);
        }

        Walk(root, rootType);
        _out.WriteLine($"{relativePath}: {checkedCount} bindings checked against {rootType.Name}");
        Assert.True(errors.Count == 0, $"{relativePath}\n" + string.Join("\n", errors));
        Assert.True(checkedCount > 0, "لم يُفحص أي ربط — تحقق من d:DataContext");
    }

    [Fact]
    public void Every_section_and_module_view_model_has_a_view_mapping()
    {
        var mappings = XDocument.Load(Path.Combine(XamlDir, "Views", "ViewMappings.xaml"));
        var mapped = mappings.Root!.Elements().Where(e => e.Name.LocalName == "DataTemplate")
            .Select(e => Regex.Match(e.Attribute("DataType")!.Value, @":(\w+)\}").Groups[1].Value).ToHashSet();

        var required = typeof(MainShellViewModel).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && (typeof(SectionViewModel).IsAssignableFrom(t) || t == typeof(HomeSectionViewModel)
                                          || t == typeof(DashboardViewModel) || t == typeof(PlaceholderModuleViewModel)))
            .Select(t => t.Name).ToList();

        var missing = required.Where(n => !mapped.Contains(n)).ToList();
        Assert.True(missing.Count == 0, "ViewModels بلا شاشة في ViewMappings.xaml: " + string.Join(", ", missing));
        Assert.Contains("ModuleViewModel", mapped);
    }

    [Fact]
    public void Row_buttons_use_ElementName_Root_on_a_root_named_Root()
    {
        foreach (var file in Directory.GetFiles(XamlDir, "*.xaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            if (!text.Contains("ElementName=Root")) continue;
            Assert.True(XDocument.Load(file).Root!.Attribute(Xaml + "Name")?.Value == "Root",
                        $"{Path.GetFileName(file)} يستخدم ElementName=Root لكن جذره ليس x:Name=\"Root\"");
        }
    }

    /// <summary>
    /// WPF يعامل خاصية اسمها "Item" في نهاية مسار الربط كمفهرس عند كتابة null إليها (فراغ اختيار القائمة المنسدلة
    /// عند مغادرة الشاشة) فيرمي NullReferenceException داخل PropertyPathWorker — لا يُربط أي عنصر بخاصية بهذا الاسم.
    /// </summary>
    [Fact]
    public void No_binding_ends_on_a_property_named_Item()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(XamlDir, "*.xaml", SearchOption.AllDirectories))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\{Binding\s+(?:Path=)?(?<path>[\w.\[\]]+)"))
                if (m.Groups["path"].Value.Split('.')[^1] == "Item")
                    offenders.Add($"{Path.GetFileName(file)}: {m.Value}");
        Assert.True(offenders.Count == 0, "ربط بخاصية اسمها Item (سمّها باسم آخر):\n" + string.Join("\n", offenders));
    }

    // ------------------------------------------------------------------

    private static Type ResolveDesignType(XElement root)
    {
        var design = root.Attribute(Design + "DataContext")!.Value;               // {d:DesignInstance vm:Type}
        var m = Regex.Match(design, @"DesignInstance\s+(?:Type=)?(\w+):(\w+)");
        var prefix = m.Groups[1].Value;
        var typeName = m.Groups[2].Value;
        var ns = root.GetNamespaceOfPrefix(prefix)!.NamespaceName;                 // clr-namespace:X;assembly=Y
        var clrNs = Regex.Match(ns, @"clr-namespace:([\w.]+)").Groups[1].Value;
        return Assemblies.Select(a => a.GetType($"{clrNs}.{typeName}")).FirstOrDefault(t => t is not null)
               ?? throw new InvalidOperationException($"النوع {clrNs}.{typeName} غير موجود");
    }

    private static (string? path, string? elementName, bool relative) ParseBinding(string markup)
    {
        var inner = markup.Trim('{', '}').Trim();
        inner = inner.Length > "Binding".Length ? inner["Binding".Length..].Trim() : "";
        if (inner.Length == 0) return (null, null, false);

        string? path = null, element = null;
        bool relative = inner.Contains("RelativeSource");
        foreach (var part in SplitTopLevel(inner))
        {
            var p = part.Trim();
            if (p.StartsWith("Path=")) path = p[5..].Trim();
            else if (p.StartsWith("ElementName=")) element = p[12..].Trim();
            else if (!p.Contains('=') && path is null) path = p;
        }
        return (path, element, relative);
    }

    private static IEnumerable<string> SplitTopLevel(string s)
    {
        int depth = 0; bool quoted = false; var start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\'') quoted = !quoted;
            else if (!quoted && c == '{') depth++;
            else if (!quoted && c == '}') depth--;
            else if (!quoted && depth == 0 && c == ',')
            {
                yield return s[start..i];
                start = i + 1;
            }
        }
        yield return s[start..];
    }

    private static Type? ResolvePath(Type start, string path, out string? error)
    {
        error = null;
        var type = start;
        foreach (var segment in path.Split('.'))
        {
            var name = segment.Trim();
            if (name.Length == 0) continue;
            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (prop is null)
            {
                error = $"الخاصية \"{name}\" غير موجودة في {type.Name}";
                return null;
            }
            type = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            if (type == typeof(object)) return type;   // لا يمكن التحقق أعمق
        }
        return type;
    }

    private static Type ElementType(Type collection)
    {
        var enumerable = collection.IsGenericType && collection.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            ? collection
            : collection.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0] ?? typeof(object);
    }
}
