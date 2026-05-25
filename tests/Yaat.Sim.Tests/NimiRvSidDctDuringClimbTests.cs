using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

/// <summary>
/// Regression probe: unconditional DCT during an active RV SID heading hold must release
/// <see cref="InitialClimbPhase"/>'s internal hold so the phase can complete and the
/// aircraft is not stuck with <c>_rvSidActive == true</c> while following a DCT route.
/// </summary>
public class NimiRvSidDctDuringClimbTests
{
    private const double ExpectedRvHeading = 315.0;

    private static (InitialClimbPhase Phase, AircraftState Aircraft, PhaseContext Ctx) BuildRvSidClimbHarness()
    {
        const double fieldElev = 6.0;
        var runway = TestRunwayFactory.Make(designator: "28R", airportId: "OAK", heading: 280.0, elevationFt: fieldElev);
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = new LatLon(37.728, -122.218),
            TrueHeading = new TrueHeading(280),
            Altitude = fieldElev + 1500,
            IndicatedAirspeed = 180,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KOAK",
                Destination = "KSAC",
                Route = "NIMI5 OAK V6 SAC",
                CruiseAltitude = 11000,
                FlightRules = "IFR",
            },
        };
        var climb = new InitialClimbPhase
        {
            Departure = new DefaultDeparture(),
            AssignedAltitude = 5000,
            DepartureRoute = [new NavigationTarget { Name = "FESIK", Position = new LatLon(38.2, -121.5) }],
            DepartureSidId = null,
            SidDepartureHeadingMagnetic = ExpectedRvHeading,
            IsVfr = false,
            CruiseAltitude = 11000,
        };
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = fieldElev,
            Logger = NullLogger.Instance,
        };
        ac.Phases = new PhaseList { AssignedRunway = runway };
        ac.Phases.Phases.Add(climb);
        climb.Status = PhaseStatus.Active;
        climb.OnStart(ctx);
        Assert.Same(climb, ac.Phases.CurrentPhase);
        return (climb, ac, ctx);
    }

    [Fact]
    public void DctDuringRvSidHold_ReleasesRvSidActiveSoPhaseCanComplete()
    {
        TestVnasData.EnsureInitialized();
        var (phase, ac, ctx) = BuildRvSidClimbHarness();
        Assert.False(ac.HasLeftStudentFrequency);

        var parsed = CommandParser.ParseCompound("DCT FESIK");
        Assert.True(parsed.IsSuccess);

        var dispatch = CommandDispatcher.DispatchCompound(
            parsed.Value!,
            ac,
            TestDispatch.Context(new SerializableRandom(42), validateDctFixes: false)
        );
        Assert.True(dispatch.Success, dispatch.Message);

        Assert.NotEmpty(ac.Targets.NavigationRoute);

        // Simulate one sub-tick: phase then physics (mirrors SimulationWorld.Tick).
        bool complete = phase.OnTick(ctx);
        FlightPhysics.Update(ac, 1.0, _ => null);

        Assert.False(complete, "Phase should not complete on the first tick after DCT");
        var dto = Assert.IsType<InitialClimbPhaseDto>(phase.ToSnapshot());
        Assert.False(
            dto.RvSidActive,
            "DCT during the RV SID hold must release _rvSidActive; otherwise InitialClimb never completes while a route is active."
        );

        // Climb to the assigned altitude — phase must be able to exit once there.
        ac.Altitude = 5000;
        for (int i = 0; i < 30; i++)
        {
            if (phase.OnTick(ctx))
            {
                return;
            }

            FlightPhysics.Update(ac, 1.0, _ => null);
        }

        Assert.Fail("InitialClimbPhase should complete after DCT releases the RV SID hold and assigned altitude is reached.");
    }
}
