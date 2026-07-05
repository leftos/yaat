using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// <see cref="SimulationEngine.AmendFlightPlan"/> beacon-code semantics: amending the assigned
/// beacon code (e.g. from the Flight Plan Editor's beacon field, or a DA/VP create with an explicit
/// code) changes only <c>Transponder.AssignedCode</c> — the code the flight plan expects — and never
/// <c>Transponder.Code</c>, the code the aircraft actually transmits. A controller cannot reach into
/// the cockpit and change the transponder; the pilot keeps squawking the current code until told to
/// squawk the new one (and the resulting beacon mismatch is shown on the data block until they do).
/// This mirrors the auto-assign-on-filing path, which already leaves <c>Code</c> untouched.
/// </summary>
public class AmendFlightPlanBeaconTests(ITestOutputHelper output)
{
    private SimulationEngine BuildEngine()
    {
        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    [Fact]
    public void ExplicitBeaconAmend_SetsAssignedCodeOnly_LeavesSquawkUntouched()
    {
        var engine = BuildEngine();
        var ac = new AircraftState
        {
            Callsign = "N342T",
            AircraftType = "DA42",
            Transponder = new AircraftTransponder
            {
                Code = 1200,
                AssignedCode = 1200,
                Mode = "C",
            },
            FlightPlan = new AircraftFlightPlan { FlightRules = "VFR", HasFlightPlan = true },
        };
        engine.World.AddAircraft(ac);

        engine.AmendFlightPlan(
            "N342T",
            new FlightPlanAmendment(
                AircraftType: null,
                EquipmentSuffix: null,
                Departure: null,
                Destination: null,
                CruiseSpeed: null,
                Altitude: null,
                FlightRules: null,
                Route: null,
                Remarks: null,
                Scratchpad1: null,
                Scratchpad2: null,
                BeaconCode: 4304
            )
        );

        var amended = engine.FindAircraft("N342T");
        Assert.NotNull(amended);
        Assert.Equal(4304u, amended.Transponder.AssignedCode);
        Assert.Equal(1200u, amended.Transponder.Code);
    }
}
