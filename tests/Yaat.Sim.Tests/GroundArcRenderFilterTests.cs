using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;

namespace Yaat.Sim.Tests;

/// <summary>
/// Locks the semantics behind the ground-view "hide never-driven fillet arcs" render filter
/// (<c>GroundRenderer.DrawEdges</c>): an arc whose <see cref="GroundArc.TurnAngleDeg"/> exceeds the
/// most permissive fixed-wing heading-change limit (Piston, 155°) can be entered by no fixed-wing
/// aircraft, so the taxi pathfinder never routes it (<see cref="GeometricAdmissibility"/>) — drawing
/// it would only clutter the view with an unusable taxi line. Uses real SFO geometry
/// (<c>TestData/sfo.geojson</c>).
/// </summary>
public class GroundArcRenderFilterTests
{
    private static AirportGroundLayout? LoadSfo()
    {
        string path = Path.Combine("TestData", "sfo.geojson");
        return File.Exists(path) ? GeoJsonParser.Parse("SFO", File.ReadAllText(path), null) : null;
    }

    [Fact]
    public void NeverDrivenArcs_ExistOnSfo_AndExceedEveryFixedWingTurnLimit()
    {
        var layout = LoadSfo();
        if (layout is null)
        {
            return;
        }

        double pistonLimit = CategoryLimits.MaxHeadingChangeDeg(AircraftCategory.Piston);
        var hidden = layout.Arcs.Where(a => a.TurnAngleDeg > pistonLimit).ToList();

        // The render filter actually hides something on a real airport.
        Assert.NotEmpty(hidden);

        // Every hidden arc is un-taxiable by every fixed-wing category — i.e. genuinely never routed,
        // which is exactly what makes it safe to omit from the view.
        foreach (var arc in hidden)
        {
            Assert.True(arc.TurnAngleDeg > CategoryLimits.MaxHeadingChangeDeg(AircraftCategory.Jet));
            Assert.True(arc.TurnAngleDeg > CategoryLimits.MaxHeadingChangeDeg(AircraftCategory.Turboprop));
            Assert.True(arc.TurnAngleDeg > CategoryLimits.MaxHeadingChangeDeg(AircraftCategory.Piston));
        }

        // Guard against a future layout or threshold regression that would hide normal driveable
        // corners: the never-driven set must stay a small fraction of all arcs.
        Assert.True(
            hidden.Count < layout.Arcs.Count / 10,
            $"hidden {hidden.Count} of {layout.Arcs.Count} arcs — too many; the filter may be hiding real corners"
        );
    }
}
