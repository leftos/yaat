using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

/// <summary>
/// P4.1/P4.2: E2E tests using real airport GeoJSON layouts.
/// Tests go through GroundCommandHandler with real OAK/SFO data.
/// Silently skip if yaat-server ArtccResources are not available.
/// </summary>
public class AirportE2ETests
{
    private static readonly ILogger Logger = new NullLogger<AirportE2ETests>();
    private const string ArtccResourcesDir = @"X:\dev\yaat-server\ArtccResources\ZOA\airports";

    private static AirportGroundLayout? LoadLayout(string airportId, string subdir)
    {
        string combined = Path.Combine(ArtccResourcesDir, $"{subdir}.geojson");
        if (File.Exists(combined))
        {
            return GeoJsonParser.Parse(airportId, File.ReadAllText(combined));
        }
        return null;
    }

    private static AircraftState MakeGroundAircraft(string departure = "OAK", double lat = 37.728, double lon = -122.218)
    {
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Latitude = lat,
            Longitude = lon,
            Heading = 280,
            Altitude = 6,
            GroundSpeed = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            Departure = departure,
        };
        ac.Phases = new PhaseList();
        ac.Phases.Add(new AtParkingPhase());
        ac.Phases.Start(MinCtx(ac));
        return ac;
    }

    private static PhaseContext MinCtx(AircraftState ac) => CommandDispatcher.BuildMinimalContext(ac, NullLogger.Instance);

    private static GroundNode? FindParking(AirportGroundLayout layout, string name) =>
        layout.Nodes.Values.FirstOrDefault(n =>
            (n.Type == GroundNodeType.Parking || n.Type == GroundNodeType.Spot) && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)
        );

    // -------------------------------------------------------------------------
    // P4.1: OAK E2E — real routes verified against oak.geojson graph
    //
    // OAK taxiway connectivity (verified):
    //   D connects to: C, G, H, J, K
    //   C connects to: A, B, C1, D, E, F, G, H, J
    //   B connects to: A, B1-B5, C, R, S, T, V, W
    //   W connects to: B, U, V, W1-W7
    //   K connects to: D, F, J, L
    //   F connects to: C, K, L
    //
    // NEW7 parking (lat 37.740, lon -122.221) connects via RAMP to D.
    // D crosses runway 15/33 (lat range 37.730-37.740).
    // Route to runway 30: D → C → B → W (W serves the 30/12 threshold area).
    // -------------------------------------------------------------------------

    [Fact]
    public void OAK_TaxiFromParking_D_Succeeds()
    {
        var layout = LoadLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        var parking = FindParking(layout, "NEW7");
        Assert.NotNull(parking);

        var ac = MakeGroundAircraft(lat: parking.Latitude, lon: parking.Longitude);

        // TAXI D — walks south along D from NEW7 parking (via RAMP edge)
        var taxi = new TaxiCommand(["D"], []);
        var result = GroundCommandHandler.TryTaxi(ac, taxi, layout, null, Logger);

        Assert.True(result.Success, $"Taxi should succeed: {result.Message}");
        Assert.NotNull(ac.AssignedTaxiRoute);

        // Route should start with RAMP (parking→taxiway) then have D segments
        Assert.Contains(ac.AssignedTaxiRoute!.Segments, s => string.Equals(s.TaxiwayName, "D", StringComparison.OrdinalIgnoreCase));
        Assert.IsType<TaxiingPhase>(ac.Phases!.CurrentPhase);
    }

    [Fact]
    public void OAK_TaxiFromParking_DC_ReachesC()
    {
        var layout = LoadLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        var parking = FindParking(layout, "NEW7");
        Assert.NotNull(parking);

        var ac = MakeGroundAircraft(lat: parking.Latitude, lon: parking.Longitude);

        // TAXI D C — D south to C junction
        var taxi = new TaxiCommand(["D", "C"], []);
        var result = GroundCommandHandler.TryTaxi(ac, taxi, layout, null, Logger);

        Assert.True(result.Success, $"Taxi should succeed: {result.Message}");

        Assert.Contains(ac.AssignedTaxiRoute!.Segments, s => string.Equals(s.TaxiwayName, "D", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ac.AssignedTaxiRoute.Segments, s => string.Equals(s.TaxiwayName, "C", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OAK_TaxiFromParking_DCBW_ToRunway30_HasHoldShortAndPhases()
    {
        var layout = LoadLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        var parking = FindParking(layout, "NEW7");
        Assert.NotNull(parking);

        var ac = MakeGroundAircraft(lat: parking.Latitude, lon: parking.Longitude);

        // TAXI D C B W to runway 30 — full route from NEW7 to runway 30
        var taxi = new TaxiCommand(["D", "C", "B", "W"], [], DestinationRunway: "30");
        var result = GroundCommandHandler.TryTaxi(ac, taxi, layout, null, Logger);

        Assert.True(result.Success, $"Taxi should succeed: {result.Message}");

        // Should now be in TaxiingPhase
        Assert.IsType<TaxiingPhase>(ac.Phases!.CurrentPhase);

        // Route should have D, C, B, and W segments
        Assert.Contains(ac.AssignedTaxiRoute!.Segments, s => string.Equals(s.TaxiwayName, "D", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ac.AssignedTaxiRoute.Segments, s => string.Equals(s.TaxiwayName, "B", StringComparison.OrdinalIgnoreCase));

        // Should end with a hold-short for runway 30 (destination)
        var destHs = ac.AssignedTaxiRoute.HoldShortPoints.Where(h => h.Reason == HoldShortReason.DestinationRunway).ToList();
        Assert.True(destHs.Count > 0, "Should have destination runway hold-short");
    }

    [Fact]
    public void OAK_PushbackFromParking_FacingD_Succeeds()
    {
        var layout = LoadLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        var parking = FindParking(layout, "NEW7");
        Assert.NotNull(parking);

        var ac = MakeGroundAircraft(lat: parking.Latitude, lon: parking.Longitude);

        var push = new PushbackCommand(FacingTaxiway: "D");
        var result = GroundCommandHandler.TryPushback(ac, push, layout, Logger);

        Assert.True(result.Success, $"Pushback should succeed: {result.Message}");
        Assert.IsType<PushbackPhase>(ac.Phases!.CurrentPhase);
    }

    [Fact]
    public void OAK_TaxiDF_MultipleHoldShorts_CrossesRunway15_33()
    {
        var layout = LoadLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        // Start from a D node north of the 15/33 crossing (lat > 37.735)
        var dEdges = layout.Edges.Where(e => string.Equals(e.TaxiwayName, "D", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(dEdges.Count > 0);

        GroundNode? startNode = null;
        foreach (var edge in dEdges)
        {
            var node = layout.Nodes[edge.FromNodeId];
            if (node.Latitude > 37.735)
            {
                startNode = node;
                break;
            }
        }

        if (startNode is null)
        {
            return;
        }

        var ac = MakeGroundAircraft(lat: startNode.Latitude, lon: startNode.Longitude);

        // D → K → F: D connects to K, K connects to F. Both D and F cross 15/33.
        var taxi = new TaxiCommand(["D", "K", "F"], []);
        var result = GroundCommandHandler.TryTaxi(ac, taxi, layout, null, Logger);

        Assert.True(result.Success, $"Taxi D K F should succeed: {result.Message}");

        // Should have hold-short(s) for runway 15/33 crossing
        var hsRwy = ac.AssignedTaxiRoute!.HoldShortPoints.Where(h => h.Reason == HoldShortReason.RunwayCrossing).ToList();
        Assert.True(hsRwy.Count > 0, "Should have runway crossing hold-short(s)");
    }

    [Fact]
    public void OAK_TaxiDKF_AutoCrossRunway_ClearsHoldShorts()
    {
        var layout = LoadLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        var dEdges = layout.Edges.Where(e => string.Equals(e.TaxiwayName, "D", StringComparison.OrdinalIgnoreCase)).ToList();

        GroundNode? startNode = null;
        foreach (var edge in dEdges)
        {
            var node = layout.Nodes[edge.FromNodeId];
            if (node.Latitude > 37.735)
            {
                startNode = node;
                break;
            }
        }

        if (startNode is null)
        {
            return;
        }

        var ac = MakeGroundAircraft(lat: startNode.Latitude, lon: startNode.Longitude);

        // Taxi with auto-cross-runway flag
        var taxi = new TaxiCommand(["D", "K", "F"], []);
        var result = GroundCommandHandler.TryTaxi(ac, taxi, layout, null, Logger, autoCrossRunway: true);

        Assert.True(result.Success, $"Taxi D K F (auto-cross) should succeed: {result.Message}");

        // All runway crossing hold-shorts should be pre-cleared
        var unclearedCrossings = ac.AssignedTaxiRoute!.HoldShortPoints.Where(h => h.Reason == HoldShortReason.RunwayCrossing && !h.IsCleared)
            .ToList();
        Assert.Empty(unclearedCrossings);
    }

    [Fact]
    public void OAK_FullTaxiToTakeoff_DCBW_HoldShort30_HasPhases()
    {
        var layout = LoadLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        var parking = FindParking(layout, "NEW7");
        Assert.NotNull(parking);

        var ac = MakeGroundAircraft(lat: parking.Latitude, lon: parking.Longitude);

        // Step 1: Taxi from parking to runway 30 via D C B W
        var taxi = new TaxiCommand(["D", "C", "B", "W"], [], DestinationRunway: "30");
        var taxiResult = GroundCommandHandler.TryTaxi(ac, taxi, layout, null, Logger);
        Assert.True(taxiResult.Success, $"Taxi failed: {taxiResult.Message}");

        // Verify we're in TaxiingPhase
        Assert.IsType<TaxiingPhase>(ac.Phases!.CurrentPhase);

        // Verify route has destination hold-short for runway 30
        var destHs = ac.AssignedTaxiRoute!.HoldShortPoints.FirstOrDefault(h => h.Reason == HoldShortReason.DestinationRunway);
        Assert.NotNull(destHs);

        // The destination hold-short target should reference runway 30
        Assert.NotNull(destHs.TargetName);
        Assert.True(
            RunwayIdentifier.Parse(destHs.TargetName!).Contains("30"),
            $"Hold-short target should be for runway 30, got: {destHs.TargetName}"
        );

        // HoldingShortPhase is inserted by TaxiingPhase at runtime when the
        // aircraft reaches the hold-short position — it's not in the phase list yet.
    }

    [Fact]
    public void OAK_PushbackThenTaxi_NEW7_PushD_TaxiDC()
    {
        var layout = LoadLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        var parking = FindParking(layout, "NEW7");
        Assert.NotNull(parking);

        var ac = MakeGroundAircraft(lat: parking.Latitude, lon: parking.Longitude);

        // Step 1: Pushback facing D
        var push = new PushbackCommand(FacingTaxiway: "D");
        var pushResult = GroundCommandHandler.TryPushback(ac, push, layout, Logger);
        Assert.True(pushResult.Success, $"Pushback failed: {pushResult.Message}");
        Assert.IsType<PushbackPhase>(ac.Phases!.CurrentPhase);

        // Step 2: Complete pushback by ticking until done
        for (int i = 0; i < 200; i++)
        {
            var ctx = MinCtx(ac);
            FlightPhysics.Update(ac, ctx.DeltaSeconds);
            PhaseRunner.Tick(ac, ctx);

            if (ac.Phases.CurrentPhase is not PushbackPhase)
            {
                break;
            }
        }

        // After pushback, issue taxi via D C
        var taxi = new TaxiCommand(["D", "C"], []);
        var taxiResult = GroundCommandHandler.TryTaxi(ac, taxi, layout, null, Logger);
        Assert.True(taxiResult.Success, $"Taxi after pushback failed: {taxiResult.Message}");
        Assert.IsType<TaxiingPhase>(ac.Phases.CurrentPhase);
    }

    [Fact]
    public void OAK_TaxiD_NeedsVariantForRunway30()
    {
        var layout = LoadLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        var parking = FindParking(layout, "NEW7");
        Assert.NotNull(parking);

        var ac = MakeGroundAircraft(lat: parking.Latitude, lon: parking.Longitude);

        // TAXI D to runway 30 — D doesn't reach runway 30 (it's in the 28/15-33 area).
        // Should fail because D alone can't reach the 30 threshold.
        var taxi = new TaxiCommand(["D"], [], DestinationRunway: "30");
        var result = GroundCommandHandler.TryTaxi(ac, taxi, layout, null, Logger);

        Assert.False(result.Success, "D alone should not reach runway 30");
    }

    // -------------------------------------------------------------------------
    // P4.2: SFO E2E
    // -------------------------------------------------------------------------

    [Fact]
    public void SFO_LayoutLoads_HasMultipleRunwayHoldShorts()
    {
        var layout = LoadLayout("SFO", "sfo");
        if (layout is null)
        {
            return;
        }

        Assert.True(layout.Nodes.Count > 0);
        Assert.True(layout.Edges.Count > 0);

        // SFO has parallel runways — should have hold-shorts for multiple runways
        var holdShortRunways = layout
            .Nodes.Values.Where(n => n.Type == GroundNodeType.RunwayHoldShort && n.RunwayId is not null)
            .Select(n => n.RunwayId!.ToString())
            .Distinct()
            .ToList();

        Assert.True(holdShortRunways.Count >= 2, $"SFO should have hold-shorts for multiple runways, got: [{string.Join(", ", holdShortRunways)}]");
    }

    [Fact]
    public void SFO_TaxiRoute_HasVariantInference()
    {
        var layout = LoadLayout("SFO", "sfo");
        if (layout is null)
        {
            return;
        }

        // Check that SFO has taxiway variants (e.g., A1, A2, etc.)
        var taxiwayNames = layout.Edges.Select(e => e.TaxiwayName).Where(n => n is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // SFO should have multiple taxiway variants
        bool hasVariants = taxiwayNames.Any(n => n.Length >= 2 && char.IsLetter(n[0]) && char.IsDigit(n[^1]));
        Assert.True(hasVariants, $"SFO should have taxiway variants (e.g., A1). Found: [{string.Join(", ", taxiwayNames.Take(20))}]");
    }
}
