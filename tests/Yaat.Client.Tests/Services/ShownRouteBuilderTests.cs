using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Client.Tests.Services;

/// <summary>
/// Unit tests for <see cref="ShownRouteBuilder"/>: the server's fix list drawn verbatim, the heading tail of a vectored
/// aircraft with no route fixes, and the shown-path cache key over what it reads.
/// </summary>
public class ShownRouteBuilderTests
{
    [Fact]
    public void BuildPrimary_PureVectorAircraft_NoRoute_ReturnsTailFromAircraft()
    {
        var ac = new AircraftModel
        {
            Callsign = "UAL789",
            Position = new LatLon(37.6, -122.4),
            AssignedHeading = new MagneticHeading(250.0),
        };

        (List<DrawnWaypoint>? waypoints, VectorTail? tail) = ShownRouteBuilder.BuildPrimary(ac);

        Assert.Empty(waypoints);
        Assert.NotNull(tail);
        Assert.Equal(37.6, tail!.FromLat, 6);
        Assert.Equal(-122.4, tail.FromLon, 6);
        Assert.Equal(250.0, tail.HeadingMag, 3);
        Assert.Equal(ShownRouteBuilder.TailLengthNm, tail.LengthNm, 3);
    }

    [Fact]
    public void BuildPrimary_RouteHasFixes_NoAssignedHeading_NoTail()
    {
        var ac = new AircraftModel
        {
            Callsign = "UAL101",
            NavRouteFixes = [new NavRouteFixDto("ENROU", 38.0, -122.5, null, null), new NavRouteFixDto("WNDSR", 37.85, -122.30, null, null)],
        };

        (List<DrawnWaypoint> _, VectorTail? tail) = ShownRouteBuilder.BuildPrimary(ac);
        Assert.Null(tail);
    }

    /// <summary>With route fixes the polyline already shows where the aircraft is going, so an assigned heading adds no tail.</summary>
    [Fact]
    public void BuildPrimary_RouteHasFixes_WithAssignedHeading_NoTail()
    {
        var ac = new AircraftModel
        {
            Callsign = "UAL102",
            AssignedHeading = new MagneticHeading(250.0),
            NavRouteFixes = [new NavRouteFixDto("ENROU", 38.0, -122.5, null, null)],
        };

        (List<DrawnWaypoint> waypoints, VectorTail? tail) = ShownRouteBuilder.BuildPrimary(ac);

        Assert.Single(waypoints);
        Assert.Null(tail);
    }

    /// <summary>
    /// A STAR whose runway transition ends in a VM leg (KFAT ALTTA9, RW11L: 310° off ALTTA) draws no client tail: the
    /// server's route-end course shape carries that course now.
    /// </summary>
    [Fact]
    public void BuildPrimary_StarEndingInVm_DrawsNoClientTail()
    {
        Yaat.Sim.Testing.TestVnasData.EnsureInitialized();
        CifpStarProcedure star = Assert.IsType<CifpStarProcedure>(NavigationDatabase.Instance.GetStar("KFAT", "ALTTA9"));
        Assert.Equal(CifpPathTerminator.VM, star.RunwayTransitions["RW11L"].Legs[^1].PathTerminator);
        (double Lat, double Lon) altta =
            NavigationDatabase.Instance.GetFixPosition("ALTTA") ?? throw new InvalidOperationException("ALTTA not in navdata");
        var ac = new AircraftModel
        {
            Callsign = "UAL103",
            Destination = "KFAT",
            DestinationRunway = "11L",
            ActiveStarId = "ALTTA9",
            NavRouteFixes = [new NavRouteFixDto("ALTTA", altta.Lat, altta.Lon, null, null)],
        };

        (List<DrawnWaypoint> waypoints, VectorTail? tail) = ShownRouteBuilder.BuildPrimary(ac);

        Assert.Single(waypoints);
        Assert.Null(tail);
    }

    [Fact]
    public void BuildPrimary_DrawsServerPositionsVerbatim_WithArcVerticesAndRestrictionLabels()
    {
        // The overlay must draw the server-provided route positions directly — including synthetic
        // arc-densification vertices (empty name) that no navdb lookup could resolve — and carry each
        // fix's crossing-restriction tokens with their latency. No navDb resolution of names.
        var ac = new AircraftModel
        {
            Callsign = "UAL909",
            NavRouteFixes =
            [
                new NavRouteFixDto(
                    "SKUNK",
                    37.90,
                    -122.40,
                    ["≥6000  250"],
                    [
                        [Token("≥6000", latent: true), Token("250")],
                    ]
                ),
                new NavRouteFixDto("", 37.88, -122.36, null, null), // synthetic arc vertex
                new NavRouteFixDto("", 37.86, -122.33, null, null), // synthetic arc vertex
                new NavRouteFixDto(
                    "DOCKR",
                    37.84,
                    -122.30,
                    ["≤17000", "≥11000"],
                    [
                        [Token("≤17000")],
                        [Token("≥11000")],
                    ]
                ),
            ],
        };

        (List<DrawnWaypoint>? waypoints, VectorTail? tail) = ShownRouteBuilder.BuildPrimary(ac);

        Assert.Null(tail);
        Assert.Equal(4, waypoints.Count);

        // Positions passed through verbatim.
        Assert.Equal("SKUNK", waypoints[0].ResolvedName);
        Assert.Equal(37.90, waypoints[0].Lat, 6);
        Assert.Equal(-122.40, waypoints[0].Lon, 6);
        Assert.Equal(["~≥6000~ 250"], RenderTokens(waypoints[0]));

        // Arc vertices preserved as bare points (empty name, no restriction).
        Assert.Equal("", waypoints[1].ResolvedName);
        Assert.Null(waypoints[1].RestrictionTokens);
        Assert.Equal("", waypoints[2].ResolvedName);

        // Window restriction preserved as two lines.
        Assert.Equal("DOCKR", waypoints[3].ResolvedName);
        Assert.Equal(["≤17000", "≥11000"], RenderTokens(waypoints[3]));
    }

