using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Sim.Commands;
using Yaat.Sim.Testing;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Pins the Direct-to picker's fix list: the aircraft's route from the fix it is navigating to on — the navigation
/// route from that fix, then the filed route's fixes after the last of them, then the destination, each fix once and
/// in order, ignoring case, with the departure airport never listed. Without a navigating-to fix the whole route
/// (<see cref="IMenuAircraft.RouteFixNames"/>) comes back unchanged.
/// </summary>
public class MenuCatalogDirectToTests
{
    private const string Callsign = "N1AA";

    /// <summary>Real navigation data, so the list's filed-route expansion runs against the real database.</summary>
    public MenuCatalogDirectToTests() => TestVnasData.EnsureInitialized();

    private static AircraftModel Aircraft(string navigatingTo, string[] navRoute, string route, string destination, string departure) =>
        new()
        {
            Callsign = Callsign,
            NavigatingTo = navigatingTo,
            NavigationRoute = [.. navRoute],
            Route = route,
            Destination = destination,
            Departure = departure,
        };

    private static MenuContext Context() => TestMenuContext.Create(Callsign, "AB", null, false, VfrCommandsForIfr.None);

    /// <summary>The fixes the Direct-to picker lists for <paramref name="aircraft"/>, read off the built item's picker descriptor.</summary>
    private static IReadOnlyList<string> DirectToFixes(AircraftModel aircraft)
    {
        MenuItem? item = MenuCatalog.Get(MenuIds.NavigationDirectTo).Build(aircraft, Context(), new RecordingMenuHost(""));
        MenuPickerDescriptor descriptor = Assert.IsType<MenuPickerDescriptor>(item?.Tag);
        Assert.Equal(MenuPickerDescriptor.List, descriptor.Kind);
        return descriptor.Items;
    }

    [AvaloniaFact]
    public void NavigatingToMidRoute_ListsFromItOnThenTheRestOfTheFiledRouteThenTheDestination()
    {
        AircraftModel aircraft = Aircraft("ALTAM", ["SUNOL", "ALTAM", "MOD"], "OAK SUNOL ALTAM MOD LIN SAC", "KSAC", "");

        Assert.Equal(["ALTAM", "MOD", "LIN", "SAC", "KSAC"], DirectToFixes(aircraft));
    }

    [AvaloniaFact]
    public void LastNavRouteFixRepeatedInTheFiledRoute_AppendsAfterItsLastOccurrence()
    {
        AircraftModel aircraft = Aircraft("ALTAM", ["ALTAM", "MOD"], "OAK MOD SUNOL MOD LIN SAC", "KSAC", "");

        Assert.Equal(["ALTAM", "MOD", "LIN", "SAC", "KSAC"], DirectToFixes(aircraft));
    }

    [AvaloniaFact]
    public void NavigatingToFixInAnotherCase_KeepsTheNavigationRoutesOwnSpelling()
    {
        AircraftModel aircraft = Aircraft("altam", ["SUNOL", "ALTAM", "MOD"], "", "", "");

        Assert.Equal(["ALTAM", "MOD"], DirectToFixes(aircraft));
    }

    [AvaloniaFact]
    public void DestinationRepeatingAFlownFix_AppearsOnceSpelledAsTheRouteHasIt()
    {
        AircraftModel aircraft = Aircraft("SUNOL", ["SUNOL"], "OAK SUNOL MOD LIN SAC KSAC", "ksac", "");

        IReadOnlyList<string> fixes = DirectToFixes(aircraft);
        Assert.Equal(["SUNOL", "MOD", "LIN", "SAC", "KSAC"], fixes);
        Assert.DoesNotContain("ksac", fixes);
    }

    [AvaloniaFact]
    public void DepartureAirport_IsNeverListed()
    {
        AircraftModel aircraft = Aircraft("ALTAM", ["SUNOL", "ALTAM", "MOD"], "OAK SUNOL ALTAM MOD LIN SAC", "KSAC", "KOAK");

        IReadOnlyList<string> fixes = DirectToFixes(aircraft);
        Assert.Equal(["ALTAM", "MOD", "LIN", "SAC", "KSAC"], fixes);
        Assert.DoesNotContain("KOAK", fixes);
    }

    [AvaloniaFact]
    public void NoNavigatingToFix_KeepsTheAircraftsWholeRouteUnchanged()
    {
        AircraftModel aircraft = Aircraft("", ["SUNOL", "ALTAM"], "OAK SUNOL ALTAM MOD", "KSAC", "KOAK");

        IReadOnlyList<string> fixes = DirectToFixes(aircraft);
        Assert.Equal(aircraft.RouteFixNames(), fixes);
        Assert.Contains("OAK", fixes);
        Assert.Contains("KOAK", fixes);
    }

    [AvaloniaFact]
    public void NoNavigationRoute_ListsFromTheNavigatingToFixOnThroughTheFiledRouteThenTheDestination()
    {
        AircraftModel aircraft = Aircraft("CEDES", [], "OAK CEDES MOD", "KSMF", "");

        Assert.Equal(["CEDES", "MOD", "KSMF"], DirectToFixes(aircraft));
    }

    [AvaloniaFact]
    public void FiledRouteWithoutTheLastNavigationFix_AppendsNothingFromTheFiledRoute()
    {
        AircraftModel aircraft = Aircraft("ALTAM", ["SUNOL", "ALTAM"], "OAK CEDES MOD", "KSMF", "");

        Assert.Equal(["ALTAM", "KSMF"], DirectToFixes(aircraft));
    }

    [AvaloniaFact]
    public void NavigatingToNotInTheNavigationRoute_StartsFromItThenWalksTheFiledRouteFromTheRoutesLastFix()
    {
        AircraftModel aircraft = Aircraft("CEDES", ["SUNOL", "ALTAM"], "OAK SUNOL ALTAM MOD LIN SAC", "KSAC", "");

        Assert.Equal(["CEDES", "MOD", "LIN", "SAC", "KSAC"], DirectToFixes(aircraft));
    }
}
