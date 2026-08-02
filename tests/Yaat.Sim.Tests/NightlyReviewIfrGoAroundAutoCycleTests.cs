using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// Nightly-review repro: an IFR go-around must NOT auto-enter the VFR traffic pattern.
///
/// Contract (docs/phases.md and the comment at PhaseRunner.cs:105): "Instrument approach traffic
/// does NOT auto-enter the pattern — they fly runway heading to 2000 AGL and await instructions."
/// <see cref="GoAroundHelper.ResolvePatternIntent"/> enforces this by returning null for a non-in-pattern
/// IFR aircraft, so the resulting <see cref="GoAroundPhase"/> has ReenterPattern=false.
///
/// But the persistent <see cref="AircraftPattern.TrafficDirection"/> (set by an earlier MLT/MRT and
/// deliberately surviving FH/TR/TL phase clears) is read by PhaseRunner's auto-cycle guard WITHOUT
/// checking ReenterPattern, so a completed IFR go-around is cranked into a full VFR circuit anyway.
/// </summary>
public class NightlyReviewIfrGoAroundAutoCycleTests
{
    private readonly ITestOutputHelper _output;

    public NightlyReviewIfrGoAroundAutoCycleTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private static RunwayInfo? Runway(string designator) => NavigationDatabase.Instance.GetRunway("OAK", designator);

    [Fact]
    public void IfrGoAround_WithStalePersistentPatternDirection_DoesNotAutoEnterPattern()
    {
        var runway = Runway("28R");
        if (runway is null)
        {
            return;
        }

        // IFR aircraft, already climbed through 2000 ft AGL so the self-clearing go-around completes
        // on the first PhaseRunner tick.
        var aircraft = new AircraftState
        {
            Callsign = "N85439",
            AircraftType = "C172",
            AirportId = "OAK",
            Position = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt + 2500,
            IndicatedAirspeed = 90,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { FlightRules = "IFR", Destination = "KOAK" },
        };

        // A prior MLT stamped the persistent pattern direction; it survives a subsequent vector / new
        // approach clearance (Pattern.TrafficDirection is only cleared by CLAND/LAHSO/force-landing).
        aircraft.Pattern.TrafficDirection = PatternDirection.Left;

        // The go-around GoAroundHelper.Trigger builds for a non-in-pattern IFR aircraft: no MAP data,
        // so it self-clears at 2000 AGL and is the last phase. phases.TrafficDirection stays null.
        aircraft.Phases = new PhaseList { AssignedRunway = runway };
        aircraft.Phases.Add(
            new GoAroundPhase
            {
                ReenterPattern = false,
                TargetAltitude = null,
                NextLandingFullStop = true,
            }
        );

        var ctx = CommandDispatcher.BuildMinimalContext(aircraft, groundLayout: null);
        aircraft.Phases.Start(ctx);
        Assert.IsType<GoAroundPhase>(aircraft.Phases.CurrentPhase);
        Assert.Null(aircraft.Phases.TrafficDirection);

        // One tick: the go-around is already above 2000 AGL, so it completes and PhaseRunner runs its
        // post-completion routing.
        PhaseRunner.Tick(aircraft, ctx);

        var appendedPatternLegs = aircraft.Phases?.Phases.Where(p =>
            p is UpwindPhase or CrosswindPhase or DownwindPhase or BasePhase or PatternEntryPhase
        )
            .ToList() ?? new List<Phase>();

        foreach (var leg in appendedPatternLegs)
        {
            _output.WriteLine($"WRONGLY appended pattern leg: {leg.GetType().Name}");
        }

        Assert.True(
            appendedPatternLegs.Count == 0,
            $"An IFR go-around (ReenterPattern=false) must not auto-enter the pattern, but PhaseRunner "
                + $"appended {appendedPatternLegs.Count} pattern leg(s): {string.Join(", ", appendedPatternLegs.Select(p => p.GetType().Name))}"
        );
    }

    /// <summary>
    /// Control: a VFR aircraft with the same persistent direction SHOULD re-enter the pattern
    /// (ResolvePatternIntent returns the direction → ReenterPattern=true). This pins the intended
    /// behavior so the fix only suppresses the IFR case.
    /// </summary>
    [Fact]
    public void VfrGoAround_WithPersistentPatternDirection_DoesAutoEnterPattern()
    {
        var runway = Runway("28R");
        if (runway is null)
        {
            return;
        }

        var aircraft = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            AirportId = "OAK",
            Position = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt + 2500,
            IndicatedAirspeed = 90,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { FlightRules = "VFR", Destination = "KOAK" },
        };
        aircraft.Pattern.TrafficDirection = PatternDirection.Left;

        aircraft.Phases = new PhaseList { AssignedRunway = runway, TrafficDirection = PatternDirection.Left };
        aircraft.Phases.Add(
            new GoAroundPhase
            {
                ReenterPattern = true,
                TargetAltitude = null,
                NextLandingFullStop = false,
            }
        );

        var ctx = CommandDispatcher.BuildMinimalContext(aircraft, groundLayout: null);
        aircraft.Phases.Start(ctx);
        PhaseRunner.Tick(aircraft, ctx);

        var appendedPatternLegs = aircraft.Phases?.Phases.Where(p =>
            p is UpwindPhase or CrosswindPhase or DownwindPhase or BasePhase
        )
            .ToList() ?? new List<Phase>();

        Assert.True(appendedPatternLegs.Count > 0, "A VFR pattern go-around should auto-enter the pattern.");
    }
}
