using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests;

/// <summary>
/// A rejected command must leave the aircraft's pending work untouched. <c>docs/command-pipeline.md</c> §5.2 states
/// the contract directly: "the user gets the error and state is unchanged".
///
/// The real dispatch path clears conflicting queue blocks and every deferred dispatch <em>before</em> applying the
/// first block, so any handler-level rejection that escapes <c>DryRunValidate</c> destroys unrelated controller work
/// on its way out. <c>DCT</c> escapes because the dry run runs with DCT-fix validation disabled.
/// </summary>
[Collection("NavDbMutator")]
public sealed class RejectedDctPreservesStateTests
{
    public RejectedDctPreservesStateTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState MakeAircraft()
    {
        return new AircraftState
        {
            Callsign = "UAL123",
            AircraftType = "B738",
            Position = new LatLon(37.62, -122.38),
            TrueHeading = new TrueHeading(280),
            Altitude = 10000,
            FlightPlan = new AircraftFlightPlan { Route = "SUNOL MODESTO OXNARD", Destination = "OAK" },
        };
    }

    [Fact]
    public void RejectedDct_LeavesQueuedConditionalAndDeferredDispatchIntact()
    {
        var aircraft = MakeAircraft();
        var navDb = TestNavDbFactory.WithFixes(("RANDOM", 37.0, -121.0));
        using var _ = NavigationDatabase.ScopedOverride(navDb);
        var ctx = TestDispatch.Context(Random.Shared);

        var conditional = CommandParser.ParseCompound("AT 6000 CM 120");
        Assert.True(conditional.IsSuccess, conditional.Reason);
        CommandDispatcher.DispatchCompound(conditional.Value!, aircraft, ctx);

        var deferredPayload = CommandParser.ParseCompound("FH 090");
        Assert.True(deferredPayload.IsSuccess, deferredPayload.Reason);
        aircraft.DeferredDispatches.Add(new DeferredDispatch(120, deferredPayload.Value!));

        // Preconditions: assert the state under test actually exists, so a setup change can never turn this
        // into a vacuous pass.
        Assert.NotEmpty(aircraft.Queue.Blocks);
        Assert.NotEmpty(aircraft.DeferredDispatches);
        int queuedBefore = aircraft.Queue.Blocks.Count;

        var dct = CommandParser.ParseCompound("DCT RANDOM");
        Assert.True(dct.IsSuccess, dct.Reason);
        var result = CommandDispatcher.DispatchCompound(dct.Value!, aircraft, ctx);

        Assert.False(result.Success);
        Assert.Contains("not programmed", result.Message);

        Assert.Equal(queuedBefore, aircraft.Queue.Blocks.Count);
        Assert.NotEmpty(aircraft.DeferredDispatches);
    }

    /// <summary>
    /// Guards the obvious-but-wrong fix. <c>DryRunValidate</c> validates against a snapshot clone, and
    /// <see cref="ApproachClearance.Procedure"/> is serialized by neither <c>ToSnapshot</c> nor <c>FromSnapshot</c> —
    /// so the clone's <c>GetProgrammedFixes</c> sees a strictly smaller set than the real aircraft whenever an
    /// approach is already active. Simply enabling DCT validation on the clone would therefore start rejecting
    /// direct-to clearances onto the active approach's own fixes.
    /// </summary>
    [Fact]
    public void DctToFixOnAlreadyActiveApproach_IsAccepted()
    {
        var aircraft = MakeAircraft();
        aircraft.Phases = new PhaseList
        {
            ActiveApproach = new ApproachClearance
            {
                ApproachId = "I28R",
                AirportCode = "OAK",
                RunwayId = "28R",
                FinalApproachCourse = new TrueHeading(280),
                Procedure = MakeApproachProcedure(),
            },
        };

        var navDb = TestNavDbFactory.WithFixes(("BERYL", 37.6, -122.1));
        using var _ = NavigationDatabase.ScopedOverride(navDb);
        var ctx = TestDispatch.Context(Random.Shared);

        // Precondition: BERYL is reachable only via the active approach procedure, never via the filed route.
        Assert.DoesNotContain("BERYL", aircraft.FlightPlan.Route);
        Assert.Contains("BERYL", aircraft.GetProgrammedFixes());

        var dct = CommandParser.ParseCompound("DCT BERYL");
        Assert.True(dct.IsSuccess, dct.Reason);
        var result = CommandDispatcher.DispatchCompound(dct.Value!, aircraft, ctx);

        Assert.True(result.Success, result.Message);
    }

    private static CifpApproachProcedure MakeApproachProcedure()
    {
        return new CifpApproachProcedure(
            "OAK",
            "I28R",
            'I',
            "ILS",
            "28R",
            [
                new CifpLeg("GROVE", CifpPathTerminator.IF, null, null, null, CifpFixRole.IAF, 10, null, null, null),
                new CifpLeg("BERYL", CifpPathTerminator.TF, null, null, null, CifpFixRole.FAF, 30, null, null, null),
                new CifpLeg("RW28R", CifpPathTerminator.TF, null, null, null, CifpFixRole.MAP, 40, null, null, null),
            ],
            new Dictionary<string, CifpTransition>(),
            [],
            false,
            null
        );
    }
}
