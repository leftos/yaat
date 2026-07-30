using Yaat.Sim;

namespace Yaat.Client.Views.Map;

/// <summary>
/// One end of a range/bearing line. Either a fixed point on the map, or an aircraft the endpoint is
/// latched to — a latched endpoint is re-resolved to the aircraft's live position every frame, so the
/// line travels with the target.
/// </summary>
/// <param name="Callsign">Non-null when the endpoint is latched to an aircraft.</param>
/// <param name="FixedPosition">The map position when <paramref name="Callsign" /> is null; unused otherwise.</param>
/// <param name="Label">Display text for this end (callsign, fix name, or FRD).</param>
public readonly record struct RblEndpoint(string? Callsign, LatLon FixedPosition, string Label)
{
    /// <summary>True when this end follows an aircraft rather than staying put.</summary>
    public bool IsLatched => !string.IsNullOrEmpty(Callsign);

    /// <summary>An endpoint that follows <paramref name="callsign" />.</summary>
    public static RblEndpoint OnAircraft(string callsign) => new(callsign, default, callsign);

    /// <summary>An endpoint pinned to a fixed map position.</summary>
    public static RblEndpoint AtPoint(LatLon position, string label) => new(null, position, label);
}

/// <summary>A placed range/bearing line occupying slot <paramref name="Slot" /> (1-based, as displayed).</summary>
public sealed record RangeBearingLine(int Slot, RblEndpoint A, RblEndpoint B);

/// <summary>Live state of an aircraft an endpoint is latched to.</summary>
public readonly record struct RblTrack(LatLon Position, double? GroundSpeedKts);

/// <summary>Resolves a latched endpoint's callsign to its current position, or null if it is gone.</summary>
public delegate RblTrack? RblTrackLookup(string callsign);

/// <summary>Which units a view labels distances in.</summary>
public enum RblUnits
{
    /// <summary>Always nautical miles to two decimals (Radar View).</summary>
    NauticalMiles,

    /// <summary>Feet below one mile, nautical miles above it (Ground View).</summary>
    FeetThenNauticalMiles,
}

/// <summary>A range/bearing line resolved to screen-ready geography for one frame.</summary>
/// <param name="Slot">1-based slot number, or 0 for the half-placed line following the cursor.</param>
/// <param name="ALatched">True when the first end follows an aircraft, so the renderer skips its marker.</param>
/// <param name="BLatched">True when the second end follows an aircraft.</param>
public sealed record ResolvedRbl(int Slot, LatLon A, LatLon B, string Label, bool ALatched, bool BLatched);

/// <summary>
/// The instructor's placed range/bearing lines, shared by the Radar and Ground views so a measurement
/// taken in one shows up in the other under the same slot number.
/// </summary>
/// <remarks>
/// Mirrors CRC STARS' <c>*T</c> tool: at most fifteen lines, each labelled with its slot number, each
/// endpoint either a fixed point or an aircraft it follows. UI-thread only — the render thread reads a
/// snapshot taken by <see cref="RangeBearingLineResolver" />, never this object.
/// </remarks>
public sealed class RangeBearingLineStore
{
    /// <summary>Slot ceiling, matching CRC STARS.</summary>
    public const int MaxLines = 15;

    private readonly RangeBearingLine?[] _slots = new RangeBearingLine?[MaxLines];

    /// <summary>Raised whenever the placed lines, the armed state, or the pending anchor change.</summary>
    public event Action? Changed;

    /// <summary>True while the tool is waiting for the user to pick endpoints.</summary>
    public bool IsArmed { get; private set; }

    /// <summary>The first endpoint once picked, while waiting for the second.</summary>
    public RblEndpoint? PendingAnchor { get; private set; }

    /// <summary>True when all <see cref="MaxLines" /> slots are occupied.</summary>
    public bool IsFull => LowestFreeSlotIndex() < 0;

