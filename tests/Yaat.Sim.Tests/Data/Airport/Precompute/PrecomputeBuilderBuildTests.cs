using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Tests.Helpers;
using static Yaat.Sim.Tests.Data.Airport.Precompute.PrecomputeBuilderFixtures;

namespace Yaat.Sim.Tests.Data.Airport.Precompute;

/// <summary>
/// A <see cref="PrecomputeEntryBuild.Build"/> of the real KOAK GeoJSON writes an entry keyed as computed now.
/// </summary>
public class PrecomputeBuilderBuildTests
{
    public PrecomputeBuilderBuildTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Build_RealKoakGeoJson_WritesEntryWhoseKeyIsCurrent()
    {
        string root = NewRoot();
        try
        {
            var store = new PrecomputeStore(root);

            PrecomputeEntry built = PrecomputeEntryBuild.Build(store, "OAK", OakGeoJson.Value, Context(PushTargetPlannerTests.Sequential), Gate26);

            string md5 = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(OakGeoJson.Value))).ToLowerInvariant();
            var current = PrecomputeKey.Current(
                md5,
                NavDataSerial,
                AirportSidecarHash.For(ArtccsDir, "OAK"),
                AirportSidecarHash.ForStandLeadIns(ArtccsDir, "OAK"),
                string.Empty
            );
            Assert.Equal(current, built.Key);
            Assert.Equal("OAK", built.AirportId);
            Assert.True(File.Exists(Path.Combine(root, "OAK.json.br")));
            Assert.True(store.TryRead("OAK", out PrecomputeEntry? read));
            Assert.Equal(current, read.Key);
            Assert.True(read.Key.PushTargetsMatch(current));
            Assert.Contains(read.PushTargets, e => (e.StandName == "26") && (e.Targets.Count > 0));
            Assert.Equal(PushTargetPlannerTests.Json(built.PushTargets), PushTargetPlannerTests.Json(read.PushTargets));
        }
        finally
        {
            DeleteRoot(root);
        }
    }
}
