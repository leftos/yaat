using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

/// <summary>
/// Covers the file-loading and expansion rules YAAT copies from CRC so an alias behaves identically in
/// both clients. Uses real files in a temp directory — the two-file rule and load order are the point.
/// </summary>
public sealed class CrcAliasStoreTests : IDisposable
{
    private static readonly HashSet<string> BuiltIns = new(StringComparer.OrdinalIgnoreCase) { ".ff", ".nomarkers" };

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"yaat-crc-aliases-{Guid.NewGuid():N}");

    public CrcAliasStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private void WriteFile(string name, params string[] lines) => File.WriteAllLines(Path.Combine(_directory, name), lines);

    private CrcAliasStore LoadStore(string? artccId)
    {
        var store = new CrcAliasStore();
        store.Load(_directory, artccId, BuiltIns);
        return store;
    }

    private string Expand(CrcAliasStore store, string input)
    {
        Assert.True(store.TryExpand(input, out var expanded, out var error), error);
        return expanded;
    }

    [Fact]
    public void LoadsArtccFileAndPersonalFile()
    {
        WriteFile("ZOA.txt", ".C172 .echo from artcc");
        WriteFile("MyAliases.txt", ".NNA320 .echo from personal");

        var store = LoadStore("ZOA");

        Assert.Equal(2, store.Count);
        Assert.Equal(["ZOA.txt", "MyAliases.txt"], store.LastLoad.FilesLoaded);
    }

    /// <summary>CRC reads two fixed file names, never a directory glob — a stray file must stay inert.</summary>
    [Fact]
    public void IgnoresOtherTxtFilesInTheDirectory()
    {
        WriteFile("ZOA.txt", ".C172 .echo real");
        WriteFile("badpilot_aliases.txt", ".BADRWY .echo ignored");

        var store = LoadStore("ZOA");

        Assert.True(store.Contains(".C172"));
        Assert.False(store.Contains(".BADRWY"));
    }

    /// <summary>The personal file loads second, so it wins — that is CRC's override mechanism.</summary>
    [Fact]
    public void PersonalFileOverridesArtccFile()
    {
        WriteFile("ZOA.txt", ".DUP .echo artcc");
        WriteFile("MyAliases.txt", ".DUP .echo personal");

        Assert.Equal(".echo personal", Expand(LoadStore("ZOA"), ".DUP"));
    }

    [Fact]
    public void LaterLineInSameFileOverridesEarlier()
    {
        WriteFile("MyAliases.txt", ".DUP .echo first", ".DUP .echo second");

        Assert.Equal(".echo second", Expand(LoadStore(artccId: null), ".DUP"));
    }

    [Fact]
    public void LookupIsCaseInsensitive()
    {
        WriteFile("MyAliases.txt", ".FbC .echo *** F BEHIND C - 3.5NM");

        var store = LoadStore(artccId: null);

        Assert.True(store.Contains(".fbc"));
        Assert.Equal(".echo *** F BEHIND C - 3.5NM", Expand(store, ".FBC"));
    }

    [Fact]
    public void MissingDirectory_LoadsNothingWithoutThrowing()
    {
        var store = new CrcAliasStore();

        var result = store.Load(Path.Combine(_directory, "does-not-exist"), "ZOA", BuiltIns);

        Assert.Equal(0, result.AliasCount);
        Assert.Empty(result.FilesLoaded);
    }

    [Fact]
    public void MissingArtccFile_StillLoadsPersonalFile()
    {
        WriteFile("MyAliases.txt", ".C172 .echo personal only");

        var store = LoadStore("ZZZ");

        Assert.Equal(1, store.Count);
        Assert.Equal(["MyAliases.txt"], store.LastLoad.FilesLoaded);
    }

    [Fact]
    public void ReportsAliasesShadowedByBuiltIns()
    {
        WriteFile("MyAliases.txt", ".ff .echo shadowed", ".C172 .echo fine");

        Assert.Equal([".ff"], LoadStore(artccId: null).LastLoad.ShadowedByBuiltins);
    }

    [Fact]
    public void LoadReplacesPreviousContents()
    {
        WriteFile("MyAliases.txt", ".OLD .echo gone");
        var store = LoadStore(artccId: null);
        Assert.True(store.Contains(".OLD"));

        WriteFile("MyAliases.txt", ".NEW .echo here");
        store.Load(_directory, null, BuiltIns);

        Assert.False(store.Contains(".OLD"));
        Assert.True(store.Contains(".NEW"));
    }

    // --- Expansion ---

    [Fact]
    public void AliasBodyMayReferenceAnotherAlias()
    {
        WriteFile("MyAliases.txt", ".INNER .echo inner text", ".OUTER .INNER");

        Assert.Equal(".echo inner text", Expand(LoadStore(artccId: null), ".OUTER"));
    }

    [Fact]
    public void SelfReferentialAlias_ReportsRecursionInsteadOfHanging()
    {
        WriteFile("MyAliases.txt", ".LOOP .LOOP");

        var store = LoadStore(artccId: null);

        Assert.False(store.TryExpand(".LOOP", out _, out var error));
        Assert.Contains("recursion", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PositionalArgumentsAreSubstituted()
    {
        WriteFile("MyAliases.txt", ".RM2 .openurl https://example.test?dep=$1&dest=$2");

        Assert.Equal(".openurl https://example.test?dep=KOAK&dest=KJFK", Expand(LoadStore(artccId: null), ".RM2 KOAK KJFK"));
    }

    /// <summary>CRC pads a missing argument with empty string rather than erroring.</summary>
    [Fact]
    public void MissingArgument_SubstitutesEmpty()
    {
        WriteFile("MyAliases.txt", ".CH .openurl https://example.test/charts/$1");

        Assert.Equal(".openurl https://example.test/charts/", Expand(LoadStore(artccId: null), ".CH"));
    }

    /// <summary>Tokens the body doesn't declare are never consumed, so they trail the expansion.</summary>
    [Fact]
    public void ExtraArguments_AreAppendedAfterTheExpansion()
    {
        WriteFile("MyAliases.txt", ".CTRFIX .FF SUNOL ALTAM");

        Assert.Equal(".FF SUNOL ALTAM MOD", Expand(LoadStore(artccId: null), ".CTRFIX MOD"));
    }

    /// <summary>Plain substring replacement is what lets a body write <c>$1Z</c> for "argument then Z".</summary>
    [Fact]
    public void ArgumentSubstitutionIsSubstringBased()
    {
        WriteFile("MyAliases.txt", ".T .echo off by $1Z");

        Assert.Equal(".echo off by 1830Z", Expand(LoadStore(artccId: null), ".T 1830"));
    }

    [Fact]
    public void UnknownAlias_IsNotContainedAndExpandsUnchanged()
    {
        WriteFile("MyAliases.txt", ".C172 .echo known");

        var store = LoadStore(artccId: null);

        Assert.False(store.Contains(".NOPE"));
        Assert.Equal(".NOPE", Expand(store, ".NOPE"));
    }
}
