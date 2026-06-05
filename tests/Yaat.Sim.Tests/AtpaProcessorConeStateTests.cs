using Xunit;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

/// <summary>
/// Covers <see cref="AtpaProcessor"/> automatic-cone behavior (GitHub issue #189): the in-trail
/// cone state (Monitor / Warning / Alert) and the per-TCP adaptation lists. The vNAS facility config
/// adapts each TCP with <c>AtpaConeType</c> = <c>Alert</c> or <c>AlertAndMonitor</c> (e.g. ZHU IAH
/// 8L/8R use AlertAndMonitor); the processor must translate those into the monitor/alert TCP code
/// lists CRC consumes, and pick a cone state from the live in-trail separation and closure.
/// </summary>
public class AtpaProcessorConeStateTests
{
    private const string TcpUlidApp = "ulid-APP";

    public AtpaProcessorConeStateTests() => TestVnasData.EnsureInitialized();

    // Threshold at (37.0, -122.0); approach course due north (000). Aircraft on final sit SOUTH of the
    // threshold (1 nm latitude ~= 1/60 deg) and track north toward it, so they read as inside +
    // established on approach. distanceOnFinalNm is the along-final distance out from the threshold.
    private static AircraftState MakeApproachAircraft(string callsign, double distanceOnFinalNm, double ias)
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = new LatLon(37.0 - (distanceOnFinalNm / 60.0), -122.0),
            TrueHeading = new TrueHeading(0),
            TrueTrack = new TrueHeading(0),
            Altitude = 3000,
            IndicatedAirspeed = ias,
            VerticalSpeed = 0,
            IsOnGround = false,
        };
        ac.Track.Owner = TrackOwner.CreateStars("NCT_APP", "NCT", 4, "A");
        return ac;
    }

    private static AtpaVolumeConfig MakeVolume(string coneType) =>
        new()
        {
            Id = "vol-1",
            // No AirportId -> no runway resolves, so the configured heading is treated as true and the
            // aircraft below sit on the centerline. The true-heading resolution itself is covered by
            // AtpaVolumeGeometryHeadingTests.
            AirportId = "",
            RunwayThreshold = new TowerLocationConfig { Lat = 37.0, Lon = -122.0 },
            MagneticHeading = 0,
            MaximumHeadingDeviation = 90,
            Floor = 0,
            Ceiling = 200,
            Length = 30,
            WidthLeft = 4000,
            WidthRight = 4000,
            TwoPointFiveApproachEnabled = false,
            Tcps =
            [
                new AtpaVolumeTcpConfig
                {
                    Id = "vt-1",
                    TcpId = TcpUlidApp,
                    ConeType = coneType,
                },
            ],
        };

    private static StarsConfig MakeStarsConfig() =>
        new()
        {
            Tcps =
            [
                new TcpConfig
                {
                    Id = TcpUlidApp,
                    Subset = 4,
                    SectorId = "A",
                },
            ],
        };

    [Fact]
    public void Process_AlertAndMonitorTcp_PopulatesMonitorAndAlertTcps()
    {
        // Lead 3 nm out, trailing 6 nm out: well separated, so the trailing aircraft gets a Monitor
        // cone. The TCP is adapted AlertAndMonitor, so it must appear in BOTH the monitor and alert
        // code lists (regression for matching the legacy "Monitor"/"Alert" strings that never exist).
        List<AircraftState> snapshot = [MakeApproachAircraft("SWA101", 3, 140), MakeApproachAircraft("UAL202", 6, 140)];

        var results = new AtpaProcessor().Process(snapshot, [MakeVolume("AlertAndMonitor")], MakeStarsConfig());

        Assert.True(results.ContainsKey("UAL202"));
        var trailing = results["UAL202"];
        Assert.Equal("CALLSIGNSWA101", trailing.TargetTrackId);
        Assert.Contains("4A", trailing.AtpaMonitorTcps);
        Assert.Contains("4A", trailing.AtpaAlertTcps);
    }

    [Fact]
    public void Process_AlertOnlyTcp_PopulatesAlertButNotMonitor()
    {
        List<AircraftState> snapshot = [MakeApproachAircraft("SWA101", 3, 140), MakeApproachAircraft("UAL202", 6, 140)];

        var results = new AtpaProcessor().Process(snapshot, [MakeVolume("Alert")], MakeStarsConfig());

        var trailing = results["UAL202"];
        Assert.Contains("4A", trailing.AtpaAlertTcps);
        Assert.DoesNotContain("4A", trailing.AtpaMonitorTcps);
    }

    [Theory]
    // actual <= allowed -> already violated -> Alert (regardless of closure)
    [InlineData(2.0, 3.0, 100.0, AtpaConeState.Alert)]
    [InlineData(3.0, 3.0, 0.0, AtpaConeState.Alert)]
    // predicted to violate within 24 s -> Alert. (4-3)/(200/3600) = 18 s
    [InlineData(4.0, 3.0, 200.0, AtpaConeState.Alert)]
    // predicted within 45 s but not 24 s -> Warning. (4-3)/(102.857/3600) ~= 35 s
    [InlineData(4.0, 3.0, 102.857, AtpaConeState.Warning)]
    // predicted beyond 45 s -> Monitor. (4-3)/(50/3600) = 72 s
    [InlineData(4.0, 3.0, 50.0, AtpaConeState.Monitor)]
    // not closing (or diverging) and currently separated -> Monitor
    [InlineData(4.0, 3.0, 0.0, AtpaConeState.Monitor)]
    [InlineData(4.0, 3.0, -50.0, AtpaConeState.Monitor)]
    public void DetermineConeState_MatchesStarsThresholds(double actualNm, double allowedNm, double closureKt, AtpaConeState expected)
    {
        Assert.Equal(expected, AtpaProcessor.DetermineConeState(actualNm, allowedNm, closureKt));
    }

    [Fact]
    public void Process_WellSeparatedPair_YieldsMonitorState()
    {
        // 3 nm vs 8 nm, equal speed: ~5 nm apart, not closing -> Monitor.
        List<AircraftState> snapshot = [MakeApproachAircraft("SWA101", 3, 140), MakeApproachAircraft("UAL202", 8, 140)];

        var results = new AtpaProcessor().Process(snapshot, [MakeVolume("AlertAndMonitor")], MakeStarsConfig());

        Assert.Equal(AtpaConeState.Monitor, results["UAL202"].ConeState);
    }

    [Fact]
    public void Process_OverlappingPair_YieldsAlertState()
    {
        // 3 nm vs 5 nm = 2 nm apart, below the 3.0 nm radar floor (no wake for B738/B738) -> Alert.
        List<AircraftState> snapshot = [MakeApproachAircraft("SWA101", 3, 140), MakeApproachAircraft("UAL202", 5, 140)];

        var results = new AtpaProcessor().Process(snapshot, [MakeVolume("AlertAndMonitor")], MakeStarsConfig());

        Assert.Equal(AtpaConeState.Alert, results["UAL202"].ConeState);
    }
}
