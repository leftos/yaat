using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Automation;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.UI.Tests.Helpers;

namespace Yaat.Client.UI.Tests.Automation;

/// <summary>
/// <c>get_tree</c> and the selector errors end to end over a real named pipe: stable node ids, overlay popups and owned
/// windows in the tree, a selector root with a depth, and the coded selector errors. Each test has its own pipe name and
/// discovery directory.
/// </summary>
public sealed class AutomationTreeTests : AutomationHostFixture
{
    private Window ShowWindow(string name, Window? owner, Control content) =>
        Show(
            new Window
            {
                Name = name,
                Title = name,
                Width = 300,
                Height = 200,
                Content = content,
            },
            owner
        );

    private static StackPanel BuildForm() =>
        new()
        {
            Name = "Form",
            Children =
            {
                new Button { Name = "Save", Content = "Save" },
                new Button { Name = "Cancel", Content = "Cancel" },
                new Border
                {
                    Name = "Box",
                    Child = new TextBlock { Name = "Label", Text = "Ready" },
                },
            },
        };

    private static Task<JsonElement> GetTree(AutomationPipeTestClient client, object parameters) =>
        client.SendRawAsync(
            JsonSerializer.Serialize(
                new
                {
                    id = "tree",
                    method = ProtocolMethods.GetTree,
                    @params = parameters,
                }
            )
        );

