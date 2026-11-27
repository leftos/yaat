using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring.AirportMap;

namespace Yaat.Client.Views.Authoring;

public sealed partial class AirportEditorCanvas
{
    // A taxiway or runway needs this many vertices: MapFeature.MinLineVertices, which is internal to Yaat.Sim.
    private const int MinDrawnVertices = 2;

    // The unsaved vertices of the drawing in progress, on the UI thread; each frame copies them.
    private readonly List<MapPosition> _drawing = [];
    private AirportEditorMode _mode;
    private MapPosition? _drawCursor;
    private MapPosition? _drawSnapAt;

    /// <summary>
    /// Gets or sets what a left press does, the view model's mode; set on the UI thread. A change drops the drag and the
    /// drawing in progress.
    /// </summary>
    public AirportEditorMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
            {
                return;
            }

            _mode = value;
            if (_drag is not null)
            {
                CancelDrag();
            }

            _drawing.Clear();
            ClearDrawCursor();
        }
    }

    /// <summary>Gets a value indicating whether a taxiway or runway drawing is in progress.</summary>
    public bool IsDrawing => _drawing.Count > 0;

    /// <summary>Gets the unsaved vertices of the drawing in progress. Test seam.</summary>
    internal ImmutableArray<MapPosition> DrawingVertices => [.. _drawing];

    private MapPosition? DrawSnapAt => _mode == AirportEditorMode.Select ? null : _drawSnapAt;

    /// <summary>Drops the drawing in progress unsaved; does nothing when there is none.</summary>
    public void CancelDrawing()
    {
        if (_drawing.Count > 0)
        {
            _drawing.Clear();
            MarkDirty();
        }
    }

    /// <summary>Finishes a taxiway drawing of at least two vertices as one new taxiway; does nothing otherwise.</summary>
    public void FinishDrawing()
    {
        if ((_mode == AirportEditorMode.DrawTaxiway) && (_drawing.Count >= MinDrawnVertices))
        {
            Commit("taxiway");
        }
    }

    /// <summary>Drops a drag in progress, or else a drawing in progress, as Esc does first.</summary>
    /// <returns>True when there was one to drop.</returns>
    public bool CancelGesture()
    {
        if (_drag is not null)
        {
            CancelDrag();
            return true;
        }

        if (!IsDrawing)
        {
            return false;
        }

        CancelDrawing();
        return true;
    }

    /// <summary>Clears the selection as Esc in Select mode does, raising <see cref="SelectionChanged"/> when it changes.</summary>
    public void ClearSelectionFromUser() => SetSelectionFromUser(null);

    // A taxiway press adds a vertex, and the second press of a double-click finishes instead; a runway's second end
    // finishes it; a place mode places. A repeated press of a double-click adds nothing anywhere else.
    private void DrawAt(Point at, int clickCount)
    {
        MapPosition position = SnapScreen(at).Position;
        if (_mode == AirportEditorMode.DrawTaxiway)
        {
            if (clickCount >= 2)
            {
                FinishDrawing();
            }
            else
            {
                AddVertex(position);
            }

            return;
        }

        if (clickCount >= 2)
        {
            return;
        }

        if (_mode == AirportEditorMode.DrawRunway)
        {
            AddVertex(position);
            if (_drawing.Count >= MinDrawnVertices)
            {
                Commit("runway");
            }
        }
        else if (AirportEditorModes.FeatureType(_mode) is { } featureType)
        {
            EditTarget?.AddFeature(featureType, [position]);
        }
    }

    // A press on the last vertex again would repeat it, so it adds nothing.
    private void AddVertex(MapPosition position)
    {
        if ((_drawing.Count > 0) && (_drawing[^1] == position))
        {
            return;
        }

        _drawing.Add(position);
        MarkDirty();
    }

    private void Commit(string featureType)
    {
        ImmutableArray<MapPosition> positions = [.. _drawing];
        _drawing.Clear();
        MarkDirty();
        EditTarget?.AddFeature(featureType, positions);
    }

    // In a draw or place mode the snapped cursor follows the pointer, for the ghost line and the crosshair.
    private void UpdateDrawCursor(Point at)
    {
        if (_mode == AirportEditorMode.Select)
        {
            return;
        }

        (_drawCursor, _drawSnapAt) = SnapScreen(at);
        MarkDirty();
    }

    private void ClearDrawCursor()
    {
        _drawCursor = null;
        _drawSnapAt = null;
        MarkDirty();
    }

    // The map position under a canvas point, snapped with the default options to the map and the drawing's own vertices,
    // and the target it snapped to, or null for none.
    private (MapPosition Position, MapPosition? SnapAt) SnapScreen(Point at)
    {
        (double lat, double lon) = Viewport.ScreenToLatLon((float)at.X, (float)at.Y);
        var cursor = new MapPosition(lon, lat);
        if (_document is not { } document)
        {
            return (cursor, null);
        }

        SnapResult? snap = SnapEngine.Find(document, cursor, SnapOptions.Default, null, DrawingTargets());
        return (snap?.Position ?? cursor, snap?.Position);
    }

    private List<SnapTarget>? DrawingTargets()
    {
        if (_drawing.Count == 0)
        {
            return null;
        }

        SnapTargetKind kind = _mode == AirportEditorMode.DrawRunway ? SnapTargetKind.RunwayVertex : SnapTargetKind.TaxiwayVertex;
        return [.. _drawing.Select(p => new SnapTarget(p, kind))];
    }

    private AirportEditorDrawGhost? BuildGhost() => _drawing.Count == 0 ? null : new AirportEditorDrawGhost([.. _drawing], _drawCursor);

    // On empty space, after a separator: Add Parking Here, Add Spot Here and Add Helipad Here, at the snapped click. None
    // without an edit target.
    private void AddPlaceItems(ContextMenu menu, Point at)
    {
        if (EditTarget is not { } target)
        {
            return;
        }

        MapPosition position = SnapScreen(at).Position;
        menu.Items.Add(new Separator());
        menu.Items.Add(EditItem("Add Parking Here", true, () => target.AddFeature("parking", [position])));
        menu.Items.Add(EditItem("Add Spot Here", true, () => target.AddFeature("spot", [position])));
        menu.Items.Add(EditItem("Add Helipad Here", true, () => target.AddFeature("helipad", [position])));
    }
}
