using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Yaat.Sim.Tests;

/// <summary>
/// Source-level convention guard: every ground motion phase under
/// <c>src/Yaat.Sim/Phases/Ground/</c> must reference <c>IsImmobile</c> so it
/// honours active HOLDPOSITION / GIVEWAY directives. Stationary-by-definition
/// phases (HoldingShortPhase, AtParkingPhase, etc.) are whitelisted because
/// they never command motion in the first place. Non-phase helpers in the same
/// folder (GroundNavigator, PathPrimitive, etc.) are also whitelisted.
///
/// Why source-level: the check is for "someone added a new motion phase but
/// forgot the IsImmobile guard". A reflection-based check can't observe
/// whether a method body uses a property without IL inspection, and an
/// empirical per-phase tick test misses any phase not enumerated. Reading the
/// source files is the most direct expression of the contract.
/// </summary>
public sealed class GroundPhaseConvention
{
    [Fact]
    public void EveryGroundMotionPhase_ReferencesIsImmobile()
    {
        var dir = LocateGroundPhasesDir();
        Assert.False(string.IsNullOrEmpty(dir), "Could not locate src/Yaat.Sim/Phases/Ground/ relative to test output dir");

        var stationaryWhitelist = new HashSet<string>(System.StringComparer.Ordinal)
        {
            "HoldingShortPhase.cs",
            "HoldingInPositionPhase.cs",
            "HoldingAfterExitPhase.cs",
            "HoldingAfterPushbackPhase.cs",
            "AtParkingPhase.cs",
        };

        var nonPhaseHelpers = new HashSet<string>(System.StringComparer.Ordinal)
        {
            "GroundNavigator.cs",
            "PathPrimitive.cs",
            "PathPrimitiveBuilder.cs",
        };

        var violations = new List<string>();
        foreach (var file in Directory.GetFiles(dir, "*.cs"))
        {
            var name = Path.GetFileName(file);
            if (stationaryWhitelist.Contains(name) || nonPhaseHelpers.Contains(name))
            {
                continue;
            }

            var source = File.ReadAllText(file);
            if (!source.Contains("IsImmobile", System.StringComparison.Ordinal))
            {
                violations.Add(name);
            }
        }

        Assert.True(
            violations.Count == 0,
            $"Ground motion phases missing IsImmobile guard: [{string.Join(", ", violations)}]. "
                + "Either wire `if (ctx.Aircraft.Ground.IsImmobile) {{ /* phase-specific stop */ return false; }}` "
                + "into the phase's OnTick, or add the phase to the stationary-whitelist in GroundPhaseConvention."
        );
    }

    private static string LocateGroundPhasesDir()
    {
        // Walk up from the test assembly's output dir looking for the source tree.
        // Works regardless of whether `dotnet test` cwd is the test project dir or
        // the output dir.
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Yaat.Sim", "Phases", "Ground");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        return string.Empty;
    }
}
