using ERP.Data.Services;
using ERP.Data.Setup;
using Xunit;

namespace ERP.Data.IntegrationTests;

/// <summary>كل نوع عملية في العقل المالي يظهر للمستخدم باسم عربي، لا برمزه الإنجليزي.</summary>
public class RuleNamesTests
{
    [Fact]
    public void Every_system_rule_has_an_arabic_name()
    {
        var missing = DefaultConfiguration.Rules.Select(r => r.type).Where(t => RuleNames.Of(t) == t).ToList();
        Assert.True(missing.Count == 0, "بلا اسم عربي: " + string.Join(", ", missing));
        Assert.Equal("سند قبض نقدي من عميل", RuleNames.Of("CashReceiptVoucher"));
        Assert.Equal("MyCustomRule", RuleNames.Of("MyCustomRule"));   // قاعدة يضيفها المستخدم تبقى برمزها
    }
}
