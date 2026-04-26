using System;
using System.IO;
using System.Linq;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Tests;

public class CommandCheatsheetTests
{
    private const string SampleJson = """
        {
          "version": "1",
          "intro": ["Args concatenate: `FH270`"],
          "categories": [
            {
              "id": "heading",
              "name": "Heading",
              "rows": [
                { "verb": "FH", "aliases": ["h"], "description": "Fly heading" },
                { "verb": "TL / TR", "aliases": ["l", "r"], "description": "Turn left/right" }
              ],
              "notes": []
            },
            {
              "id": "approach",
              "name": "Approach",
              "rows": [
                { "verb": "APPS", "aliases": [], "description": "List approaches", "global": true },
                { "verb": "JFAC", "aliases": ["jloc", "jf"], "description": "Join FAC" }
              ],
              "notes": ["Rich forms: `AT fix CAPP`"]
            },
            {
              "id": "chaining",
              "name": "Chaining",
              "rows": [
                { "verb": ";", "aliases": [], "description": "Sequential", "examples": ["CM 100 ; FH 270"] }
              ],
              "notes": []
            }
          ]
        }
        """;

    [Fact]
    public void Parse_ReadsCategoriesAndRows()
    {
        var data = CommandCheatsheetReader.Parse(SampleJson);

        Assert.Equal(3, data.Categories.Count);
        Assert.Equal("Heading", data.Categories[0].Name);
        Assert.Equal("heading", data.Categories[0].Id);
        Assert.Equal(2, data.Categories[0].Rows.Count);
    }

    [Fact]
    public void Parse_PreservesAliasesAsArrays()
    {
        var data = CommandCheatsheetReader.Parse(SampleJson);
        var jfac = data.Categories[1].Rows.Single(r => r.Verb == "JFAC");

        Assert.Equal(["jloc", "jf"], jfac.Aliases);
    }

    [Fact]
    public void Parse_DefaultsGlobalAndExamples()
    {
        var data = CommandCheatsheetReader.Parse(SampleJson);
        var fh = data.Categories[0].Rows.Single(r => r.Verb == "FH");

        Assert.False(fh.Global);
        Assert.Empty(fh.Examples);
    }

    [Fact]
    public void Parse_HonoursGlobalFlag()
    {
        var data = CommandCheatsheetReader.Parse(SampleJson);
        var apps = data.Categories[1].Rows.Single(r => r.Verb == "APPS");

        Assert.True(apps.Global);
    }

    [Fact]
    public void Parse_PreservesExamples()
    {
        var data = CommandCheatsheetReader.Parse(SampleJson);
        var seq = data.Categories[2].Rows.Single(r => r.Verb == ";");

        Assert.Equal(["CM 100 ; FH 270"], seq.Examples);
    }

    [Fact]
    public void Parse_PreservesNotes()
    {
        var data = CommandCheatsheetReader.Parse(SampleJson);
        Assert.Equal(["Rich forms: `AT fix CAPP`"], data.Categories[1].Notes);
    }

    [Fact]
    public void Parse_PreservesIntro()
    {
        var data = CommandCheatsheetReader.Parse(SampleJson);
        Assert.Single(data.Intro);
        Assert.Contains("`FH270`", data.Intro[0]);
    }

    [Fact]
    public void ViewModel_EmptyFilter_ShowsAllSectionsAndRows()
    {
        var vm = new CommandCheatsheetViewModel(CommandCheatsheetReader.Parse(SampleJson));

        Assert.All(vm.Sections, s => Assert.True(s.IsVisible));
        Assert.Equal(2, vm.Sections[0].VisibleRows.Count);
        Assert.Equal(2, vm.Sections[1].VisibleRows.Count);
        Assert.Single(vm.Sections[2].VisibleRows);
    }

    [Fact]
    public void ViewModel_FilterMatchesVerb()
    {
        var vm = new CommandCheatsheetViewModel(CommandCheatsheetReader.Parse(SampleJson));

        vm.FilterText = "fh";

        var heading = vm.Sections.Single(s => s.Category == "Heading");
        Assert.True(heading.IsVisible);
        Assert.Contains(heading.VisibleRows, r => r.Verb == "FH");

        var approach = vm.Sections.Single(s => s.Category == "Approach");
        Assert.False(approach.IsVisible);
    }

