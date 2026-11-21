using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// <c>@{prefix}*</c> names any free stand whose name starts with the prefix: <c>TAXI … @SIG*</c> and <c>TAXIAUTO @SIG*</c> resolve,
/// when applied, to one free stand of that name that fits the type, drawn the same way for the same callsign and traffic, and from
/// then on the clearance is exactly the typed <c>@{stand}</c>. Stands other aircraft hold and the airport's sidecar
/// <c>nonGateParking</c> names are never picked; with no free stand left the clearance is refused, and a bare <c>@*</c> is a usage
/// error. The exact <c>@{stand}</c> and <c>@GATE</c> are unchanged.
/// </summary>
public class TaxiToStandPrefixTests(ITestOutputHelper output)
{
    private const string Callsign = "N172SP";
    private const string AircraftType = "C172";

    /// <summary>The type parked on the stands a test fills.</summary>
    private const string ParkedType = "C172";

    /// <summary>OAK's north-field spot the aircraft starts at, held in position.</summary>
    private const string OakSpot = "9";

    /// <summary>OAK's stands named <c>SIG*</c> (the Signature ramp on the north field).</summary>
    private static readonly string[] OakSigStands = ["SIG1", "SIG2", "SIG3", "SIG4", "SIG5", "SIG6", "SIG7"];

    /// <summary>SMF's real stands named <c>F*</c>; its sidecar lists the third <c>F*</c> parking name, <c>FAA</c>, as non-gate.</summary>
    private static readonly string[] SmfFStands = ["F1", "F2"];

    /// <summary>The SMF stand the aircraft starts beside, held in position.</summary>
    private const string SmfStartStand = "B5";

    private const string TaxiAllPrefixMessage = "TAXIALL takes a stand name, not a prefix (@B*); use TAXIAUTO @B* per aircraft";

    /// <summary>The airport, stand-name prefix and wide-body type of the type-fit case.</summary>
    private const string WideBodyAirport = "SFO";

    private const string WideBodyPrefix = "G";
    private const string WideBodyType = "B77W";

    private const string UsageMessage = "@* needs a stand-name prefix: @B* taxis to a free stand whose name starts with B";

    [Fact]
    public void TaxiAuto_StandPrefix_PicksAFreeMatchingStand_TheSameOnARepeat()
    {
        if ((Build("OAK") is not { } first) || (Build("OAK") is not { } second))
        {
            return;
        }

        string picked = ClearedStand(first, "TAXIAUTO @SIG*");
        string again = ClearedStand(second, "TAXIAUTO @SIG*");

        Assert.Contains(picked, OakSigStands);
        Assert.Equal(picked, again);
    }

    [Fact]
    public void TaxiAuto_StandPrefix_ReadsBackAsTheTypedStand()
    {
        if ((Build("OAK") is not { } prefixGround) || (Build("OAK") is not { } typedGround))
        {
            return;
        }

        (CommandResult prefixResult, string? prefixReadback) = ClearWithReadback(prefixGround, "TAXIAUTO @SIG*");
        string picked = Assert.IsType<string>(StandOf(prefixGround));
        (CommandResult typedResult, string? typedReadback) = ClearWithReadback(typedGround, $"TAXIAUTO @{picked}");

        output.WriteLine($"prefix: {prefixResult.Message} | {prefixReadback}");
        output.WriteLine($"typed:  {typedResult.Message} | {typedReadback}");
        Assert.True(typedResult.Success, typedResult.Message);
        Assert.Equal(typedResult.Message, prefixResult.Message);
        Assert.NotNull(prefixReadback);
        Assert.Equal(typedReadback, prefixReadback);
        Assert.DoesNotContain("*", prefixReadback, StringComparison.Ordinal);
    }

    [Fact]
    public void Taxi_StandPrefix_ResolvesAndReadsBackAsTheTypedStand()
    {
        if ((Build("OAK") is not { } prefixGround) || (Build("OAK") is not { } typedGround))
        {
            return;
        }

        CommandResult prefixResult = Clear(prefixGround, "TAXI @SIG*");
        Assert.True(prefixResult.Success, prefixResult.Message);
        string picked = Assert.IsType<string>(StandOf(prefixGround));
        CommandResult typedResult = Clear(typedGround, $"TAXI @{picked}");

        output.WriteLine($"prefix: {prefixResult.Message}");
        output.WriteLine($"typed:  {typedResult.Message}");
        Assert.Contains(picked, OakSigStands);
        Assert.True(typedResult.Success, typedResult.Message);
        Assert.Equal(typedResult.Message, prefixResult.Message);
        HandlerReadback prefixReadback = Assert.IsType<HandlerReadback>(prefixResult.PilotReadback);
        HandlerReadback typedReadback = Assert.IsType<HandlerReadback>(typedResult.PilotReadback);
        Assert.Equal(typedReadback.Speech.Terminal, prefixReadback.Speech.Terminal);
    }

