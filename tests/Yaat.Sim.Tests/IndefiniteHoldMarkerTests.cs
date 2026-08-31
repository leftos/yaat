using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// Pins <see cref="CommandDescriber.InstallsIndefiniteHoldPhase"/> to the curated
/// command → never-self-completing-phase map. A static "OnTick can never return true" proof is not
/// feasible, so the contract is convention: when adding a phase whose OnTick never returns true under
/// normal operation (or a command that installs one), extend BOTH the predicate and this map so
/// dispatch keeps warning about chains queued behind it.
/// </summary>
public class IndefiniteHoldMarkerTests
{
    /// <summary>Command type → the never-self-completing phase its handler installs.</summary>
    private static readonly Dictionary<string, string> ExpectedInstallers = new()
    {
        ["HoldingPatternCommand"] = "HoldingPatternPhase",
        ["HoldPresentPosition360Command"] = "VfrHoldPhase",
        ["HoldPresentPositionHoverCommand"] = "VfrHoldPhase",
        ["HoldAtFixOrbitCommand"] = "VfrHoldPhase",
        ["HoldAtFixHoverCommand"] = "VfrHoldPhase",
        ["FollowCommand"] = "VfrFollowPhase",
        // Deliberately absent: FollowGroundCommand — FollowingPhase self-completes into an
        // IsIdleAwaitingCommands phase on both exits, so "FOLLOWG X; CROSS 28R" chains fine and must
        // not warn (aviation review 2026-08-31); AerialRefuelingAnchorPhase (CAR anchor case) —
        // anchor-vs-track needs a military-route lookup unavailable at dispatch classification.
    };

    [Fact]
    public void Predicate_MatchesCuratedInstallerMap()
    {
        var actual = new HashSet<string>();
        foreach (var type in ParsedCommandDummyFactory.AllParsedCommandTypes)
        {
            var cmd = ParsedCommandDummyFactory.CreateDummy(type);
            if (cmd is not null && CommandDescriber.InstallsIndefiniteHoldPhase(cmd))
            {
                actual.Add(type.Name);
            }
        }

        Assert.Equal(ExpectedInstallers.Keys.OrderBy(n => n), actual.OrderBy(n => n));
    }

    [Fact]
    public void CuratedMap_PhaseTypesExist()
    {
        var phaseNames = typeof(Yaat.Sim.Phases.Phase)
            .Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(Yaat.Sim.Phases.Phase)))
            .Select(t => t.Name)
            .ToHashSet();

        foreach (var phase in ExpectedInstallers.Values.Distinct())
        {
            Assert.Contains(phase, phaseNames);
        }
    }
}
