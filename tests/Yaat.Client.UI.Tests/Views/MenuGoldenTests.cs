using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Sim.Data;

namespace Yaat.Client.UI.Tests.Views;

// Text goldens of today's aircraft context menus, one file per view and fixture under Goldens/menu/{view}/, built
// through each view's whole-menu builder against the committed NavData, CIFP and KOAK layout. A menu change shows up as
// a diff of these files; once it is intended, YAAT_MENU_GOLDEN_REGENERATE=1 rewrites them.
public class MenuGoldenTests
{
    private const string RegenerateVariable = "YAAT_MENU_GOLDEN_REGENERATE";
    private const int DiffLines = 8;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The top-level labels that open a view's own section: the canvases' Display submenu and the radar's Draw route.</summary>
    private static readonly string[] ViewSectionHeads = ["Display", "Draw route"];

    private readonly NavigationDatabase _navDb;

    public MenuGoldenTests() => _navDb = MenuGoldenFixtures.EnsureNavData();

    [AvaloniaFact]
    public Task RadarMenus_MatchGoldens() => AssertGoldens(MenuView.Radar);

    [AvaloniaFact]
    public Task GroundMenus_MatchGoldens() => AssertGoldens(MenuView.Ground);

    [AvaloniaFact]
    public Task ListMenus_MatchGoldens() => AssertGoldens(MenuView.List);

    /// <summary>
    /// The at-parking ground fixture's Taxi to runway submenu once opened, Other runways opened too, as the real host answers
    /// it over the committed KOAK layout: each group's rows with their via, distance and command, and every second level.
    /// </summary>
    [AvaloniaFact]
    public void GroundMenu_AtParking_TaxiToRunwayOpened_MatchesGolden()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(_navDb);
        var main = new MainViewModel(new FakeFilePickerService());
        main.DisplayFavorites.Clear();
        main.Ground.SetLayoutForTesting(MenuGoldenFixtures.OakLayoutForClient);
        MenuFixture fixture = MenuGoldenFixtures.For(MenuView.Ground).Single(f => f.Name == "at-parking");
        main.Aircraft.Clear();
        main.Aircraft.Add(fixture.Aircraft);
        ContextMenu menu = MenuHostHarness.BuildGroundMenu(main, fixture.Aircraft, fixture.Selected);
        MenuItem allCommands = menu.Items.OfType<MenuItem>().Single(m => (m.Header as string) == AircraftMenuBuilder.AllCommandsHeader);
        MenuItem taxiToRunway = allCommands.Items.OfType<MenuItem>().Single(m => (m.Header as string) == "Taxi to runway");