    [Fact]
    public void StandPrefix_TakenStands_AreNeverPicked()
    {
        if (Build("OAK") is not { } ground)
        {
            return;
        }

        string[] parkedOn = [.. OakSigStands.Where(s => s != "SIG2")];
        ParkOnEach(ground, parkedOn);

        string picked = ClearedStand(ground, "TAXIAUTO @SIG*");

        Assert.Equal("SIG2", picked);
    }

    [Fact]
    public void StandPrefix_EveryMatchingStandTaken_IsRefused()
    {
        if (Build("OAK") is not { } ground)
        {
            return;
        }

        ParkOnEach(ground, OakSigStands);
        AircraftState aircraft = SpawnAtOakSpot(ground);

        CommandResult result = ground.Engine.SendCommand(Callsign, "TAXIAUTO @sig*");

        AssertRefused(result, aircraft, "No free stand matching SIG* at this airport");
    }

    [Fact]
    public void StandPrefix_NonGateParkingName_IsNeverPicked()
    {
        if ((Build("SMF") is not { } ground) || (Build("SMF") is not { } control))
        {
            return;
        }

        AircraftState controlAircraft = SpawnAtSmfStand(control);
        CommandResult controlResult = control.Engine.SendCommand(Callsign, "TAXIAUTO @F*");
        ParkOnEach(ground, SmfFStands);
        AircraftState aircraft = SpawnAtSmfStand(ground);
        CommandResult result = ground.Engine.SendCommand(Callsign, "TAXIAUTO @F*");

        Assert.True(controlResult.Success, controlResult.Message);
        Assert.Contains(Assert.IsType<TaxiRoute>(controlAircraft.Ground.AssignedTaxiRoute).DestinationParking, SmfFStands);
        AssertRefused(result, aircraft, "No free stand matching F* at this airport");
    }

    [Theory]
    [InlineData("TAXIAUTO @*")]
    [InlineData("TAXI A @*")]
    public void BarePrefixWildcard_IsAUsageError(string command)
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse(command);

