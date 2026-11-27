using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Sim;

namespace Yaat.Client.Views.Scope;

/// <summary>One item of a RUNDOWN entry's right-click menu.</summary>
/// <param name="Header">The item's text.</param>
/// <param name="Command">
/// The command the item sends, with its AS prefix; for an item that asks for text, the part before that text. Null when there is
/// no position to act as.
/// </param>
/// <param name="AsksForText">Whether a click asks for text first (Hold… and Modify…).</param>
/// <param name="SendsWithoutText">Whether an item that asks for text sends its command alone for empty text (Hold…).</param>
/// <param name="IsEnabled">Whether the entry's status, its place in the list and the channel's positions allow the item.</param>
public sealed record RundownMenuItem(string Header, string? Command, bool AsksForText, bool SendsWithoutText, bool IsEnabled)
{
    /// <summary>The text an item that asks for text opens its box with (Modify…: the entry's current message); empty for the others.</summary>
    public string InitialText { get; init; } = "";
}

/// <summary>
/// The right-click menu of one RUNDOWN entry: the coordination verbs CoordinationCommandHandler takes for an item, each enabled only
/// for the statuses it accepts, each sent with an AS prefix naming the position it acts as (the channel's receiver for Acknowledge,
/// the item's origin TCP for the others), since the list shows every channel whatever the active position.
/// </summary>
public static class RundownEntryMenu
{
    private static readonly ILogger Log = AppLog.CreateLogger("RundownEntryMenu");

    /// <summary>The Hold… item's header.</summary>
    public const string HoldHeader = "Hold…";

    /// <summary>The Modify… item's header.</summary>
    public const string ModifyHeader = "Modify…";

    /// <summary>
    /// The menu for <paramref name="item"/> in <paramref name="list"/>, in a fixed order: Acknowledge, Release, Recall, Hold…,
    /// Modify…, Delete, Move up, Move down. An unknown status enables only Delete; no origin TCP disables all but Acknowledge.
    /// </summary>
    public static IReadOnlyList<RundownMenuItem> Items(CoordinationListHubDto list, CoordinationItemHubDto item)
    {
        string? origin = string.IsNullOrWhiteSpace(item.OriginTcp) ? null : item.OriginTcp;
        return [Acknowledge(list, item), .. StatusItems(list.ListId, item.Status, origin, item.Message), .. MoveItems(list, item, origin)];
    }

    // The receiver acknowledges; with no receiver on the channel there is no one to act as.
    private static RundownMenuItem Acknowledge(CoordinationListHubDto list, CoordinationItemHubDto item)
    {
        if (list.ReceiverTcps.Count == 0)
        {
            return new RundownMenuItem("Acknowledge", null, false, false, false);
        }

        return new RundownMenuItem(
            "Acknowledge",
            $"AS {list.ReceiverTcps[0]} RDACK {list.ListId}",
            false,
            false,
            item.Status == nameof(StarsCoordinationStatus.Unacknowledged)
        );
    }

    // Release, Recall, Hold…, Modify… and Delete, sent as the item's origin TCP; Modify… opens with the entry's message.
    private static IEnumerable<RundownMenuItem> StatusItems(string id, string status, string? origin, string message)
    {
        const string Unsent = nameof(StarsCoordinationStatus.Unsent);
        const string Recalled = nameof(StarsCoordinationStatus.Recalled);
        bool known = RundownRows.IsKnownStatus(status);
        string release = (status == Unsent) ? $"RDH {id}" : $"RD {id}";
        yield return Sender("Release", origin, release, false, status is Unsent or Recalled);
        yield return Sender("Recall", origin, $"RDR {id}", false, known && (status != Recalled));
        yield return Sender(HoldHeader, origin, $"RDH {id}", true, status == Recalled) with
        {
            SendsWithoutText = true,
        };
        yield return Sender(ModifyHeader, origin, $"RDTXT /{id}", true, status == Unsent) with
        {
            InitialText = message,
        };
        yield return Sender("Delete", origin, $"RDDEL {id}", false, true);
    }

    // Move up and Move down, to the item's 1-based place in the list less or plus one; an unknown status moves nothing.
    private static IEnumerable<RundownMenuItem> MoveItems(CoordinationListHubDto list, CoordinationItemHubDto item, string? origin)
    {
        int index = list.Items.FindIndex(i => i.Id == item.Id) + 1;
        bool movable = RundownRows.IsKnownStatus(item.Status) && (index > 0);
        yield return Sender("Move up", origin, $"RDPOS {list.ListId} {index - 1}", false, movable && (index > 1));
        yield return Sender("Move down", origin, $"RDPOS {list.ListId} {index + 1}", false, movable && (index < list.Items.Count));
    }

    // An item sent as the origin TCP: no origin leaves it without a command and disabled.
    private static RundownMenuItem Sender(string header, string? origin, string command, bool asksForText, bool allowed) =>
        (origin is null)
            ? new RundownMenuItem(header, null, asksForText, false, false)
            : new RundownMenuItem(header, $"AS {origin} {command}", asksForText, false, allowed);

    /// <summary>
    /// The command an item that asks for text sends with <paramref name="text"/>: the item's command, a space and the text; Hold…
    /// with empty text sends its command alone, Modify… with empty text sends nothing (null).
    /// </summary>
    public static string? Compose(RundownMenuItem item, string text)
    {
        if (item.Command is not { } command)
        {
            return null;
        }

        string trimmed = text.Trim();
        if (trimmed.Length > 0)
        {
            return $"{command} {trimmed}";
        }

        return item.SendsWithoutText ? command : null;
    }

    /// <summary>
    /// A context menu of <paramref name="items"/>, disabled ones shown greyed. A click on an item sends its command through
    /// <paramref name="send"/>; an item that asks for text first asks <paramref name="prompt"/> with its header and its initial text,
    /// sending nothing when the prompt answers null (dismissed) or <see cref="Compose"/> gives nothing.
    /// </summary>
    public static ContextMenu Build(IReadOnlyList<RundownMenuItem> items, Action<string> send, Func<string, string, Task<string?>> prompt)
    {
        var menu = new ContextMenu();
        foreach (RundownMenuItem item in items)
        {
            var menuItem = new MenuItem { Header = item.Header, IsEnabled = item.IsEnabled };
            menuItem.Click += (_, _) => _ = ClickAsync(item, send, prompt);
            menu.Items.Add(menuItem);
        }

        return menu;
    }

    /// <summary>
    /// What a click on <paramref name="item"/> does: sends its command, asking for text first when it asks for it. A failure of
    /// the send or the prompt is logged as an error and never escapes, since the click does not await it.
    /// </summary>
    internal static async Task ClickAsync(RundownMenuItem item, Action<string> send, Func<string, string, Task<string?>> prompt)
    {
        try
        {
            await RunAsync(item, send, prompt);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "RUNDOWN menu item {Header} ({Command}) failed", item.Header, item.Command);
        }
    }

    private static async Task RunAsync(RundownMenuItem item, Action<string> send, Func<string, string, Task<string?>> prompt)
    {
        if (item.Command is not { } command)
        {
            return;
        }

        if (!item.AsksForText)
        {
            send(command);
            return;
        }

        if ((await prompt(item.Header, item.InitialText) is { } text) && (Compose(item, text) is { } composed))
        {
            send(composed);
        }
    }
}
