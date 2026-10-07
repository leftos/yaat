using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// SAYEXIT is a read-only query (it emits a terminal line and changes nothing), but unlike the other SAY*
/// queries it is dispatched through the aviation arm, not <c>RecordedCommandClassifier.IsSayQuery</c>. It was
/// omitted from the broad <c>CommandDescriber.IsPhaseTransparent</c> list, so a lone SAYEXIT fell through to
/// normal dispatch where its <c>None</c> fired dimension trips the clear-everything fast path in
/// <c>ClearConflictingBlocks</c> — wiping the entire pending queue on a query.
/// </summary>
public class SayExitFixEstimateTransparencyTests
{
    public SayExitFixEstimateTransparencyTests() => TestVnasData.EnsureInitialized();

    private static AircraftState Airborne() =>
        new()
        {
            Callsign = "N929AW",
            AircraftType = "BE33",
            Position = new LatLon(37.7, -122.2),
            TrueHeading = new TrueHeading(090),
            TrueTrack = new TrueHeading(090),
            Altitude = 3000,
            IndicatedAirspeed = 120,
            IsOnGround = false,
        };

    private static void Dispatch(string text, AircraftState ac)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"parse failed for '{text}': {parsed.Reason}");
        CommandResult result = CommandDispatcher.DispatchCompound(parsed.Value!, ac, TestDispatch.Context(Random.Shared, validateDctFixes: false));
        Assert.True(result.Success, $"dispatch failed for '{text}': {result.Message}");
    }

    private static bool StillQueued(AircraftState ac, CanonicalCommandType type) =>
        ac.Queue.Blocks.Any(b =>
            !b.IsApplied && (b.ParsedCommands ?? []).Any(c => c is not UnsupportedCommand && CommandDescriber.ToCanonicalType(c) == type)
        );

    [Fact]
    public void SayExitFixEstimate_DoesNotWipeTheQueue()
    {
        AircraftState ac = Airborne();
        Dispatch("AT 5000 SPD 150", ac);
        Assert.True(StillQueued(ac, CanonicalCommandType.Speed), "setup: the queued speed should be waiting behind its AT trigger");

        Dispatch("SAYEXIT", ac);

        Assert.True(StillQueued(ac, CanonicalCommandType.Speed), "a read-only SAYEXIT query must not wipe the queued speed");
    }
}