        taxiToRunway.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, taxiToRunway));
        foreach (MenuItem other in taxiToRunway.Items.OfType<MenuItem>().Where(m => (m.Header as string) == "Other runways"))
        {
            other.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, other));
        }

        AssertGolden(
            "filled",
            "ground-at-parking-taxi-to-runway",
            $"# ground at-parking, Taxi to runway opened\n{MenuTreeSnapshot.Render(taxiToRunway)}"
        );
    }

    /// <summary>
    /// Compares <paramref name="actual"/> with the golden <c>Goldens/menu/{folder}/{name}.txt</c>, or rewrites it under the
    /// variable.
    /// </summary>
    private static void AssertGolden(string folder, string name, string actual)
    {
        string path = Path.Combine(FindRepoRoot(), "tests", "Yaat.Client.UI.Tests", "Goldens", "menu", folder, name + ".txt");
        if (Environment.GetEnvironmentVariable(RegenerateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual, Utf8NoBom);
            Assert.Fail("golden regenerated; review the diff and rerun without the variable");
        }

        Assert.True(File.Exists(path), $"{folder}/{name}: no golden at {path}; rerun with {RegenerateVariable}=1");
        string expected = File.ReadAllText(path).ReplaceLineEndings("\n");
        Assert.True(
            expected == actual,
            $"{folder}/{name}:\n{Diff(expected, actual)}\n\nOnce the change is intended, rerun with {RegenerateVariable}=1."
        );
    }

    [AvaloniaFact]
    public async Task EveryView_RendersIdenticallyTwice()
    {
        foreach (MenuView view in Enum.GetValues<MenuView>())
        {
            string first = string.Concat((await RenderAll(view)).Select(g => g.Text));
            string second = string.Concat((await RenderAll(view)).Select(g => g.Text));
            Assert.Equal(first, second);
        }
    }

    /// <summary>
    /// Every fixture's menu is the same on every view that renders it, once each view's own section (its Display submenu
    /// and Draw route, with the separator before them) and the golden's title line are set aside. A fixture is the
    /// fixture name plus the right-clicked aircraft's callsign, since the radar and ground relative selections right-click
    /// different aircraft; every list fixture must also be rendered by another view, so the list is always compared. The
    /// callsign keys the fixture, not the rendered title row, so the title row stays inside the compared text.
    /// </summary>
    [AvaloniaFact]
    public async Task EveryView_SharesTheMenuOutsideItsViewSection()
    {
        var byFixture = new Dictionary<string, List<(MenuView View, string Menu)>>(StringComparer.Ordinal);
        foreach (MenuView view in Enum.GetValues<MenuView>())
        {
            foreach ((string name, string callsign, string text) in await RenderAll(view))
            {
                string shared = WithoutViewSection(text);
                string key = $"{name} ({callsign})";
                if (!byFixture.TryGetValue(key, out List<(MenuView View, string Menu)>? menus))
                {
                    menus = [];
                    byFixture[key] = menus;
                }

                menus.Add((view, shared));
            }
        }

        var failures = new List<string>();
        foreach ((string key, List<(MenuView View, string Menu)> menus) in byFixture)
        {
            if ((menus.Count == 1) && (menus[0].View == MenuView.List))
            {
                failures.Add($"{key}: only the list renders it, so nothing compares it");
            }

            (MenuView firstView, string firstMenu) = menus[0];
            foreach ((MenuView view, string menu) in menus.Skip(1).Where(m => m.Menu != firstMenu))
            {
                failures.Add($"{key}: {FolderName(firstView)} (-) vs {FolderName(view)} (+):\n{Diff(firstMenu, menu)}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n\n", failures));
    }

    /// <summary>
    /// <paramref name="golden"/> without its title line and without the view's section: each top-level block a
    /// <see cref="ViewSectionHeads"/> label opens, its indented children, and the separator straight before the section.
    /// </summary>
    private static string WithoutViewSection(string golden)
    {
        var kept = new List<string>();
        bool inSection = false;
        foreach (string line in golden.Split('\n').Skip(1))
        {
            if (!line.StartsWith(' '))
            {
                bool head = ViewSectionHeads.Contains(line);
                if (head && !inSection && (kept.Count > 0) && (kept[^1] == "---"))
                {
                    kept.RemoveAt(kept.Count - 1);
                }

                inSection = head;
            }

            if (!inSection)
            {
                kept.Add(line);
            }
        }

        return string.Join('\n', kept);
    }

    private async Task AssertGoldens(MenuView view)
    {
        string dir = Path.Combine(FindRepoRoot(), "tests", "Yaat.Client.UI.Tests", "Goldens", "menu", FolderName(view));
        IReadOnlyList<(string Name, string Callsign, string Text)> goldens = await RenderAll(view);
        List<string> stale = StaleGoldens(dir, goldens);

        if (Environment.GetEnvironmentVariable(RegenerateVariable) == "1")
        {
            Directory.CreateDirectory(dir);
            foreach ((string name, string _, string text) in goldens)
            {
                File.WriteAllText(Path.Combine(dir, name + ".txt"), text, Utf8NoBom);
            }

            foreach (string path in stale)
            {
                File.Delete(path);
            }

            Assert.Fail("goldens regenerated; review the diff and rerun without the variable");
        }

        List<string> failures = [.. stale.Select(path => $"{FolderName(view)}/{Path.GetFileName(path)}: stale golden, no fixture of that name")];
        foreach ((string name, string _, string actual) in goldens)
        {
            string path = Path.Combine(dir, name + ".txt");
            if (!File.Exists(path))
            {
                failures.Add($"{FolderName(view)}/{name}: no golden at {path}");
                continue;
            }

            string expected = File.ReadAllText(path).ReplaceLineEndings("\n");
            if (expected != actual)
            {
                failures.Add($"{FolderName(view)}/{name}:\n{Diff(expected, actual)}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n\n", failures) + $"\n\nOnce the change is intended, rerun with {RegenerateVariable}=1.");
    }

    /// <summary>The <c>.txt</c> files in <paramref name="dir"/> that name no current fixture, sorted by path.</summary>
    private static List<string> StaleGoldens(string dir, IReadOnlyList<(string Name, string Callsign, string Text)> goldens)
    {
        if (!Directory.Exists(dir))
        {
            return [];
        }

        var fixtureNames = goldens.Select(g => g.Name).ToHashSet(StringComparer.Ordinal);
        return
        [
            .. Directory
                .GetFiles(dir, "*.txt")
                .Where(path => !fixtureNames.Contains(Path.GetFileNameWithoutExtension(path)))
                .Order(StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Every fixture's golden text for one view, from one main view model whose aircraft list holds exactly the fixture's
    /// aircraft at each build. The render runs under a scoped override of the committed navigation database, which the
    /// main view model's own background navdata load cannot replace.
    /// </summary>
    private async Task<List<(string Name, string Callsign, string Text)>> RenderAll(MenuView view)
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(_navDb);
        var main = new MainViewModel(new FakeFilePickerService());
        main.DisplayFavorites.Clear();
        main.Ground.SetLayoutForTesting(MenuGoldenFixtures.OakLayoutForClient);
        // No seed, whatever the committed precompute cache holds, so Push back to… shows its computing row.
        main.Ground.SetPushTargetSeedForTesting(OakPushTargetSeedCopy.NoSeed());

        var goldens = new List<(string Name, string Callsign, string Text)>();
        var pushPlans = new List<Task>();
        foreach (MenuFixture fixture in MenuGoldenFixtures.For(view))
        {
            main.Aircraft.Clear();
            main.Aircraft.Add(fixture.Aircraft);
            if (fixture.Selected is { } selected)
            {
                main.Aircraft.Add(selected);
            }

            ContextMenu menu = view switch
            {
                MenuView.Radar => MenuHostHarness.BuildRadarMenu(main, fixture.Aircraft, fixture.Selected),
                MenuView.Ground => MenuHostHarness.BuildGroundMenu(main, fixture.Aircraft, fixture.Selected),
                _ => DataGridView.BuildAircraftMenu(main, new DataGrid(), fixture.Aircraft, fixture.Selected, [fixture.Aircraft]),
            };
            goldens.Add((fixture.Name, fixture.Aircraft.Callsign, $"# {FolderName(view)} {fixture.Name}\n{MenuTreeSnapshot.Render(menu)}"));

            // The menu's Push back to… plan is still in flight, so this returns its list.
            pushPlans.Add(main.Ground.GetPushbackTargets(fixture.Aircraft).Settled);
        }

        // Waited for only once every menu is rendered: a wait yields the UI thread, and the main view model's own
        // background jobs would then change what a later fixture renders. Waiting keeps no plan outliving the render.
        await Task.WhenAll(pushPlans).WaitAsync(ClientMenuHostGroundTests.SettleTimeout, TestContext.Current.CancellationToken);
        return goldens;
    }

    private static string FolderName(MenuView view) => view.ToString().ToLowerInvariant();

    /// <summary>A unified-style excerpt from the first differing line: up to two lines of context, then the expected and actual runs.</summary>
    private static string Diff(string expected, string actual)
    {
        string[] expectedLines = expected.Split('\n');
        string[] actualLines = actual.Split('\n');
        int first = 0;
        while ((first < expectedLines.Length) && (first < actualLines.Length) && (expectedLines[first] == actualLines[first]))
        {
            first++;
        }

        var diff = new StringBuilder();
        diff.Append("@@ line ").Append(first + 1).Append(" @@\n");
        for (int i = Math.Max(0, first - 2); i < first; i++)
        {
            diff.Append("  ").Append(expectedLines[i]).Append('\n');
        }

        foreach (string line in expectedLines.Skip(first).Take(DiffLines))
        {
            diff.Append('-').Append(line).Append('\n');
        }

        foreach (string line in actualLines.Skip(first).Take(DiffLines))
        {
            diff.Append('+').Append(line).Append('\n');
        }

        return diff.ToString();
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while ((directory is not null) && (!File.Exists(Path.Combine(directory.FullName, "yaat.slnx"))))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException($"No yaat.slnx above {AppContext.BaseDirectory}");
    }
}
