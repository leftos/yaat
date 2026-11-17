using Avalonia;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The Scope view's elastic heading vector: the EuroScope AHDG field press starts it for its aircraft, a confirmed vector
/// sends "FH {heading}" through the view model's pilot-command seam, and starting a route takes an armed vector with it, so only
/// one mode is up at a time.
/// </summary>
public partial class ScopeView
{
    private ScopeViewModel? _headingVectorVm;

    partial void WireHeadingVector() => Canvas.HeadingVectorConfirmed += OnHeadingVectorConfirmed;

    partial void AttachHeadingVector(ScopeViewModel vm)
    {
        _headingVectorVm = vm;
        vm.DrawRoute.Changed += OnDrawRouteStarted;
    }

    partial void DetachHeadingVector(ScopeViewModel vm)
    {
        vm.DrawRoute.Changed -= OnDrawRouteStarted;
        _headingVectorVm = null;
        Canvas.CancelHeadingVector();
    }

    // The EuroScope AHDG field: leaves a route being drawn, then starts the vector from the field's press point, inert for an
    // aircraft that may not be commanded.
    private void StartFieldHeadingVector(ScopeViewModel vm, AircraftModel ac, Point pos)
    {
        if (!AcceptsFlightCommands(ac))
        {
            return;
        }

        if (vm.DrawRoute.IsDrawing)
        {
            vm.DrawRoute.Cancel();
        }

        Canvas.EnterHeadingVector(ac.Callsign, pos);
    }

    // A route starting, from the menu or the Destination field, takes an armed heading vector with it.
    private void OnDrawRouteStarted()
    {
        if (_headingVectorVm is { DrawRoute.IsDrawing: true })
        {
            Canvas.CancelHeadingVector();
        }
    }

    private async void OnHeadingVectorConfirmed(string callsign, int heading)
    {
        if (_headingVectorVm is not { } vm)
        {
            return;
        }

        await vm.FlyHeadingAsync(callsign, heading);
    }
}
