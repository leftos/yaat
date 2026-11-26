using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A heading supersedes a pending JRADO join and the rest of the chain it heads, by every path a heading reaches the queue:
/// typed and applied at once, typed and held for the pilot's reaction delay, and fired from a condition. A separate
/// altitude, speed or crossing instruction leaves the join armed. Driven through the engine with the real navdata.
/// </summary>
[Collection("NavDbMutator")]
public class HeadingSupersedesJoinTests
{
    private readonly ITestOutputHelper _output;

    public HeadingSupersedesJoinTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void TypedHeading_NoDelay_DropsTheJoinAndTheDirect() => AssertHeadingDropsTheJoin(delaySeconds: 0);

    [Fact]
    public void TypedHeading_ReactionDelay_DropsTheJoinAndTheDirect() => AssertHeadingDropsTheJoin(delaySeconds: 3);

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void TypedHeading_DropsTheChainsClimb(int delaySeconds)
    {
        (SimulationEngine engine, AircraftState ac) = Build(delaySeconds);
        Send(engine, "JRADO SNS264; DCT SHOEY, CM 070");
        Fly(engine, 5 + delaySeconds);
        double? assignedBefore = ac.Targets.AssignedAltitude;
        Send(engine, "FH 360");
        Fly(engine, delaySeconds + 120);

        Assert.Equal(assignedBefore, ac.Targets.AssignedAltitude);
        Assert.DoesNotContain(ac.Queue.Blocks, b => !b.IsApplied);
        Assert.True(ac.Altitude < 3100, $"climbing to {ac.Altitude:F0} ft after FH 360 dropped the chain's CM 070");
    }

    [Fact]
    public void TypedHeading_DropsThePendingBlockWhole_ItsSpeedIncluded()
    {
        (SimulationEngine engine, AircraftState ac) = Build(delaySeconds: 0);
        Send(engine, "AT SNS FH 270, SPD 210");
        int chain = Assert.Single(ac.Queue.Blocks, b => !b.IsApplied).TransmissionId;
        Assert.Null(ac.Targets.AssignedSpeed);
        Send(engine, "FH 360");

        Assert.DoesNotContain(ac.Queue.Blocks, b => b.TransmissionId == chain);
        Fly(engine, 300);
        Assert.DoesNotContain(ac.Queue.Blocks, b => b.TransmissionId == chain);
        Assert.Null(ac.Targets.AssignedSpeed);
        Assert.Equal(0.0, ac.Targets.AssignedMagneticHeading?.Degrees % 360);
    }

    [Fact]
    public void TypedHeading_LeavesASeparateClimbAlone()
    {
        (SimulationEngine engine, AircraftState ac) = Build(delaySeconds: 0);
        Send(engine, "JRADO SNS264");
        Send(engine, "CM 070");
        Fly(engine, 5);
        Send(engine, "FH 360");

        Assert.Equal(7000, ac.Targets.AssignedAltitude);
        Assert.DoesNotContain(ac.Queue.Blocks, IsPendingJoin);
        Fly(engine, 60);
        Assert.True(ac.Altitude > 3500, $"at {ac.Altitude:F0} ft 60 s after FH 360: the separate CM 070 stopped");
    }

    [Fact]
    public void ConditionalHeading_AtFireTime_DropsTheJoinAndTheDirect()
    {
        (SimulationEngine engine, AircraftState ac) = Build(delaySeconds: 0);
        ac.Altitude = 4500;
        Send(engine, "CM 070");
        Send(engine, "JRADO SNS264; DCT SHOEY");
        Send(engine, "AT 5000 FH 090");

        int? firedAt = null;
        for (int t = 1; (t <= 120) && (firedAt is null); t++)
        {
            engine.TickOneSecond();
            firedAt = (ac.Targets.AssignedMagneticHeading?.Degrees == 90) ? t : null;
        }

        Assert.NotNull(firedAt);
        Assert.DoesNotContain(ac.Queue.Blocks, IsPendingJoin);
        Assert.DoesNotContain(ac.Queue.Blocks, b => !b.IsApplied && (b.Description.Contains("SHOEY", StringComparison.Ordinal)));
        for (int t = 1; t <= 120; t++)
        {
            engine.TickOneSecond();
            Assert.Equal(90.0, ac.Targets.AssignedMagneticHeading?.Degrees);
            Assert.False(InterceptApplied(ac), $"turned onto R-264 {t} s after the AT 5000 FH 090 fired");
            Assert.False(HasShoey(ac), $"DCT SHOEY applied {t} s after the AT 5000 FH 090 fired");
        }
    }

