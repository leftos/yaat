using Xunit;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

/// <summary>
/// Covers <see cref="AtpaProcessor"/> volume TCP exclusion. An ATPA volume can name TCP ULIDs
/// in <c>ExcludedTcpIds</c>; aircraft owned by an excluded TCP must be dropped from the volume's
/// in-trail pairing set. Regression guard for the bug where exclusion silently did nothing.
/// </summary>
public class AtpaProcessorExclusionTests
{
    private const string TcpUlid4A = "ulid-4A";
    private const string TcpUlid9Z = "ulid-9Z";

    public AtpaProcessorExclusionTests() => TestVnasData.EnsureInitialized();

    private static AircraftState MakeApproachAircraft(string callsign, double distanceOnFinalNm, TrackOwner owner)
    {
        // Threshold at (37.0, -122.0); approach course is due north (000). Aircraft established on the
        // final sit SOUTH of the threshold (the volume extends back up the final, opposite the landing
        // direction) and track north toward it, so they read as "inside" and "established on approach".
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = new LatLon(37.0 - (distanceOnFinalNm / 60.0), -122.0),
            TrueHeading = new TrueHeading(0),
            TrueTrack = new TrueHeading(0),
            Altitude = 3000,
            IndicatedAirspeed = 150,
            VerticalSpeed = 0,
            IsOnGround = false,
        };
        ac.Track.Owner = owner;
        return ac;
    }

    private static AtpaVolumeConfig MakeVolume(List<string> excludedTcpIds) =>
        new()
        {
            Id = "vol-1",
            RunwayThreshold = new TowerLocationConfig { Lat = 37.0, Lon = -122.0 },
            MagneticHeading = 0,
            MaximumHeadingDeviation = 30,
            Floor = 0,
            Ceiling = 200,
            Length = 10,
            WidthLeft = 6076,
            WidthRight = 6076,
            ExcludedTcpIds = excludedTcpIds,
        };

    private static StarsConfig MakeStarsConfig() =>
        new()
        {
            Tcps =
            [
                new TcpConfig
                {
                    Id = TcpUlid4A,
                    Subset = 4,
                    SectorId = "A",
                },
                new TcpConfig
                {
                    Id = TcpUlid9Z,
                    Subset = 9,
                    SectorId = "Z",
                },
            ],
        };

    [Fact]
    public void Process_DropsAircraftOwnedByExcludedTcp()
    {
        var owner4A = TrackOwner.CreateStars("NCT_APP", "NCT", 4, "A");
        List<AircraftState> snapshot = [MakeApproachAircraft("SWA101", 4, owner4A), MakeApproachAircraft("UAL202", 6, owner4A)];
        var volume = MakeVolume([TcpUlid4A]);

        var results = new AtpaProcessor().Process(snapshot, [volume], MakeStarsConfig());

        // Both aircraft are owned by the excluded TCP 4A — the volume drops to fewer than two
        // eligible aircraft, so no in-trail pairing (and no result) is produced.
        Assert.Empty(results);
    }

    [Fact]
    public void Process_KeepsAircraftWhenExcludedTcpDoesNotMatchOwner()
    {
        var owner4A = TrackOwner.CreateStars("NCT_APP", "NCT", 4, "A");
        List<AircraftState> snapshot = [MakeApproachAircraft("SWA101", 4, owner4A), MakeApproachAircraft("UAL202", 6, owner4A)];
        // Exclude a different TCP (9Z) that neither aircraft is owned by.
        var volume = MakeVolume([TcpUlid9Z]);

        var results = new AtpaProcessor().Process(snapshot, [volume], MakeStarsConfig());

        // The trailing aircraft is paired against the lead — exclusion must not over-fire.
        Assert.True(results.ContainsKey("UAL202"));
    }

    [Fact]
    public void Process_RelinksTrailingAircraftAcrossExcludedNeighbor()
    {
        var owner4A = TrackOwner.CreateStars("NCT_APP", "NCT", 4, "A");
        var owner9Z = TrackOwner.CreateStars("ZOA_CTR", "NCT", 9, "Z");
        List<AircraftState> snapshot =
        [
            MakeApproachAircraft("SWA101", 3, owner4A), // lead
            MakeApproachAircraft("DAL150", 5, owner9Z), // excluded — dropped from the chain
            MakeApproachAircraft("UAL202", 7, owner4A), // trailing — must re-pair against the lead
        ];
        var volume = MakeVolume([TcpUlid9Z]);

        var results = new AtpaProcessor().Process(snapshot, [volume], MakeStarsConfig());

        Assert.False(results.ContainsKey("DAL150"));
        Assert.True(results.ContainsKey("UAL202"));
        Assert.Equal("CALLSIGNSWA101", results["UAL202"].TargetTrackId);
    }
}
