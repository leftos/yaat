using Xunit;
using Yaat.Client.Views.Scope.Entries;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// CRC's ERAM keyboard on the Scope: <c>[</c>, <c>]</c>, a backtick and <c>~</c> type characters of ERAM's own, which the entry
/// keeps and the preview and the response show as marks.
/// </summary>
public class ScopeEramKeysTests
{
    /// <summary>A weather key inside message text is what the server reads an SM or SW message by.</summary>
    [Fact]
    public void MapTyped_ReplacesTheClearWeatherKey() => Assert.Equal("SM 26 \u0087HELLO", ScopeEramKeys.MapTyped("SM 26 `HELLO"));

    [Fact]
    public void MapTyped_ReplacesTheArrowAndWeatherKeys() => Assert.Equal("\u0080\u0081\u0088", ScopeEramKeys.MapTyped("[]~"));

    [Fact]
    public void MapTyped_LeavesOtherCharacters() => Assert.Equal("QQ 350", ScopeEramKeys.MapTyped("QQ 350"));

    [Fact]
    public void ToDisplay_ShowsEachEramCharacterAsItsMark() => Assert.Equal("↑↓○●A", ScopeEramKeys.ToDisplay("\u0080\u0081\u0087\u0088A"));
}