    /// <summary>
    /// A reaction-delayed heading keeps a conditional the controller typed whole, whatever its shape: a typed
    /// <c>AT SNS264 JRADO SNS264</c> stays queued with its trigger exactly as a typed <c>AT SNS264 FH 090</c> does.
    /// </summary>
    [Theory]
    [InlineData("AT SNS264 JRADO SNS264")]
    [InlineData("AT SNS264 FH 090")]
    public void ReactionDelayedHeading_KeepsATypedRadialConditionalWhole(string conditional)
    {
        const int DelaySeconds = 3;
        (SimulationEngine engine, AircraftState ac) = Build(DelaySeconds);
        Send(engine, conditional);
        Fly(engine, 5 + DelaySeconds);
        CommandBlock typed = Assert.Single(ac.Queue.Blocks, b => !b.IsApplied);
        Assert.Equal(BlockTriggerType.InterceptRadial, typed.Trigger?.Type);
        Send(engine, "FH 360");
        Assert.NotEmpty(ac.DeferredDispatches);
        Fly(engine, DelaySeconds + 1);

        Assert.Equal(0.0, ac.Targets.AssignedMagneticHeading?.Degrees % 360);
        Assert.Contains(typed, ac.Queue.Blocks);
        Assert.False(typed.IsApplied);
        Assert.Equal(BlockTriggerType.InterceptRadial, typed.Trigger?.Type);
    }

