using System.Text.Json;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Asdex;

/// <summary>
/// The real ZOA ARTCC config (<c>docs/vnas-artcc-config-examples/zoa.json</c>) the ASDE-X tests seed from, read once per
/// test process.
/// </summary>
public static class ZoaArtccConfig
{
    private static readonly Lazy<ArtccConfigRoot> Lazy = new(() =>
        JsonSerializer.Deserialize<ArtccConfigRoot>(
            File.ReadAllText(Path.Combine(TickRecorder.FindRepoRoot(), "docs", "vnas-artcc-config-examples", "zoa.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        )!
    );

    public static ArtccConfigRoot Full => Lazy.Value;
}
