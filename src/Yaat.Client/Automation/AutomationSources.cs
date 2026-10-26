using Avalonia.Controls;
using Yaat.Client.Automation.Tools;

namespace Yaat.Client.Automation;

/// <summary>
/// Where an <see cref="AutomationHost"/> reads the windows, simulation state and app tools it serves, and the clock its
/// wait handlers measure their timeouts on. The app passes <see cref="TimeProvider.System"/>; a test passes a clock it
/// advances itself, so a wait's timeout runs on the test's time rather than the wall's.
/// </summary>
/// <param name="Roots">The top-level windows to report; read on the UI thread.</param>
/// <param name="State">The simulation state, or null while the main window is not up; read on the UI thread.</param>
/// <param name="Tools">The app tools bound to the main window's view model, or null while it is not up; read on the UI thread.</param>
/// <param name="Time">The clock a wait's timeout and polls are measured on.</param>
public sealed record AutomationSources(
    Func<IEnumerable<TopLevel>> Roots,
    Func<IAutomationState?> State,
    Func<AutomationTools?> Tools,
    TimeProvider Time
);