    /// <summary>
    /// A conditional heading that fires supersedes only another transmission's JRADO follow-on: a typed
    /// <c>AT SNS264 JRADO SNS264</c> is a conditional like any other, so it stays queued and no warning names it.
    /// </summary>
    [Fact]
    public void ConditionalHeadingFiring_LeavesATypedRadialConditionalAlone()
    {
        (SimulationEngine engine, AircraftState ac) = Build(delaySeconds: 0);
        var warnings = new List<string>();
        engine.WarningEmitted += (_, warning) => warnings.Add(warning);
        ac.Altitude = 4500;
        Send(engine, "CM 070");
        Send(engine, "AT SNS264 JRADO SNS264");
        CommandBlock typed = Assert.Single(ac.Queue.Blocks, IsPendingJoin);
        string typedText = !string.IsNullOrEmpty(typed.Description) ? typed.Description : typed.NaturalDescription;
        Assert.False(string.IsNullOrEmpty(typedText));
        Send(engine, "AT 5000 FH 090");

        int? firedAt = null;
        for (int t = 1; (t <= 120) && (firedAt is null); t++)
        {
            engine.TickOneSecond();
            firedAt = (ac.Targets.AssignedMagneticHeading?.Degrees == 90) ? t : null;
        }

        _output.WriteLine($"AT 5000 FH 090 fired after {firedAt} s; warnings: {string.Join(" | ", warnings)}");
        Assert.NotNull(firedAt);
        Assert.Contains(typed, ac.Queue.Blocks);
        Assert.False(typed.IsApplied);
        Assert.DoesNotContain(warnings, w => w.Contains(typedText, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("CM 070")]
    [InlineData("SPD 210")]
    [InlineData("CFIX SHOEY 070")]
    public void NonLateralInstruction_LeavesTheJoinArmed(string instruction)
    {
        (SimulationEngine engine, AircraftState ac) = Build(delaySeconds: 0);
        Send(engine, "JRADO SNS264; DCT SHOEY");
        Fly(engine, 5);
        Send(engine, instruction);

        Assert.Contains(ac.Queue.Blocks, IsPendingJoin);
        for (int t = 1; t <= 1800; t++)
        {
            engine.TickOneSecond();
            if (InterceptApplied(ac))
            {
                return;
            }
        }

        Assert.Fail($"the join never applied after {instruction}");
    }

    private void AssertHeadingDropsTheJoin(int delaySeconds)
    {
        (SimulationEngine engine, AircraftState ac) = Build(delaySeconds);
        Send(engine, "JRADO SNS264; DCT SHOEY");
        Fly(engine, 5 + delaySeconds);
        Send(engine, "FH 360");
        Assert.Equal(delaySeconds > 0, ac.DeferredDispatches.Count > 0);
        Fly(engine, delaySeconds + 1);

        Assert.DoesNotContain(ac.Queue.Blocks, IsPendingJoin);
        Assert.DoesNotContain(ac.Queue.Blocks, b => !b.IsApplied);
        LatLon sns = SnsPosition();
        double radial = NavigationCommandHandler.RadialToTrueBearing("SNS", sns.Lat, sns.Lon, 264).Degrees;
        double startSide = SignedOff(GeoMath.BearingTo(sns, ac.Position), radial);
        int? crossedAt = null;
        for (int t = 1; (t <= 1200) && ((crossedAt is null) || (t <= crossedAt + 120)); t++)
        {
            engine.TickOneSecond();
            Assert.Equal(0.0, ac.Targets.AssignedMagneticHeading?.Degrees % 360);
            Assert.False(InterceptApplied(ac) || HasShoey(ac), $"the join or DCT SHOEY applied {t} s after FH 360");
            bool crossed = Math.Sign(SignedOff(GeoMath.BearingTo(sns, ac.Position), radial)) != Math.Sign(startSide);
            crossedAt ??= crossed ? t : null;
        }

        Assert.True(crossedAt is not null, "the aircraft never crossed R-264 on FH 360");
        _output.WriteLine($"crossed R-264 {crossedAt} s after FH 360 applied");
    }

    private static (SimulationEngine Engine, AircraftState Aircraft) Build(int delaySeconds)
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "t",
                ScenarioName = "t",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
                CommandRunDelayMinSeconds = delaySeconds,
                CommandRunDelayMaxSeconds = delaySeconds,
            },
        };
        engine.World.ReactionDelayRng = new SerializableRandom(42);
        var ac = new AircraftState
        {
            Callsign = "EJA802",
            AircraftType = "B738",
            Position = GeoMath.ProjectPoint(SnsPosition(), new TrueHeading(240), 14),
            TrueHeading = new TrueHeading(293),
            TrueTrack = new TrueHeading(293),
            Altitude = 3000,
            IndicatedAirspeed = 180,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KMRY",
                Destination = "KSJC",
                FlightRules = "IFR",
            },
        };
        engine.World.AddAircraft(ac);
        return (engine, ac);
    }

    private static void Send(SimulationEngine engine, string command)
    {
        CommandResult result = engine.SendCommand("EJA802", command);
        Assert.True(result.Success, $"{command}: {result.Message}");
    }

    private static void Fly(SimulationEngine engine, int seconds)
    {
        for (int t = 0; t < seconds; t++)
        {
            engine.TickOneSecond();
        }
    }

    private static bool IsPendingJoin(CommandBlock block) => !block.IsApplied && (block.Trigger?.Type == BlockTriggerType.InterceptRadial);

    private static bool InterceptApplied(AircraftState ac) =>
        ac.Queue.Blocks.Any(b => (b.Trigger?.Type == BlockTriggerType.InterceptRadial) && b.IsApplied);

    private static bool HasShoey(AircraftState ac) => ac.Targets.NavigationRoute.Exists(n => n.Name == "SHOEY");

    private static LatLon SnsPosition()
    {
        (double lat, double lon) = NavigationDatabase.Instance.GetFixPosition("SNS")!.Value;
        return new LatLon(lat, lon);
    }

    /// <summary>The signed difference a - b, -180 to 180 degrees.</summary>
    private static double SignedOff(double a, double b) => ((((a - b) % 360) + 540) % 360) - 180;
}