        Assert.False(result.IsSuccess, command);
        // The parser wraps every verb's failure in the verb as typed and its usage ("TAXI" … — expected: TAXI <route...>).
        Assert.Contains(UsageMessage, result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ExactStand_IsUnchanged()
    {
        if (Build("OAK") is not { } ground)
        {
            return;
        }

        Assert.Equal("SIG3", ClearedStand(ground, "TAXIAUTO @SIG3"));
    }

    [Fact]
    public void RemarkGate_IsUnchanged()
    {
        if (Build("OAK") is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnAtOakSpot(ground);
        CommandResult result = ground.Engine.SendCommand(Callsign, "TAXIAUTO @GATE");

        AssertRefused(result, aircraft, $"No gate in {Callsign}'s flight plan remarks; name the stand with @");
    }

    [Theory]
    [InlineData("TAXIALL @SIG*")]
    [InlineData("TAXIALL @*")]
    public void TaxiAll_StandPrefix_IsRefused(string command)
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse(command);

        Assert.False(result.IsSuccess, command);
        Assert.Contains(TaxiAllPrefixMessage, result.Reason, StringComparison.Ordinal);
    }

    /// <summary>An aircraft parked on a matching stand moves to another one: its own stand is not in the pool.</summary>
    [Fact]
    public void StandPrefix_ParkedOnAMatchingStand_PicksAnotherStand()
    {
        if ((Build("OAK") is not { } fromSpot) || (Build("OAK") is not { } fromStand))
        {
            return;
        }

        string wouldPick = ClearedStand(fromSpot, "TAXIAUTO @SIG*");
        AircraftState aircraft = SfoGroundHarness.SpawnParked(fromStand, Callsign, AircraftType, wouldPick);
        aircraft.Ground.ParkingSpot = wouldPick;

        CommandResult result = fromStand.Engine.SendCommand(Callsign, "TAXIAUTO @SIG*");

        Assert.True(result.Success, result.Message);
        string picked = Assert.IsType<string>(StandOf(fromStand));
        Assert.Contains(picked, OakSigStands);
        Assert.NotEqual(wouldPick, picked);
    }

    [Fact]
    public void StandPrefix_MatchingNoStand_IsRefused()
    {
        if (Build("OAK") is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnAtOakSpot(ground);

        CommandResult result = ground.Engine.SendCommand(Callsign, "TAXIAUTO @ZZQ*");

        AssertRefused(result, aircraft, "No free stand matching ZZQ* at this airport");
    }

    [Fact]
    public void Taxi_StandPrefix_EveryMatchingStandTaken_IsRefused()
    {
        if (Build("OAK") is not { } ground)
        {
            return;
        }

        ParkOnEach(ground, OakSigStands);
        AircraftState aircraft = SpawnAtOakSpot(ground);

        CommandResult result = ground.Engine.SendCommand(Callsign, "TAXI A @SIG*");

        AssertRefused(result, aircraft, "No free stand matching SIG* at this airport");
    }

    /// <summary>
    /// A wide-body against a prefix whose stands mix sizes: the pool is the stands that fit it (<see cref="StandFit"/>), a proper
    /// part of the matching stands, and the pick is one of them.
    /// </summary>
    [Fact]
    public void StandPrefix_WideBody_PicksOnlyAStandThatFits()
    {
        if (Build(WideBodyAirport) is not { } ground)
        {
            return;
        }

        AirportGroundLayout layout = ground.Layout;
        double spanFt = AircraftFootprint.ResolveWingspanFt(WideBodyType);
        AirplaneDesignGroup ceiling = AirplaneDesignGroups.AirportCeiling(layout);
        (GroundNode Node, string Name)[] matching =
        [
            .. layout
                .Nodes.Values.Select(n => (Node: n, Name: n is { Type: GroundNodeType.Parking, Name: { } name } ? name.Trim() : ""))
                .Where(s => s.Name.StartsWith(WideBodyPrefix, StringComparison.OrdinalIgnoreCase)),
        ];
        string[] fitting = [.. matching.Where(s => spanFt <= StandFit.MaxWingspanFt(layout, s.Node, ceiling, null)).Select(s => s.Name)];
        output.WriteLine($"{WideBodyPrefix}*: {matching.Length} matching, {fitting.Length} fit {WideBodyType}: {string.Join(" ", fitting)}");
        Assert.NotEmpty(fitting);
        Assert.True(fitting.Length < matching.Length, "the prefix's stands do not mix sizes");

        ParkingAreaPool pool = Assert.IsType<ParkingAreaPool>(ArrivalParkingPicker.PrefixPool(WideBodyType, layout, WideBodyPrefix, null));
        string picked = Assert.IsType<string>(ArrivalParkingPicker.PickFree(Callsign, pool.Names, []));

        Assert.True(pool.Fits, "the pool fell back to every matching stand");
        Assert.All(pool.Names, n => Assert.Contains(n, fitting));
        Assert.Contains(picked, fitting);
    }

    /// <summary>An engine over the committed <paramref name="airport"/> layout, or null when navdata or the layout is missing.</summary>
    private SfoGround? Build(string airport)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout(airport) is not { } layout)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = $"test-{airport}-stand-prefix",
                ScenarioName = $"{airport} stand prefix",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = airport,
                AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
            },
        };
        return new SfoGround(engine, layout);
    }

    private static AircraftState SpawnAtOakSpot(SfoGround ground) => SfoGroundHarness.SpawnAtSpot(ground, Callsign, AircraftType, OakSpot);

    private static AircraftState SpawnAtSmfStand(SfoGround ground)
    {
        GroundNode stand = ground.Layout.FindParkingByName(SmfStartStand) ?? throw new InvalidOperationException($"SMF has no stand {SmfStartStand}");
        return SfoGroundHarness.SpawnAt(
            ground,
            Callsign,
            AircraftType,
            (stand, stand.TrueHeading ?? new TrueHeading(0)),
            new HoldingInPositionPhase()
        );
    }

    /// <summary>The aircraft spawned at the OAK spot and sent <paramref name="command"/>.</summary>
    private static CommandResult Clear(SfoGround ground, string command)
    {
        SpawnAtOakSpot(ground);
        return ground.Engine.SendCommand(Callsign, command);
    }

    /// <summary><see cref="Clear"/>, accepted; the stand its route ends at.</summary>
    private static string ClearedStand(SfoGround ground, string command)
    {
        CommandResult result = Clear(ground, command);
        Assert.True(result.Success, result.Message);
        return Assert.IsType<string>(StandOf(ground));
    }

    /// <summary><see cref="Clear"/>, with the pilot's readback of the command as applied.</summary>
    private static (CommandResult Result, string? Readback) ClearWithReadback(SfoGround ground, string command)
    {
        CommandResult result = Clear(ground, command);
        Assert.True(result.Success, result.Message);
        AircraftState aircraft = Assert.IsType<AircraftState>(ground.Engine.World.FindAircraft(Callsign));
        CompoundCommand compound = Assert.IsType<CompoundCommand>(CommandParser.ParseCompound(command).Value);
        PilotSpeechText? readback = PilotResponder.BuildReadbackAsApplied(
            compound,
            result,
            aircraft,
            PilotSpeechContext.Canonical,
            ActiveRunways.Empty
        );
        return (result, readback?.Terminal);
    }

    private static string? StandOf(SfoGround ground) => ground.Engine.World.FindAircraft(Callsign)?.Ground.AssignedTaxiRoute?.DestinationParking;

    private static void AssertRefused(CommandResult result, AircraftState aircraft, string expected)
    {
        Assert.False(result.Success, result.Message);
        Assert.Equal(expected, result.Message);
        Assert.True(result.IsOperatorGuidance, "the refusal must be operator guidance so no pilot unable is queued");
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
    }

    private static void ParkOnEach(SfoGround ground, string[] stands)
    {
        for (int i = 0; i < stands.Length; i++)
        {
            AircraftState parked = SfoGroundHarness.SpawnParked(ground, $"PRK{i + 1}", ParkedType, stands[i]);
            parked.Ground.ParkingSpot = stands[i];
        }
    }
}