    /// <summary>True when at least one line is placed.</summary>
    public bool HasLines
    {
        get
        {
            foreach (var slot in _slots)
            {
                if (slot is not null)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>The placed lines, ascending by slot number.</summary>
    public IReadOnlyList<RangeBearingLine> Lines
    {
        get
        {
            var result = new List<RangeBearingLine>(MaxLines);
            foreach (var slot in _slots)
            {
                if (slot is not null)
                {
                    result.Add(slot);
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Arms the tool so the next map click picks the first endpoint. Returns false when every slot is
    /// already taken, so the caller can report it rather than arming a tool that cannot complete.
    /// </summary>
    public bool Arm()
    {
        if (IsFull)
        {
            return false;
        }

        IsArmed = true;
        PendingAnchor = null;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Cancels the tool, discarding any half-placed line. Placed lines are kept.</summary>
    public void Disarm()
    {
        if (!IsArmed && PendingAnchor is null)
        {
            return;
        }

        IsArmed = false;
        PendingAnchor = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Picks the first endpoint. Returns false when every slot is taken — the anchor is not set, so the
    /// user is never left dragging a rubber band that cannot be committed.
    /// </summary>
    public bool SetAnchor(RblEndpoint anchor)
    {
        if (IsFull)
        {
            return false;
        }

        IsArmed = true;
        PendingAnchor = anchor;
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Picks the second endpoint and places the line in the lowest free slot. Returns the slot number
    /// (1-based) or null when there was no anchor or no free slot.
    /// </summary>
    public int? Complete(RblEndpoint end)
    {
        if (PendingAnchor is not { } anchor)
        {
            return null;
        }

        var index = LowestFreeSlotIndex();
        if (index < 0)
        {
            return null;
        }

        var slot = index + 1;
        _slots[index] = new RangeBearingLine(slot, anchor, end);
        IsArmed = false;
        PendingAnchor = null;
        Changed?.Invoke();
        return slot;
    }

    /// <summary>
    /// Places a whole line at once, for the modifier-drag path where both endpoints arrive together.
    /// Returns the slot number, or null when full.
    /// </summary>
    public int? Add(RblEndpoint a, RblEndpoint b)
    {
        var index = LowestFreeSlotIndex();
        if (index < 0)
        {
            return null;
        }

        var slot = index + 1;
        _slots[index] = new RangeBearingLine(slot, a, b);
        IsArmed = false;
        PendingAnchor = null;
        Changed?.Invoke();
        return slot;
    }

    /// <summary>Removes the line in <paramref name="slot" /> (1-based). Returns false if it was empty.</summary>
    public bool Remove(int slot)
    {
        var index = slot - 1;
        if (index < 0 || index >= MaxLines || _slots[index] is null)
        {
            return false;
        }

        _slots[index] = null;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Removes every line and cancels any pending pick.</summary>
    public void Clear()
    {
        var changed = IsArmed || PendingAnchor is not null;
        for (var i = 0; i < MaxLines; i++)
        {
            if (_slots[i] is not null)
            {
                _slots[i] = null;
                changed = true;
            }
        }

        IsArmed = false;
        PendingAnchor = null;
        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Drops any line — and any pending anchor — latched to an aircraft that no longer exists, matching
    /// CRC's behaviour when a track is dropped. Call whenever the aircraft list changes.
    /// </summary>
    public void PruneMissing(Func<string, bool> aircraftExists)
    {
        var changed = false;
        for (var i = 0; i < MaxLines; i++)
        {
            if (_slots[i] is not { } line)
            {
                continue;
            }

            if (IsMissing(line.A, aircraftExists) || IsMissing(line.B, aircraftExists))
            {
                _slots[i] = null;
                changed = true;
            }
        }

        if (PendingAnchor is { } anchor && IsMissing(anchor, aircraftExists))
        {
            PendingAnchor = null;
            changed = true;
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private static bool IsMissing(RblEndpoint endpoint, Func<string, bool> aircraftExists) =>
        endpoint.Callsign is { } callsign && !aircraftExists(callsign);

    private int LowestFreeSlotIndex()
    {
        for (var i = 0; i < MaxLines; i++)
        {
            if (_slots[i] is null)
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>
/// Builds the label a range/bearing line carries: magnetic bearing, distance, an optional time-to-go,
/// and the slot number.
/// </summary>
/// <remarks>
/// Follows CRC STARS' format (<c>DisplayElementRangeBearingLines.Render</c>), with one deliberate
/// difference: the bearing is zero-padded to three digits, which is how bearings are written
/// everywhere else in YAAT. A bearing of zero renders as <c>360</c>, matching CRC's
/// <c>NavCalc.NormalizeHeading</c>.
/// </remarks>
public static class RangeBearingLineFormatter
{
    private const double MaxDisplayNm = 999.99;
    private const int MaxDisplayMinutes = 99;

    /// <summary>
    /// Formats one line's label.
    /// </summary>
    /// <param name="distanceNm">Great-circle distance between the endpoints.</param>
    /// <param name="magneticBearing">Bearing from the first endpoint to the second, in magnetic degrees.</param>
    /// <param name="minutesToGo">
    /// Time for the moving endpoint to reach the fixed one, or null when the pair does not qualify —
    /// CRC shows it only when exactly one end is an aircraft with groundspeed.
    /// </param>
    /// <param name="slot">1-based slot number, or null while the line is still being placed.</param>
    /// <param name="units">Which units this view labels distance in.</param>
    public static string Format(double distanceNm, double magneticBearing, int? minutesToGo, int? slot, RblUnits units)
    {
        var bearing = FormatBearing(magneticBearing);
        var distance = FormatDistance(distanceNm, units);

        var label = $"{bearing}/{distance}";
        if (minutesToGo is { } minutes)
        {
            label += $"/{(minutes > MaxDisplayMinutes ? "##" : minutes.ToString("0"))}";
        }

        if (slot is { } number)
        {
            label += $"-{number}";
        }

        return label;
    }

    /// <summary>Rounds to whole degrees and renders zero as 360, as bearings are read aloud.</summary>
    private static string FormatBearing(double magneticBearing)
    {
        var normalized = ((magneticBearing % 360.0) + 360.0) % 360.0;
        var rounded = (int)Math.Round(normalized, MidpointRounding.AwayFromZero);
        if (rounded <= 0 || rounded > 360)
        {
            rounded = rounded <= 0 ? rounded + 360 : rounded - 360;
        }

        return rounded.ToString("000");
    }

    private static string FormatDistance(double distanceNm, RblUnits units)
    {
        if (units == RblUnits.FeetThenNauticalMiles && distanceNm < 1.0)
        {
            var feet = (int)Math.Round(distanceNm * GeoMath.FeetPerNm, MidpointRounding.AwayFromZero);
            return $"{feet:N0} ft";
        }

        var nm = distanceNm > MaxDisplayNm ? "###.##" : distanceNm.ToString("0.00");
        return units == RblUnits.FeetThenNauticalMiles ? $"{nm} NM" : nm;
    }

    /// <summary>
    /// Time for a moving endpoint to cover <paramref name="distanceNm" />, or null when the pair does not
    /// qualify: CRC shows it only when exactly one end is an aircraft and it has a positive groundspeed.
    /// </summary>
    public static int? MinutesToGo(RblTrack? a, RblTrack? b, double distanceNm)
    {
        var mover = (a, b) switch
        {
            ({ } track, null) => track,
            (null, { } track) => track,
            _ => (RblTrack?)null,
        };

        if (mover is not { GroundSpeedKts: > 0 } moving)
        {
            return null;
        }

        return (int)Math.Round(distanceNm / moving.GroundSpeedKts!.Value * 60.0, MidpointRounding.AwayFromZero);
    }
}

/// <summary>
/// Turns the placed lines into per-frame geometry and labels. Called on the UI thread while building a
/// canvas render snapshot; the result is immutable and safe to hand to the render thread.
/// </summary>
public static class RangeBearingLineResolver
{
    /// <summary>
    /// Resolves every placed line against the current aircraft positions. Lines latched to an aircraft
    /// that has gone are skipped for this frame rather than drawn at a stale position.
    /// </summary>
    public static List<ResolvedRbl> Resolve(IReadOnlyList<RangeBearingLine> lines, RblTrackLookup lookup, RblUnits units)
    {
        var result = new List<ResolvedRbl>(lines.Count);
        foreach (var line in lines)
        {
            if (ResolveOne(line.A, lookup) is not { } a || ResolveOne(line.B, lookup) is not { } b)
            {
                continue;
            }

            var label = BuildLabel(a, b, line.Slot, units);
            result.Add(new ResolvedRbl(line.Slot, a.Position, b.Position, label, line.A.IsLatched, line.B.IsLatched));
        }

        return result;
    }

    /// <summary>
    /// Resolves the half-placed line the user is dragging: from the pending anchor to the cursor. Returns
    /// null when nothing is pending or the anchored aircraft has gone.
    /// </summary>
    public static ResolvedRbl? ResolvePending(RblEndpoint? anchor, LatLon cursor, RblTrackLookup lookup, RblUnits units)
    {
        if (anchor is not { } endpoint || ResolveOne(endpoint, lookup) is not { } a)
        {
            return null;
        }

        var b = (Position: cursor, Track: (RblTrack?)null);
        return new ResolvedRbl(0, a.Position, cursor, BuildLabel(a, b, null, units), endpoint.IsLatched, false);
    }

    private static string BuildLabel((LatLon Position, RblTrack? Track) a, (LatLon Position, RblTrack? Track) b, int? slot, RblUnits units)
    {
        var distanceNm = GeoMath.DistanceNm(a.Position, b.Position);
        var trueBearing = GeoMath.BearingTo(a.Position, b.Position);

        // Both views are magnetic-north-up, so the bearing is read as magnetic. Declination is taken at
        // the origin end, which is where a controller would be reading the bearing from.
        var magnetic = MagneticDeclination.TrueToMagnetic(trueBearing, a.Position);
        var minutes = RangeBearingLineFormatter.MinutesToGo(a.Track, b.Track, distanceNm);
        return RangeBearingLineFormatter.Format(distanceNm, magnetic, minutes, slot, units);
    }

    private static (LatLon Position, RblTrack? Track)? ResolveOne(RblEndpoint endpoint, RblTrackLookup lookup)
    {
        if (endpoint.Callsign is not { } callsign)
        {
            return (endpoint.FixedPosition, null);
        }

        return lookup(callsign) is { } track ? (track.Position, track) : null;
    }
}

/// <summary>Finds which drawn measurement the cursor is pointing at, for the "remove" menu item.</summary>
public static class RangeBearingHitTest
{
    /// <summary>
    /// Returns the slot number of the measurement whose drawn line passes nearest <paramref name="x" />/
    /// <paramref name="y" /> within <paramref name="maxPixels" />, or null when none is close enough.
    /// </summary>
    public static int? NearestSlot(IReadOnlyList<ResolvedRbl> lines, MapViewport viewport, float x, float y, float maxPixels)
    {
        int? best = null;
        var bestDistance = maxPixels;

        foreach (var line in lines)
        {
            var (ax, ay) = viewport.LatLonToScreen(line.A.Lat, line.A.Lon);
            var (bx, by) = viewport.LatLonToScreen(line.B.Lat, line.B.Lon);
            var distance = DistanceToSegment(x, y, ax, ay, bx, by);
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                best = line.Slot;
            }
        }

        return best;
    }

    private static float DistanceToSegment(float px, float py, float ax, float ay, float bx, float by)
    {
        var dx = bx - ax;
        var dy = by - ay;
        var lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared < 1e-6f)
        {
            return MathF.Sqrt(((px - ax) * (px - ax)) + ((py - ay) * (py - ay)));
        }

        var t = Math.Clamp((((px - ax) * dx) + ((py - ay) * dy)) / lengthSquared, 0f, 1f);
        var cx = ax + (t * dx);
        var cy = ay + (t * dy);
        return MathF.Sqrt(((px - cx) * (px - cx)) + ((py - cy) * (py - cy)));
    }
}