    private static IEnumerable<JsonElement> Flatten(JsonElement node)
    {
        yield return node;
        if (!node.TryGetProperty("children", out JsonElement children))
        {
            yield break;
        }

        foreach (JsonElement child in children.EnumerateArray())
        {
            foreach (JsonElement descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }

    private static IEnumerable<JsonElement> AllNodes(JsonElement roots) => roots.EnumerateArray().SelectMany(Flatten);

    private static bool HasName(JsonElement node, string name) => node.TryGetProperty("name", out JsonElement value) && (value.GetString() == name);

    private static bool HasType(JsonElement node, string type) => node.GetProperty("type").GetString() == type;

    private static int IdOfNamed(JsonElement roots, string name) =>
        Assert.Single(AllNodes(roots), node => HasName(node, name)).GetProperty("nodeId").GetInt32();

    [AvaloniaFact]
    public async Task GetTree_ReturnsStableNodeIdsAcrossCalls()
    {
        using AutomationHost host = StartHost(() => Windows);
        ShowWindow("TreeWindow", null, BuildForm());
        await using AutomationPipeTestClient client = await Connect();

        JsonElement windows = Result(await client.SendAsync(ProtocolMethods.ListWindows));
        JsonElement first = Result(await GetTree(client, new { depth = 50 }));
        JsonElement second = Result(await GetTree(client, new { depth = 50 }));

        string[] names = ["TreeWindow", "Form", "Save", "Cancel", "Box", "Label"];
        int[] firstIds = [.. names.Select(name => IdOfNamed(first, name))];
        Assert.Equal(firstIds, names.Select(name => IdOfNamed(second, name)));
        Assert.Equal(names.Length, firstIds.Distinct().Count());
        JsonElement windowRoot = Assert.Single(first.EnumerateArray());
        Assert.Equal(nameof(Window), windowRoot.GetProperty("type").GetString());
        Assert.Equal(Assert.Single(windows.EnumerateArray()).GetProperty("nodeId").GetInt32(), windowRoot.GetProperty("nodeId").GetInt32());
    }

    [AvaloniaFact]
    public async Task GetTree_IncludesOverlayPopupAndOwnedWindow()
    {
        using AutomationHost host = StartHost(() => Windows.Take(1));
        var panel = new StackPanel();
        var popup = new Popup
        {
            Child = new Border
            {
                Name = "PopupContent",
                Width = 40,
                Height = 30,
            },
        };
        panel.Children.Add(popup);
        Window owner = ShowWindow("OwnerWindow", null, panel);
        ShowWindow("OwnedWindow", owner, new TextBlock { Name = "OwnedLabel", Text = "Owned" });
        popup.IsOpen = true;
        await using AutomationPipeTestClient client = await Connect();

        JsonElement roots = Result(await GetTree(client, new { depth = 50 }));

        JsonElement ownerRoot = Assert.Single(roots.EnumerateArray(), root => HasName(root, "OwnerWindow"));
        int ownerId = ownerRoot.GetProperty("nodeId").GetInt32();
        Assert.False(ownerRoot.TryGetProperty("ownerId", out _));
        JsonElement ownedRoot = Assert.Single(roots.EnumerateArray(), root => HasName(root, "OwnedWindow"));
        Assert.Equal(ownerId, ownedRoot.GetProperty("ownerId").GetInt32());
        Assert.Contains(Flatten(ownedRoot), node => HasName(node, "OwnedLabel"));
        JsonElement popupRoot = Assert.Single(roots.EnumerateArray(), root => HasType(root, nameof(OverlayPopupHost)));
        Assert.Equal(ownerId, popupRoot.GetProperty("ownerId").GetInt32());
        Assert.Contains(Flatten(popupRoot), node => HasName(node, "PopupContent"));
        Assert.Single(AllNodes(roots), node => HasType(node, nameof(OverlayPopupHost)));
    }

    [AvaloniaFact]
    public async Task GetTree_FromSelectorRoot_HonoursDepth()
    {
        using AutomationHost host = StartHost(() => Windows);
        ShowWindow("TreeWindow", null, BuildForm());
        await using AutomationPipeTestClient client = await Connect();

        JsonElement depthZero = Result(await GetTree(client, new { selector = "#Form", depth = 0 }));
        JsonElement depthOne = Result(await GetTree(client, new { selector = "#Form", depth = 1 }));
        JsonElement depthTwo = Result(await GetTree(client, new { selector = "#Form", depth = 2 }));

        JsonElement formAtZero = Assert.Single(depthZero.EnumerateArray());
        Assert.True(HasName(formAtZero, "Form"));
        Assert.False(formAtZero.TryGetProperty("children", out _));
        JsonElement formAtOne = Assert.Single(depthOne.EnumerateArray());
        JsonElement saveAtOne = Assert.Single(formAtOne.GetProperty("children").EnumerateArray(), node => HasName(node, "Save"));
        Assert.False(saveAtOne.TryGetProperty("children", out _));
        JsonElement formAtTwo = Assert.Single(depthTwo.EnumerateArray());
        JsonElement saveAtTwo = Assert.Single(formAtTwo.GetProperty("children").EnumerateArray(), node => HasName(node, "Save"));
        Assert.NotEmpty(saveAtTwo.GetProperty("children").EnumerateArray());
    }

    [AvaloniaFact]
    public async Task GetTree_NodeInsideAPanelWithMargin_ReportsWindowRelativeBounds()
    {
        using AutomationHost host = StartHost(() => Windows);
        var button = new Button { Name = "Inner", Content = "Inner" };
        var border = new Border
        {
            Name = "Outer",
            Margin = new Thickness(40, 30),
            Child = new StackPanel
            {
                Name = "Middle",
                Margin = new Thickness(10, 5),
                Children = { button },
            },
        };
        ShowWindow("MarginWindow", border, null);
        await using AutomationPipeTestClient client = await Connect();

        JsonElement roots = Result(await GetTree(client, new { selector = "#Inner", depth = 0 }));

        JsonElement node = Assert.Single(roots.EnumerateArray());
        JsonElement windowBounds = node.GetProperty("windowBounds");
        Assert.Equal(50, windowBounds.GetProperty("x").GetDouble());
        Assert.Equal(35, windowBounds.GetProperty("y").GetDouble());
        Assert.Equal(button.Bounds.Width, windowBounds.GetProperty("width").GetDouble());
        Assert.Equal(button.Bounds.Height, windowBounds.GetProperty("height").GetDouble());
        JsonElement bounds = node.GetProperty("bounds");
        Assert.Equal(0, bounds.GetProperty("x").GetDouble());
        Assert.Equal(0, bounds.GetProperty("y").GetDouble());
    }

    [AvaloniaFact]
    public async Task Selector_MatchingTwoNodes_ReturnsAmbiguousSelectorWithCountAndHint()
    {
        using AutomationHost host = StartHost(() => Windows);
        ShowWindow("TreeWindow", null, BuildForm());
        await using AutomationPipeTestClient client = await Connect();

        JsonElement error = Error(await GetTree(client, new { selector = "StackPanel > Button" }), AutomationErrorCodes.AmbiguousSelector);
        JsonElement narrowed = Result(await GetTree(client, new { selector = "StackPanel > Button:nth(1)", depth = 0 }));

        Assert.Equal(2, error.GetProperty("details").GetProperty("matchCount").GetInt32());
        Assert.Equal("StackPanel > Button", error.GetProperty("details").GetProperty("selector").GetString());
        Assert.Contains(":nth(0) through :nth(1)", error.GetProperty("suggested").GetString(), StringComparison.Ordinal);
        Assert.True(HasName(Assert.Single(narrowed.EnumerateArray()), "Cancel"));
    }

    [AvaloniaFact]
    public async Task Selector_Invalid_ReturnsInvalidSelectorWithPosition()
    {
        using AutomationHost host = StartHost(() => Windows);
        ShowWindow("TreeWindow", null, BuildForm());
        await using AutomationPipeTestClient client = await Connect();

        JsonElement error = Error(await GetTree(client, new { selector = "Button[" }), AutomationErrorCodes.InvalidSelector);

        Assert.Equal(7, error.GetProperty("details").GetProperty("position").GetInt32());
        Assert.Equal("Button[", error.GetProperty("details").GetProperty("selector").GetString());
        Assert.Contains("position 7", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Selector_NoMatch_ReturnsNoMatch()
    {
        using AutomationHost host = StartHost(() => Windows);
        ShowWindow("TreeWindow", null, BuildForm());
        await using AutomationPipeTestClient client = await Connect();

        JsonElement error = Error(await GetTree(client, new { selector = "#NoSuchControl" }), AutomationErrorCodes.NoMatch);

        Assert.Equal("#NoSuchControl", error.GetProperty("details").GetProperty("selector").GetString());
    }

    [AvaloniaFact]
    public async Task Selector_Missing_ReturnsMissingSelector()
    {
        using AutomationHost host = StartHost(() => Windows);
        ShowWindow("TreeWindow", null, BuildForm());
        await using AutomationPipeTestClient client = await Connect();

        JsonElement error = Error(await GetTree(client, new { selector = "   " }), AutomationErrorCodes.MissingSelector);

        Assert.Equal("selector", error.GetProperty("details").GetProperty("param").GetString());
    }

    [AvaloniaFact]
    public async Task Selector_DataContextAttribute_MatchesByViewModelProperty()
    {
        using AutomationHost host = StartHost(() => Windows);
        var rows = new StackPanel
        {
            Children =
            {
                new Border { Name = "AalRow", DataContext = new StripRow("AAL123") },
                new Border { Name = "UalRow", DataContext = new StripRow("UAL456") },
            },
        };
        ShowWindow("StripWindow", null, rows);
        await using AutomationPipeTestClient client = await Connect();

        JsonElement match = Result(await GetTree(client, new { selector = "Border[dc.Callsign=AAL123]", depth = 0 }));

        Assert.True(HasName(Assert.Single(match.EnumerateArray()), "AalRow"));
    }

    [AvaloniaFact]
    public async Task Selector_DataContextAttribute_MatchesOnlyWhereTheDataContextIsSet()
    {
        using AutomationHost host = StartHost(() => Windows);
        var list = new ListBox { Name = "Strips", ItemsSource = new[] { new StripRow("AAL123"), new StripRow("UAL456") } };
        ShowWindow("StripListWindow", null, list);
        await using AutomationPipeTestClient client = await Connect();

        JsonElement match = Result(await GetTree(client, new { selector = "[dc.Callsign=AAL123]", depth = 0 }));

        Assert.Equal(nameof(ListBoxItem), Assert.Single(match.EnumerateArray()).GetProperty("type").GetString());
    }

    [AvaloniaFact]
    public async Task Selector_DataContextPropertyWithoutGetter_IsNoMatch()
    {
        using AutomationHost host = StartHost(() => Windows);
        var rows = new StackPanel
        {
            Children =
            {
                new Border { Name = "Row", DataContext = new WriteOnlyRow() },
            },
        };
        ShowWindow("WriteOnlyWindow", null, rows);
        await using AutomationPipeTestClient client = await Connect();

        Error(await GetTree(client, new { selector = "[dc.Secret=x]" }), AutomationErrorCodes.NoMatch);
    }

    [AvaloniaFact]
    public async Task Selector_WhitespaceBeforeHash_IsADescendantCombinator()
    {
        using AutomationHost host = StartHost(() => Windows);
        ShowWindow("TreeWindow", null, BuildForm());
        await using AutomationPipeTestClient client = await Connect();

        JsonElement match = Result(await GetTree(client, new { selector = "#Form #Save", depth = 0 }));

        Assert.True(HasName(Assert.Single(match.EnumerateArray()), "Save"));
    }

    [AvaloniaFact]
    public async Task Selector_Universal_MatchesEveryElementInScope()
    {
        using AutomationHost host = StartHost(() => Windows);
        ShowWindow("TreeWindow", null, BuildForm());
        await using AutomationPipeTestClient client = await Connect();

        JsonElement children = Error(await GetTree(client, new { selector = "#Form > *" }), AutomationErrorCodes.AmbiguousSelector);
        JsonElement buttons = Error(await GetTree(client, new { selector = "* Button" }), AutomationErrorCodes.AmbiguousSelector);
        JsonElement everything = Error(await GetTree(client, new { selector = "*" }), AutomationErrorCodes.AmbiguousSelector);

        Assert.Equal(3, children.GetProperty("details").GetProperty("matchCount").GetInt32());
        Assert.True(buttons.GetProperty("details").GetProperty("matchCount").GetInt32() >= 2);
        Assert.True(everything.GetProperty("details").GetProperty("matchCount").GetInt32() > 6);
    }

    [AvaloniaFact]
    public async Task Selector_UnknownPseudoClass_ReturnsInvalidSelectorWithPosition()
    {
        using AutomationHost host = StartHost(() => Windows);
        ShowWindow("TreeWindow", null, BuildForm());
        await using AutomationPipeTestClient client = await Connect();

        JsonElement error = Error(await GetTree(client, new { selector = "Button:bogus" }), AutomationErrorCodes.InvalidSelector);

        Assert.Equal(7, error.GetProperty("details").GetProperty("position").GetInt32());
    }

    [AvaloniaFact]
    public async Task Selector_RoleAndText_UseTheDescriptionsGetTreeReports()
    {
        using AutomationHost host = StartHost(() => Windows);
        var scope = new Border { Name = "Scope" };
        AutomationProperties.SetName(scope, "Radar scope");
        var panel = new StackPanel
        {
            Name = "Panel",
            Children =
            {
                new ToggleButton { Name = "Mode", Content = "Mode" },
                scope,
            },
        };
        ShowWindow("RoleWindow", null, panel);
        await using AutomationPipeTestClient client = await Connect();

        JsonElement byPseudo = Result(await GetTree(client, new { selector = "#Panel :role(button)", depth = 0 }));
        JsonElement byAttribute = Result(await GetTree(client, new { selector = "#Panel [Role=button]", depth = 0 }));
        JsonElement byText = Result(await GetTree(client, new { selector = "#Panel [Text='Radar scope']", depth = 0 }));

        Assert.True(HasName(Assert.Single(byPseudo.EnumerateArray()), "Mode"));
        Assert.True(HasName(Assert.Single(byAttribute.EnumerateArray()), "Mode"));
        Assert.True(HasName(Assert.Single(byText.EnumerateArray()), "Scope"));
    }

    [AvaloniaFact]
    public async Task Selector_TypeName_MatchesTheTypeOrABaseTypeExactly()
    {
        using AutomationHost host = StartHost(() => Windows);
        var panel = new StackPanel
        {
            Children =
            {
                new ButtonSpinner { Name = "Spinner" },
                new CheckBox { Name = "Check", Content = "Check" },
            },
        };
        ShowWindow("TypeWindow", null, panel);
        await using AutomationPipeTestClient client = await Connect();

        Error(await GetTree(client, new { selector = "Button#Spinner" }), AutomationErrorCodes.NoMatch);
        JsonElement check = Result(await GetTree(client, new { selector = "ToggleButton#Check", depth = 0 }));

        Assert.True(HasName(Assert.Single(check.EnumerateArray()), "Check"));
    }

    [AvaloniaFact]
    public async Task Selector_StaleNodeId_ReturnsStaleNode()
    {
        using AutomationHost host = StartHost(() => Windows);
        StackPanel form = BuildForm();
        ShowWindow("TreeWindow", null, form);
        await using AutomationPipeTestClient client = await Connect();
        int boxId = IdOfNamed(Result(await GetTree(client, new { depth = 50 })), "Box");
        form.Children.RemoveAt(2);

        JsonElement error = Error(await GetTree(client, new { selector = $"#{boxId}" }), AutomationErrorCodes.StaleNode);

        Assert.Equal(boxId, error.GetProperty("details").GetProperty("nodeId").GetInt32());
    }

    [AvaloniaFact]
    public async Task GetTree_NodeIdRoot_ReturnsThatNode_OrStaleNode()
    {
        using AutomationHost host = StartHost(() => Windows);
        StackPanel form = BuildForm();
        ShowWindow("TreeWindow", null, form);
        await using AutomationPipeTestClient client = await Connect();
        JsonElement tree = Result(await GetTree(client, new { depth = 50 }));
        int formId = IdOfNamed(tree, "Form");
        int boxId = IdOfNamed(tree, "Box");

        JsonElement byId = Result(await GetTree(client, new { nodeId = formId, depth = 0 }));
        form.Children.RemoveAt(2);
        JsonElement stale = Error(await GetTree(client, new { nodeId = boxId }), AutomationErrorCodes.StaleNode);

        Assert.True(HasName(Assert.Single(byId.EnumerateArray()), "Form"));
        Assert.Equal(boxId, stale.GetProperty("details").GetProperty("nodeId").GetInt32());
    }

    [AvaloniaTheory]
    [InlineData("""{"depth":-1}""", "depth")]
    [InlineData("""{"depth":1.5}""", "depth")]
    [InlineData("""{"depth":"x"}""", "depth")]
    [InlineData("""{"treeKind":"Bogus"}""", "treeKind")]
    [InlineData("""{"nodeId":1,"selector":"#Form"}""", "selector")]
    [InlineData("[1]", "params")]
    public async Task GetTree_InvalidParams_ReturnInvalidParam(string parameters, string param)
    {
        using AutomationHost host = StartHost(() => []);
        await using AutomationPipeTestClient client = await Connect();

        JsonElement response = await client.SendRawAsync($$"""{"id":"p","method":"get_tree","params":{{parameters}}}""");

        Assert.Equal(param, Error(response, AutomationErrorCodes.InvalidParam).GetProperty("details").GetProperty("param").GetString());
    }

    [AvaloniaFact]
    public async Task GetTree_Logical_WalksLogicalChildren_AndListsPopupContentOnce()
    {
        using AutomationHost host = StartHost(() => Windows);
        var panel = new StackPanel { Name = "PopupPanel" };
        var popup = new Popup
        {
            Child = new Border
            {
                Name = "PopupContent",
                Width = 40,
                Height = 30,
            },
        };
        panel.Children.Add(popup);
        ShowWindow("OwnerWindow", null, panel);
        popup.IsOpen = true;
        await using AutomationPipeTestClient client = await Connect();

        JsonElement roots = Result(await GetTree(client, new { treeKind = "Logical", depth = 50 }));

        JsonElement windowRoot = Assert.Single(roots.EnumerateArray(), root => HasName(root, "OwnerWindow"));
        // The window's content is its direct logical child; in the visual tree it sits under the window template.
        Assert.Contains(windowRoot.GetProperty("children").EnumerateArray(), node => HasName(node, "PopupPanel"));
        Assert.Single(AllNodes(roots), node => HasName(node, "PopupContent"));
    }

    /// <summary>A view model with the property the <c>[dc.Callsign=...]</c> selector reads.</summary>
    public sealed record StripRow(string Callsign);

    /// <summary>A view model whose property has a setter and no getter.</summary>
    public sealed class WriteOnlyRow
    {
        public string Secret
        {
            set { }
        }
    }
}
