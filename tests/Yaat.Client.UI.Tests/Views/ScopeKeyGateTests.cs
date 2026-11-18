using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Scope's own keys (Esc's chain, Ctrl+M, Ctrl+Shift+T) act only while the keyboard focus is inside the Scope: docked as
/// the main window's tab, Esc and Ctrl+T typed in the command box stay the command box's and Take Control's, and Ctrl+M
/// there does not arm the Scope's measuring; with the focus on the Scope canvas, or anywhere in the Scope's pop-out window,
/// they are the Scope's.
/// </summary>
public class ScopeKeyGateTests
{
    private static RawInputModifiers Action => PlatformHelper.IsMacOS ? RawInputModifiers.Meta : RawInputModifiers.Control;

    // The main window with the terminal docked, the Scope tab selected and the primary Scope holding a typed entry.
    private static async Task<(MainWindow Main, MainViewModel Vm, ScopeViewModel Scope)> BootDockedAsync()
    {
        (MainWindow main, MainViewModel vm) = await MainWindowHost.BootDockedScopeAsync();
        ScopeViewModel scope = vm.PrimaryScope;
        scope.TypeEntryText("XYZ");
        return (main, vm, scope);
    }

    private static void FocusCommandBox(MainWindow main)
    {
        TextBox box = main.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "CommandInput");
        Assert.True(box.Focus());
        Dispatcher.UIThread.RunJobs();
    }

    private static void FocusScopeCanvas(MainWindow main)
    {
        ScopeView view = Assert.Single(main.GetVisualDescendants().OfType<ScopeView>());
        Assert.True(view.Canvas.Focus());
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Docked_CommandBoxFocus_EscGoesToTheCommandBox()
    {
        (MainWindow main, MainViewModel vm, ScopeViewModel scope) = await BootDockedAsync();
        try
        {
            FocusCommandBox(main);
            vm.CommandText = "abc";

            main.DispatchKey(Key.Escape);

            Assert.Equal("", vm.CommandText);
            Assert.Equal("XYZ", scope.EntryText);
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact]
    public async Task Docked_CommandBoxFocus_CtrlTIsTakeControl()
    {
        (MainWindow main, MainViewModel vm, ScopeViewModel _) = await BootDockedAsync();
        try
        {
            FocusCommandBox(main);
            vm.SelectedAircraft = ScopeTestFixture.AddAircraft(vm, "UAL1", 11000);
            vm.StatusText = "";

            main.DispatchKey(Key.T, RawInputModifiers.Control);

            Assert.Equal("Cannot take control — not in a multi-user room", vm.StatusText);
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact]
    public async Task Docked_CommandBoxFocus_CtrlMDoesNotArmTheScopeMeasure()
    {
        (MainWindow main, MainViewModel vm, ScopeViewModel scope) = await BootDockedAsync();
        try
        {
            FocusCommandBox(main);

            main.DispatchKey(Key.M, Action);

            Assert.Null(scope.EntryResponse);
            // The Scope is the measuring surface from the start, so the gate shows as the Scope never taking it itself.
            Assert.Null(vm.HeldMeasureScope);
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact]
    public async Task Docked_CanvasFocus_EscRunsTheScopeChain()
    {
        (MainWindow main, MainViewModel vm, ScopeViewModel scope) = await BootDockedAsync();
        try
        {
            FocusScopeCanvas(main);
            vm.CommandText = "abc";

            main.DispatchKey(Key.Escape);

            Assert.Equal("", scope.EntryText);
            Assert.Equal("abc", vm.CommandText);
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact]
    public async Task Docked_CanvasFocus_CtrlMArmsTheScopeMeasure()
    {
        (MainWindow main, MainViewModel vm, ScopeViewModel scope) = await BootDockedAsync();
        try
        {
            FocusScopeCanvas(main);

            main.DispatchKey(Key.M, Action);

            Assert.NotNull(scope.EntryResponse);
            Assert.Equal(RblView.Scope, vm.ActiveMeasureView);
            Assert.Same(scope, vm.ActiveScope);
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact]
    public async Task Docked_CanvasFocus_CtrlShiftTFlipsTdm()
    {
        (MainWindow main, MainViewModel _, ScopeViewModel scope) = await BootDockedAsync();
        try
        {
            FocusScopeCanvas(main);
            bool before = scope.IsTdm;

            main.DispatchKey(Key.T, Action | RawInputModifiers.Shift);

            Assert.Equal(!before, scope.IsTdm);
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact]
    public async Task Docked_CommandBoxFocus_CtrlShiftTLeavesTdm()
    {
        (MainWindow main, MainViewModel _, ScopeViewModel scope) = await BootDockedAsync();
        try
        {
            FocusCommandBox(main);
            bool before = scope.IsTdm;

            main.DispatchKey(Key.T, Action | RawInputModifiers.Shift);

            Assert.Equal(before, scope.IsTdm);
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact]
    public async Task PoppedOut_FocusInItsWindow_EscCtrlMAndCtrlShiftTAreTheScopes()
    {
        (MainWindow main, MainViewModel vm, ScopeViewModel scope) = await BootDockedAsync();
        try
        {
            vm.IsScopePoppedOut = true;
            MainWindowHost.Pump();
            ScopeWindow window = Assert.IsType<ScopeWindow>(main.ScopeWindow);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            bool tdmBefore = scope.IsTdm;

            window.DispatchKey(Key.Escape);
            Assert.Equal("", scope.EntryText);

            window.DispatchKey(Key.M, Action);
            Assert.NotNull(scope.EntryResponse);

            window.DispatchKey(Key.T, Action | RawInputModifiers.Shift);
            Assert.Equal(!tdmBefore, scope.IsTdm);
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }
}
