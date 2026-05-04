using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// When a command's dispatch causes <see cref="PhaseList.Clear"/> to fire (because
/// the active phase's <c>CanAcceptCommand</c> returns <c>ClearsPhase</c> for the
/// incoming verb), a warning must be appended to the aircraft's PendingWarnings
/// describing what was cancelled and which command did it. Without this, an RPO
/// who issues e.g. CM 025 to an aircraft on a pattern entry has no way to know
/// the entry was cancelled — the symptom only surfaces minutes later when the
/// aircraft sails past the airport.
/// </summary>
public class PhaseClearWarningTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly IDisposable _navScope;

    public PhaseClearWarningTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
        _navScope = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb!);
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
    }

    public void Dispose() => _navScope.Dispose();

    private static AircraftState MakeVfrAircraft(double lat = 37.87, double lon = -122.22, double alt = 3500, double headingDeg = 180) =>
        new()
        {
            Callsign = "N435C",
            AircraftType = "M20P",
            Position = new LatLon(lat, lon),
            TrueHeading = new TrueHeading(headingDeg),
            Altitude = alt,
            IndicatedAirspeed = 110,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                FlightRules = "VFR",
                Destination = "KOAK",
            },
        };

    private static DispatchContext Ctx() => TestDispatch.Context(Random.Shared, validateDctFixes: false);

    private static void DispatchOk(AircraftState ac, string text)
    {
        var parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        var result = CommandDispatcher.DispatchCompound(parsed.Value!, ac, Ctx());
        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public void PatternEntryCleared_EmitsWarning_NamingPatternAndRunwayAndCommand()
    {
        var ac = MakeVfrAircraft();
        DispatchOk(ac, "ERD 28R");

        // Aircraft from north-of-OAK heading south → should install Pattern Entry → Downwind → Base → Final → Landing
        Assert.NotNull(ac.Phases?.CurrentPhase);
        _output.WriteLine($"Pre-CM phase: {ac.Phases!.CurrentPhase!.GetType().Name}");
        ac.PendingWarnings.Clear();

        // CM is not in PatternEntryPhase's allowed list → ClearsPhase
        DispatchOk(ac, "CM 025");

        Assert.Null(ac.Phases?.CurrentPhase);
        var warning = Assert.Single(ac.PendingWarnings);
        _output.WriteLine($"Warning: {warning}");
        Assert.Contains("N435C", warning);
        Assert.Contains("cancelled", warning);
        Assert.Contains("CM 025", warning);
        // Smart summary: pattern chain detected → "pattern to RWY 28R"
        Assert.Contains("pattern", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("28R", warning);
    }

    [Fact]
    public void DownwindAllowsClimbMaintain_NoWarning()
    {
        var ac = MakeVfrAircraft();
        DispatchOk(ac, "ERD 28R");

        // Advance the phase list past pattern entry to the actual Downwind phase
        // (so the CM 025 hits Downwind which allows it, not PatternEntry which doesn't).
        // Easiest: skip directly by manipulating the index.
        while (ac.Phases?.CurrentPhase is not DownwindPhase && ac.Phases?.CurrentPhase is not null)
        {
            ac.Phases.AdvanceToNext(CommandDispatcher.BuildMinimalContext(ac));
        }
        Assert.IsType<DownwindPhase>(ac.Phases!.CurrentPhase);
        ac.PendingWarnings.Clear();

        DispatchOk(ac, "CM 025");

        // Downwind allows ClimbMaintain → phase preserved → no warning
        Assert.IsType<DownwindPhase>(ac.Phases.CurrentPhase);
        Assert.Empty(ac.PendingWarnings);
    }

    [Fact]
    public void NoActivePhase_NoWarning()
    {
        var ac = MakeVfrAircraft();
        Assert.Null(ac.Phases);

        DispatchOk(ac, "FH 270");

        // No phase to cancel → no warning
        Assert.Empty(ac.PendingWarnings);
    }

    [Fact]
    public void CompoundSourceText_IncludedVerbatim()
    {
        var ac = MakeVfrAircraft();
        DispatchOk(ac, "ERD 28R");
        Assert.NotNull(ac.Phases?.CurrentPhase);
        ac.PendingWarnings.Clear();

        DispatchOk(ac, "FH 270; CM 050");

        var warning = Assert.Single(ac.PendingWarnings);
        _output.WriteLine($"Warning: {warning}");
        Assert.Contains("FH 270; CM 050", warning);
    }
}
