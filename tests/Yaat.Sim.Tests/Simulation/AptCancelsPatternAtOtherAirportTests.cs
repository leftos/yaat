using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// N428KK was flying the SJC 30R pattern (<c>ERD 30R, CLAND</c>) when the instructor moved it to
/// Oakland with <c>APT OAK</c> and re-entered the pattern there (<c>ELB 28L 4, CLAND</c>). The
/// destination change left the SJC phase chain — and with it <c>AssignedRunway</c> — in place, so the
/// new entry resolved its runway against SJC and was refused with "Runway 28L not found at SJC".
/// An APT to another airport must cancel the pattern flown to the old one, with the same warning a
/// phase-clearing vector emits, so the next entry resolves against the new destination — whether the
/// entry arrives in its own transmission or chained behind the APT with a semicolon.
/// </summary>
public class AptCancelsPatternAtOtherAirportTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly IDisposable _navScope;

    public AptCancelsPatternAtOtherAirportTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
        _navScope = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb!);
    }

    public void Dispose() => _navScope.Dispose();

    private static DispatchContext Ctx() => TestDispatch.Context(new Random(42), validateDctFixes: false);

    private CommandResult Dispatch(AircraftState aircraft, string text)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        CommandResult result = CommandDispatcher.DispatchCompound(parsed.Value!, aircraft, Ctx());
        _output.WriteLine($"{text} -> success={result.Success} message={result.Message}");
        return result;
    }

    /// <summary>A VFR light single on the right downwind for SJC 30R, cleared to land.</summary>
    private AircraftState MakeSjcPatternAircraft()
    {
        RunwayInfo runway = NavigationDatabase.Instance.GetRunway("KSJC", "30R")!;
        Assert.NotNull(runway);

        TrueHeading reciprocal = runway.TrueHeading.ToReciprocal();
        (double Lat, double Lon) centerline = GeoMath.ProjectPoint(runway.ThresholdLatitude, runway.ThresholdLongitude, reciprocal, 2.5);
        var patternSide = new TrueHeading((runway.TrueHeading.Degrees + 90) % 360);
        (double Lat, double Lon) position = GeoMath.ProjectPoint(centerline.Lat, centerline.Lon, patternSide, 1.5);

        var aircraft = new AircraftState
        {
            Callsign = "N428KK",
            AircraftType = "C172",
            Position = new LatLon(position.Lat, position.Lon),
            Altitude = 1500,
            TrueHeading = reciprocal,
            IndicatedAirspeed = 100,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                FlightRules = "VFR",
                Destination = "KSJC",
            },
        };

        CommandResult entry = Dispatch(aircraft, "ERD 30R, CLAND");
        Assert.True(entry.Success, entry.Message);
        Assert.Equal(ClearanceType.ClearedToLand, aircraft.Phases!.LandingClearance);
        Assert.Equal("SJC", NavigationDatabase.NormalizeAirport(aircraft.Phases.AssignedRunway!.AirportId));
        aircraft.PendingWarnings.Clear();

        return aircraft;
    }

    [Fact]
    public void AptThenPatternEntryAtNewAirport_ResolvesRunwayAtNewAirport()
    {
        AircraftState aircraft = MakeSjcPatternAircraft();

        CommandResult changed = Dispatch(aircraft, "APT OAK");
        Assert.True(changed.Success, changed.Message);
        Assert.Equal("KOAK", aircraft.FlightPlan.Destination);
        Assert.Null(aircraft.Phases);
        Assert.Contains(aircraft.PendingWarnings, w => w.Contains("pattern to RWY 30R cancelled by APT OAK", StringComparison.Ordinal));

        CommandResult entry = Dispatch(aircraft, "ELB 28L 4, CLAND");

        Assert.True(entry.Success, entry.Message);
        Assert.Equal("OAK", NavigationDatabase.NormalizeAirport(aircraft.Phases!.AssignedRunway!.AirportId));
        Assert.Equal("28L", aircraft.Phases.AssignedRunway.Designator);
        Assert.Equal(ClearanceType.ClearedToLand, aircraft.Phases.LandingClearance);
    }

    [Fact]
    public void AptChainedInSameBlockAsPatternEntry_ResolvesRunwayAtNewAirport()
    {
        AircraftState aircraft = MakeSjcPatternAircraft();

        CommandResult result = Dispatch(aircraft, "APT OAK, ELB 28L 4, CLAND");

        Assert.True(result.Success, result.Message);
        Assert.Equal("KOAK", aircraft.FlightPlan.Destination);
        Assert.Equal("OAK", NavigationDatabase.NormalizeAirport(aircraft.Phases!.AssignedRunway!.AirportId));
        Assert.Equal("28L", aircraft.Phases.AssignedRunway.Designator);
        Assert.Equal(ClearanceType.ClearedToLand, aircraft.Phases.LandingClearance);
        Assert.Contains(aircraft.PendingWarnings, w => w.Contains("pattern to RWY 30R cancelled by APT OAK", StringComparison.Ordinal));
        Assert.NotNull(result.Message);
        Assert.Contains("Destination changed to KOAK", result.Message, StringComparison.Ordinal);
        Assert.Contains("28L", result.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The peeled head is committed before the rest of its own block runs, so a failure behind it reports
    /// both halves — what took effect and why the rest did not — instead of pretending nothing happened.
    /// The destination really is Oakland afterwards, and the SJC pattern really is gone.
    /// </summary>
    [Fact]
    public void LeadingTransparentHead_RestOfBlockFails_KeepsHeadAndReportsBoth()
    {
        AircraftState aircraft = MakeSjcPatternAircraft();

        CommandResult result = Dispatch(aircraft, "APT OAK, ELB 35L 4");

        Assert.False(result.Success);
        Assert.Equal("KOAK", aircraft.FlightPlan.Destination);
        Assert.Null(aircraft.Phases);
        Assert.Contains(aircraft.PendingWarnings, w => w.Contains("pattern to RWY 30R cancelled by APT OAK", StringComparison.Ordinal));
        Assert.NotNull(result.Message);
        Assert.Contains("Destination changed to KOAK", result.Message, StringComparison.Ordinal);
        Assert.Contains("; but ", result.Message, StringComparison.Ordinal);
        Assert.Contains("35L", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AptChainedAheadOfPatternEntry_ResolvesRunwayAtNewAirport()
    {
        AircraftState aircraft = MakeSjcPatternAircraft();

        CommandResult result = Dispatch(aircraft, "APT OAK; ELB 28L 4, CLAND");

        Assert.True(result.Success, result.Message);
        Assert.Equal("KOAK", aircraft.FlightPlan.Destination);
        Assert.Equal("OAK", NavigationDatabase.NormalizeAirport(aircraft.Phases!.AssignedRunway!.AirportId));
        Assert.Equal("28L", aircraft.Phases.AssignedRunway.Designator);
        Assert.Equal(ClearanceType.ClearedToLand, aircraft.Phases.LandingClearance);
        Assert.Contains(aircraft.PendingWarnings, w => w.Contains("pattern to RWY 30R cancelled by APT OAK", StringComparison.Ordinal));
    }
}
