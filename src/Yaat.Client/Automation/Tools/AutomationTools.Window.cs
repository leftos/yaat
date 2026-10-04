using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Views;

namespace Yaat.Client.Automation.Tools;

public sealed partial class AutomationTools
{
    private static readonly ILogger WindowLog = AppLog.CreateLogger("AutomationTools.Window");

    /// <summary>
    /// The client's open windows <c>set_cloaked</c> acts on: the desktop lifetime's window list outside tests. A headless test, which
    /// runs without a desktop lifetime, replaces it.
    /// </summary>
    public static Func<IReadOnlyList<Window>> OpenWindows { get; set; } = DesktopWindows;

    /// <summary>
    /// Why a tool that acts on the client's own windows cannot run now: it needs no server or room, so it is available whenever the
    /// main window is up (the pipe refuses every tool before that) and this returns null.
    /// </summary>
    public string? AlwaysAvailable() => null;

    [AutomationTool(
        "set_cloaked",
        "Cloaks or uncloaks every client window (DWM cloak): a cloaked window is not drawn on the desktop but record_start still "
            + "captures it. Windows opened afterwards open in the same state.",
        nameof(AlwaysAvailable)
    )]
    public Task<AppToolOutcome> SetCloaked([Description("True to cloak every window, false to show them on the desktop again.")] bool cloaked)
    {
        bool previous = AutomationGate.CloakWindows;
        AutomationGate.CloakWindows = cloaked;
        IReadOnlyList<Window> windows = [.. OpenWindows()];
        string verb = cloaked ? "cloak" : "uncloak";
        List<string> failures = [];
        foreach (Window window in windows)
        {
            string? failure = AutomationGate.ApplyCloak(window, cloaked);
            if (failure is not null)
            {
                string title = string.IsNullOrEmpty(window.Title) ? $"(untitled {window.GetType().Name})" : window.Title;
                WindowLog.LogError("set_cloaked: could not {Verb} '{Title}': {Failure}", verb, title, failure);
                failures.Add($"'{title}': {failure}");
            }
        }

        string failed = string.Join("; ", failures);
        if ((windows.Count > 0) && (failures.Count == windows.Count))
        {
            // Not one window changed, so the flag goes back too: windows opened later match the ones already open.
            AutomationGate.CloakWindows = previous;
            return Task.FromResult(
                AppToolOutcome.Done(
                    $"Could not {verb} any of the {windows.Count} windows: {failed}. "
                        + $"Nothing changed: windows opened from now on still open {(previous ? "cloaked" : "uncloaked")}."
                )
            );
        }

        string done = $"{(cloaked ? "Cloaked" : "Uncloaked")} {windows.Count - failures.Count}";
        string message =
            (failures.Count == 0)
                ? $"{done} windows; windows opened from now on open {verb}ed."
                : $"{done} of {windows.Count} windows; could not {verb} {failed}. Windows opened from now on open {verb}ed.";
        return Task.FromResult(AppToolOutcome.Done(message));
    }

    private static IReadOnlyList<Window> DesktopWindows() =>
        (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) ? desktop.Windows : [];
}
