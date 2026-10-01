using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

/// <summary>
/// Delayed-spawn (list-only) context menu items for the aircraft list. The phase-aware commands come from the shared
/// catalog group (<see cref="Yaat.Client.ContextMenus.SharedMenuGroups.AddListAircraftCommands"/>).
/// </summary>
public partial class DataGridView
{
    private static MenuItem MakeItem(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await action();
        return item;
    }

    private static readonly (string Label, int Seconds)[] SpawnDelayPresets =
    [
        ("15 seconds", 15),
        ("30 seconds", 30),
        ("1 minute", 60),
        ("2 minutes", 120),
        ("5 minutes", 300),
        ("10 minutes", 600),
    ];

    private static void AddDelayedSpawnItems(ContextMenu menu, MainViewModel vm, string callsign, string initials)
    {
        Task Cmd(string raw) => vm.Connection.SendCommandAsync(callsign, raw, initials);

        menu.Items.Add(MakeItem("Spawn now", () => Cmd("SPAWN")));

        var delayMenu = new MenuItem { Header = "Change spawn delay" };
        foreach ((string? label, int seconds) in SpawnDelayPresets)
        {
            delayMenu.Items.Add(MakeItem(label, () => Cmd($"SPAWNDELAY {seconds}")));
        }
        delayMenu.Items.Add(new Separator());
        delayMenu.Items.Add(BuildCustomDelayInput(menu, callsign, vm, initials));
        menu.Items.Add(delayMenu);

        menu.Items.Add(MakeItem("Delete", () => Cmd("DEL")));
    }

    private static TextBox BuildCustomDelayInput(ContextMenu parentMenu, string callsign, MainViewModel vm, string initials)
    {
        var textBox = new TextBox
        {
            PlaceholderText = "Custom (e.g. 90, 2m15s, 1h)",
            FontSize = 12,
            MinWidth = 180,
        };
        textBox.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            e.Handled = true;
            int? seconds = ParseDelayInput(textBox.Text);
            if (seconds is null)
            {
                return;
            }

            parentMenu.Close();
            await vm.Connection.SendCommandAsync(callsign, $"SPAWNDELAY {seconds.Value}", initials);
        };
        return textBox;
    }

    private static readonly Regex DelayUnitsPattern = new(
        @"^\s*(?:(?<h>\d+)\s*h)?\s*(?:(?<m>\d+)\s*m)?\s*(?:(?<s>\d+)\s*s)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    internal static int? ParseDelayInput(string? input)
    {
        string? trimmed = input?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (int.TryParse(trimmed, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int bareSeconds))
        {
            return bareSeconds;
        }

        Match match = DelayUnitsPattern.Match(trimmed);
        if (!match.Success)
        {
            return null;
        }

        Group hours = match.Groups["h"];
        Group minutes = match.Groups["m"];
        Group secs = match.Groups["s"];
        if (!hours.Success && !minutes.Success && !secs.Success)
        {
            return null;
        }

        long total = 0;
        if (hours.Success)
        {
            total += long.Parse(hours.Value, System.Globalization.CultureInfo.InvariantCulture) * 3600L;
        }
        if (minutes.Success)
        {
            total += long.Parse(minutes.Value, System.Globalization.CultureInfo.InvariantCulture) * 60L;
        }
        if (secs.Success)
        {
            total += long.Parse(secs.Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        if (total < 0 || total > int.MaxValue)
        {
            return null;
        }
        return (int)total;
    }
}
