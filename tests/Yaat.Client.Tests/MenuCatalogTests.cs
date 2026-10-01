using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;
using Yaat.Client.ContextMenus;

namespace Yaat.Client.Tests;

/// <summary>
/// Guards the catalog's identity contract: every stable identifier in <see cref="MenuIds"/> resolves to exactly one
/// <see cref="MenuCatalog"/> entry, identifiers follow the <c>&lt;group&gt;.&lt;item&gt;</c> scheme, and an unknown
/// identifier fails loudly with its name.
/// </summary>
public class MenuCatalogTests
{
    private static readonly Regex IdScheme = new(@"^[a-z]+\.[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant);

    private static List<string> DeclaredIds() =>
        [
            .. typeof(MenuIds)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && (f.FieldType == typeof(string)))
                .Select(f => (string)f.GetRawConstantValue()!),
        ];

    [Fact]
    public void EveryMenuId_HasExactlyOneCatalogEntry()
    {
        List<string> declared = DeclaredIds();
        Assert.NotEmpty(declared);

        foreach (string id in declared)
        {
            Assert.Single(MenuCatalog.All, e => e.Id == id);
        }

        Assert.Equal(declared.Order(StringComparer.Ordinal), MenuCatalog.All.Select(e => e.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void CatalogIds_FollowTheGroupDotKebabScheme()
    {
        List<string> malformed = [.. MenuCatalog.All.Select(e => e.Id).Where(id => !IdScheme.IsMatch(id))];
        Assert.Empty(malformed);
    }

    [Fact]
    public void Get_UnknownId_ThrowsNamingTheId()
    {
        KeyNotFoundException ex = Assert.Throws<KeyNotFoundException>(() => MenuCatalog.Get("nosuch.entry"));
        Assert.Contains("nosuch.entry", ex.Message, StringComparison.Ordinal);
    }
}
