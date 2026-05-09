using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// CI guardrail for <c>src/Yaat.Sim/Data/AircraftProfileSiblings.json</c>: every
/// sibling listed must resolve to a real profile entry. Catches stale pairs after
/// a profile is removed or renamed in <c>AircraftProfiles.json</c>.
/// </summary>
public class AircraftSiblingMapTests(ITestOutputHelper output)
{
    [Fact]
    public void EverySiblingResolvesToARealProfile()
    {
        TestVnasData.EnsureInitialized();
        if (!AircraftSiblingMap.IsInitialized)
        {
            output.WriteLine("AircraftSiblingMap not initialized; skipping (data file missing)");
            return;
        }

        var siblingPath = Path.Combine(AppContext.BaseDirectory, "Data", "AircraftProfileSiblings.json");
        if (!File.Exists(siblingPath))
        {
            output.WriteLine($"Sibling map not at {siblingPath}; skipping");
            return;
        }

        var raw = AircraftSiblingMap.LoadFromFile(siblingPath);
        var problems = new List<string>();
        foreach (var (missing, sibling) in raw)
        {
            if (AircraftProfileDatabase.Get(sibling) is null)
            {
                problems.Add($"{missing} -> {sibling}: sibling has no profile");
            }
        }

        if (problems.Count > 0)
        {
            Assert.Fail("Stale sibling-map entries:\n  - " + string.Join("\n  - ", problems));
        }

        output.WriteLine($"All {raw.Count} sibling-map entries resolve to real profiles.");
    }

    [Fact]
    public void Pa28AliasResolvesToP28A()
    {
        TestVnasData.EnsureInitialized();
        Assert.True(AircraftSiblingMap.TryResolve("PA28", out var sibling));
        Assert.Equal("P28A", sibling);

        // Round-trip: the alias must produce a piston-realistic profile, not jet defaults.
        var profile = AircraftProfileDatabase.Get("PA28");
        Assert.NotNull(profile);
        Assert.True(profile!.IsProp, "PA28 alias should resolve to a propeller profile");

        // FAA ACD lookup also falls back via sibling
        var acd = FaaAircraftDatabase.Get("PA28");
        Assert.NotNull(acd);
    }

    [Fact]
    public void HeavyJetWidebodyAliasesResolve()
    {
        TestVnasData.EnsureInitialized();

        // Spot-check the modern-fleet codes we expect the sibling map to cover.
        foreach (var modern in new[] { "B789", "B77W", "A359", "B748", "A21N", "B38M" })
        {
            Assert.True(
                AircraftSiblingMap.TryResolve(modern, out var sib),
                $"{modern} should have a sibling registered (popular modern type missing from AircraftProfiles.json)"
            );
            Assert.NotNull(AircraftProfileDatabase.Get(sib));
        }
    }
}