    /// <summary>A fix with label lines but no tokens (an older server's shape) draws no restriction label.</summary>
    [Fact]
    public void BuildPrimary_FixWithLinesButNoTokens_DrawsNoRestriction()
    {
        var ac = new AircraftModel { Callsign = "UAL910", NavRouteFixes = [new NavRouteFixDto("SKUNK", 37.90, -122.40, ["≥6000  250"], null)] };

        (List<DrawnWaypoint>? waypoints, VectorTail? _) = ShownRouteBuilder.BuildPrimary(ac);

        DrawnWaypoint skunk = Assert.Single(waypoints);
        Assert.Equal("SKUNK", skunk.ResolvedName);
        Assert.Null(skunk.RestrictionTokens);
    }

    /// <summary>
    /// The shown-path cache key moves when only a token's latency flips (same fix, same text), so the overlay redraws a
    /// restriction that became active or latent.
    /// </summary>
    [Fact]
    public void ShownPathFingerprint_ChangesWhenOnlyTokenLatencyFlips()
    {
        static AircraftModel Aircraft(bool latent) =>
            new()
            {
                Callsign = "UAL911",
                NavRouteFixes =
                [
                    new NavRouteFixDto(
                        "SKUNK",
                        37.90,
                        -122.40,
                        ["≥6000  250"],
                        [
                            [Token("≥6000", latent), Token("250")],
                        ]
                    ),
                ],
            };

        Assert.NotEqual(
            RadarViewModel.BuildShownPathFingerprint(Aircraft(latent: false)),
            RadarViewModel.BuildShownPathFingerprint(Aircraft(latent: true))
        );
        Assert.Equal(
            RadarViewModel.BuildShownPathFingerprint(Aircraft(latent: true)),
            RadarViewModel.BuildShownPathFingerprint(Aircraft(latent: true))
        );
    }

    /// <summary>
    /// The same for a speed token: a published speed turning latent under an ATC speed or DSR, with the altitude token and
    /// the route unchanged, moves the shown-path cache key.
    /// </summary>
    [Fact]
    public void ShownPathFingerprint_ChangesWhenOnlySpeedTokenLatencyFlips()
    {
        static AircraftModel Aircraft(bool speedLatent) =>
            new()
            {
                Callsign = "UAL911",
                NavRouteFixes =
                [
                    new NavRouteFixDto(
                        "SKUNK",
                        37.90,
                        -122.40,
                        ["≥6000  250"],
                        [
                            [Token("≥6000"), Token("250", speedLatent)],
                        ]
                    ),
                ],
            };

        Assert.NotEqual(
            RadarViewModel.BuildShownPathFingerprint(Aircraft(speedLatent: false)),
            RadarViewModel.BuildShownPathFingerprint(Aircraft(speedLatent: true))
        );
    }

    /// <summary>A new assigned heading moves the shown-path cache key, so a vectored aircraft's heading tail is rebuilt.</summary>
    [Fact]
    public void ShownPathFingerprint_ChangesWithTheAssignedHeading()
    {
        var ac = new AircraftModel { Callsign = "UAL912", AssignedHeading = new MagneticHeading(250.0) };
        string before = RadarViewModel.BuildShownPathFingerprint(ac);

        ac.AssignedHeading = new MagneticHeading(270.0);

        Assert.NotEqual(before, RadarViewModel.BuildShownPathFingerprint(ac));
    }

    private static NavRouteRestrictionToken Token(string text, bool latent = false) => new(text, latent);

    /// <summary>Renders a waypoint's token lines one string per line, tokens joined by a space, a latent token as "~text~".</summary>
    private static List<string> RenderTokens(DrawnWaypoint waypoint) =>
        [
            .. Assert
                .IsAssignableFrom<IReadOnlyList<IReadOnlyList<NavRouteRestrictionToken>>>(waypoint.RestrictionTokens)
                .Select(line => string.Join(" ", line.Select(t => t.Latent ? $"~{t.Text}~" : t.Text))),
        ];
}