    [Fact]
    public void ViewModel_FilterMatchesAlias()
    {
        var vm = new CommandCheatsheetViewModel(CommandCheatsheetReader.Parse(SampleJson));

        vm.FilterText = "jloc";

        var approach = vm.Sections.Single(s => s.Category == "Approach");
        Assert.True(approach.IsVisible);
        Assert.Single(approach.VisibleRows);
        Assert.Equal("JFAC", approach.VisibleRows[0].Verb);
    }

    [Fact]
    public void ViewModel_FilterMatchesDescriptionCaseInsensitive()
    {
        var vm = new CommandCheatsheetViewModel(CommandCheatsheetReader.Parse(SampleJson));

        vm.FilterText = "TURN";

        var heading = vm.Sections.Single(s => s.Category == "Heading");
        Assert.Single(heading.VisibleRows);
        Assert.Equal("TL / TR", heading.VisibleRows[0].Verb);
    }

    [Fact]
    public void ViewModel_FilterMatchesCategoryName_ShowsAllRowsInThatCategory()
    {
        var vm = new CommandCheatsheetViewModel(CommandCheatsheetReader.Parse(SampleJson));

        vm.FilterText = "heading";

        var heading = vm.Sections.Single(s => s.Category == "Heading");
        Assert.True(heading.IsVisible);
        // Both rows (FH and TL/TR) are visible because the category itself matches —
        // even TL/TR which doesn't contain "heading" in any field other than its description.
        Assert.Equal(2, heading.VisibleRows.Count);

        // Other categories still get per-row matching; "heading" doesn't match
        // anything in Approach or Chaining, so they're hidden.
        var approach = vm.Sections.Single(s => s.Category == "Approach");
        Assert.False(approach.IsVisible);
    }

    [Fact]
    public void ViewModel_FilterMatchesCategoryName_CaseInsensitive()
    {
        var vm = new CommandCheatsheetViewModel(CommandCheatsheetReader.Parse(SampleJson));

        vm.FilterText = "APPROACH";

        var approach = vm.Sections.Single(s => s.Category == "Approach");
        Assert.True(approach.IsVisible);
        Assert.Equal(2, approach.VisibleRows.Count);
    }

    [Fact]
    public void ViewModel_ClearingFilterRestoresEverything()
    {
        var vm = new CommandCheatsheetViewModel(CommandCheatsheetReader.Parse(SampleJson));
        vm.FilterText = "fh";

        vm.FilterText = "";

        Assert.All(vm.Sections, s => Assert.True(s.IsVisible));
        Assert.Equal(2, vm.Sections[0].VisibleRows.Count);
    }

    [Fact]
    public void ViewModel_GlobalRowsExposeIsGlobal()
    {
        var vm = new CommandCheatsheetViewModel(CommandCheatsheetReader.Parse(SampleJson));
        var apps = vm.Sections.Single(s => s.Category == "Approach").AllRows.Single(r => r.Verb == "APPS");

        Assert.True(apps.IsGlobal);
    }

    [Fact]
    public void ViewModel_ExamplesAreJoinedForDisplay()
    {
        var vm = new CommandCheatsheetViewModel(CommandCheatsheetReader.Parse(SampleJson));
        var seq = vm.Sections.Single(s => s.Category == "Chaining").AllRows[0];

        Assert.Equal("CM 100 ; FH 270", seq.Examples);
    }

    [Fact]
    public void RealRepoJson_ParsesAndCoversExpectedCategories()
    {
        var path = ResolveRepoJsonPath();
        if (!File.Exists(path))
        {
            // Test runner not aligned with repo layout — silently skip rather than fail.
            return;
        }

        var data = CommandCheatsheetReader.Parse(File.ReadAllText(path));

        Assert.True(data.Categories.Count >= 18, $"expected at least 18 categories, got {data.Categories.Count}");

        var verbs = data.Categories.SelectMany(c => c.Rows).Select(r => r.Verb).ToHashSet();
        Assert.Contains("FH", verbs);
        Assert.Contains("CM", verbs);
        Assert.Contains("TAXI", verbs);
        Assert.Contains("RD", verbs);
        Assert.DoesNotContain("", verbs);

        Assert.All(data.Categories, c => Assert.False(string.IsNullOrEmpty(c.Name)));
        Assert.All(data.Categories, c => Assert.False(string.IsNullOrEmpty(c.Id)));
    }

    private static string ResolveRepoJsonPath()
    {
        // bin/Debug/net10.0 -> tests/Yaat.Client.Tests -> tests -> repo root -> docs/
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "command-cheatsheet.json"));
    }
}
