using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Automation;
using Yaat.Client.Logging;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Automation;

/// <summary>
/// A never-activated window is still an activatable top-level window to Windows: when the foreground window is minimized,
/// the system activates the next window in Z-order (a closed one hands it back to the previous window instead), which can
/// be the driven client (YAAT-245). Only <c>WS_EX_NOACTIVATE</c> stops that, so in automation mode every client window is
/// given it by the Win32 style callback <see cref="AutomationGate.ApplyShowActivated"/> registers, and keeps
/// <c>WS_EX_APPWINDOW</c> so its taskbar button stays. The registration itself only shows on the Win32 backend:
/// <c>live-check.ps1</c> reads every client window's extended style and asserts the bit.
/// </summary>
public class AutomationNoActivateStyleTests
{
    private const uint WsExNoActivate = 0x08000000;
    private const uint WsExAppWindow = 0x00040000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsCaptionAndSysMenu = 0x00C80000;

    [Theory]
    [InlineData(WsExAppWindow, WsExAppWindow | WsExNoActivate)]
    [InlineData(0u, WsExNoActivate)]
    [InlineData(WsExToolWindow | WsExAppWindow, WsExToolWindow | WsExAppWindow | WsExNoActivate)]
    [InlineData(WsExAppWindow | WsExNoActivate, WsExAppWindow | WsExNoActivate)]
    public void NoActivateStyles_AddsNoActivate_AndKeepsEveryOtherBit(uint exStyle, uint expectedExStyle)
    {
        (uint style, uint ex) = AutomationGate.NoActivateStyles(WsCaptionAndSysMenu, exStyle);

        Assert.Equal(WsCaptionAndSysMenu, style);
        Assert.Equal(expectedExStyle, ex);
    }

    [AvaloniaFact]
    public void ApplyShowActivated_SuppressesActivation_OnlyWhileTheGateIsOn()
    {
        var window = new Window();
        try
        {
            AutomationMode.IsEnabled = false;
            AutomationGate.ApplyShowActivated(window);
            Assert.True(window.ShowActivated);

            AutomationMode.IsEnabled = true;
            AutomationGate.ApplyShowActivated(window);
            Assert.False(window.ShowActivated);
        }
        finally
        {
            AutomationMode.IsEnabled = false;
            window.Close();
        }
    }

    // The headless backend gives a window no HWND, so the test replaces the cloaker seam (the real one finds the HWND and calls
    // DwmSetWindowAttribute(DWMWA_CLOAK)) and observes which windows the gate asks it to cloak; live-check covers the DWM call.
    [AvaloniaFact]
    public void ApplyShowActivated_CloaksTheWindow_OnlyWhileAutomationAndCloakAreOn()
    {
        var window = new Window();
        Func<WindowBase, bool, string?> previousCloaker = AutomationGate.Cloaker;
        List<(WindowBase Window, bool Cloaked)> calls = [];
        AutomationGate.Cloaker = (target, cloaked) =>
        {
            calls.Add((target, cloaked));
            return null;
        };
        try
        {
            AutomationMode.IsEnabled = true;
            AutomationGate.CloakWindows = false;
            AutomationGate.ApplyShowActivated(window);
            Assert.Empty(calls);

            AutomationMode.IsEnabled = false;
            AutomationGate.CloakWindows = true;
            AutomationGate.ApplyShowActivated(window);
            Assert.Empty(calls);

            AutomationMode.IsEnabled = true;
            AutomationGate.ApplyShowActivated(window);
            Assert.Equal([(window, true)], calls);
        }
        finally
        {
            AutomationGate.Cloaker = previousCloaker;
            AutomationGate.CloakWindows = false;
            AutomationMode.IsEnabled = false;
            window.Close();
        }
    }

    // An uncloaked window on the desktop is what cloaking prevents, so a cloak that fails before the first show ends the client.
    // The exit seam is replaced: the real one kills the test host.
    [AvaloniaFact]
    public void ApplyShowActivated_CloakFails_LogsTheErrorAndExitsWithCode3()
    {
        var window = new Window();
        Func<WindowBase, bool, string?> previousCloaker = AutomationGate.Cloaker;
        Action<int> previousExit = AutomationGate.Exit;
        List<int> exitCodes = [];
        AutomationGate.Cloaker = (_, _) => "DwmSetWindowAttribute(DWMWA_CLOAK, 1) failed with HRESULT 0x80070005";
        AutomationGate.Exit = exitCodes.Add;
        long before = AppLog.RecentErrors.LastSequence;
        try
        {
            AutomationMode.IsEnabled = true;
            AutomationGate.CloakWindows = true;
            AutomationGate.ApplyShowActivated(window);

            Assert.Equal([3], exitCodes);
            Assert.Contains(
                AppLog.RecentErrors.Since(before, 50),
                entry =>
                    (entry.Category == "AutomationGate")
                    && entry.Message.Contains(
                        "could not cloak Window before its first show (DwmSetWindowAttribute(DWMWA_CLOAK, 1) failed with HRESULT 0x80070005)",
                        StringComparison.Ordinal
                    )
            );
        }
        finally
        {
            AutomationGate.Cloaker = previousCloaker;
            AutomationGate.Exit = previousExit;
            AutomationGate.CloakWindows = false;
            AutomationMode.IsEnabled = false;
            window.Close();
        }
    }

    // A window built before a set_cloaked call and opened after it takes the new state when it opens; one whose state did not
    // change between build and open is left alone.
    [AvaloniaFact]
    public void OpenedWindow_TakesTheCloakStateSetAfterItWasBuilt()
    {
        var changed = new Window();
        var unchanged = new Window();
        Func<WindowBase, bool, string?> previousCloaker = AutomationGate.Cloaker;
        List<(WindowBase Window, bool Cloaked)> calls = [];
        AutomationGate.Cloaker = (target, cloaked) =>
        {
            calls.Add((target, cloaked));
            return null;
        };
        try
        {
            AutomationMode.IsEnabled = true;
            AutomationGate.CloakWindows = true;
            AutomationGate.ApplyShowActivated(changed);
            AutomationGate.CloakWindows = false;
            AutomationGate.ApplyShowActivated(unchanged);

            changed.Show();
            unchanged.Show();

            Assert.Equal([(changed, true), (changed, false)], calls);
        }
        finally
        {
            AutomationGate.Cloaker = previousCloaker;
            AutomationGate.CloakWindows = false;
            AutomationMode.IsEnabled = false;
            changed.Close();
            unchanged.Close();
        }
    }
}
