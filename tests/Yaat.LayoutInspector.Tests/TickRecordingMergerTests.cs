using Xunit;
using Yaat.LayoutInspector.Tick;

namespace Yaat.LayoutInspector.Tests;

/// <summary>
/// Merging several TickRecorder readings into one: label renaming, palette colouring,
/// time ordering, and the fail-fast checks (shared callsign, mixed airports).
/// </summary>
public class TickRecordingMergerTests
{
    [Fact]
    public void Merge_renames_one_aircraft_runs_to_their_labels_and_colours_them()
    {
        TickRecording merged = TickRecordingMerger.Merge([
            Source("SFO", "TODAY", "today.json", new AircraftRun("SKW3398", [(1, "SKW3398"), (3, "SKW3398")])),
            Source("SFO", "MAIN", "main.json", new AircraftRun("SKW3398", [(2, "SKW3398"), (4, "SKW3398")])),
        ]);

        Assert.Equal(["TODAY", "MAIN"], [.. merged.Aircraft.Select(a => a.Callsign)]);
        Assert.Equal(["#e53935", "#43a047"], [.. merged.Aircraft.Select(a => a.Color)]);
        Assert.Equal([1, 2, 3, 4], [.. merged.Ticks.Select(t => t.T)]);
        Assert.Equal(["TODAY", "MAIN", "TODAY", "MAIN"], [.. merged.Ticks.Select(t => t.Callsign)]);
    }

    [Fact]
    public void Merge_keeps_the_recorded_colour_for_a_single_recording()
    {
        TickRecording merged = TickRecordingMerger.Merge([Source("SFO", null, "only.json", new AircraftRun("SKW3398", [(1, "SKW3398")]))]);

        Assert.Equal("#1e88e5", merged.Aircraft[0].Color);
        Assert.Equal("SKW3398", merged.Aircraft[0].Callsign);
        Assert.Equal("SFO", merged.AirportId);
    }

    [Fact]
    public void Merge_prefixes_the_label_for_a_multi_aircraft_recording()
    {
        TickRecording merged = TickRecordingMerger.Merge([
            Source("SFO", "TODAY", "today.json", new AircraftRun("AAL1", [(1, "AAL1")]), new AircraftRun("UAL2", [(1, "UAL2")])),
            Source("SFO", "MAIN", "main.json", new AircraftRun("DAL3", [(1, "DAL3")])),
        ]);

        Assert.Equal(["TODAY:AAL1", "TODAY:UAL2", "MAIN"], [.. merged.Aircraft.Select(a => a.Callsign)]);
    }

    [Fact]
    public void Merge_orders_ticks_by_time_and_keeps_the_source_order_on_ties()
    {
        TickRecording merged = TickRecordingMerger.Merge([
            Source("SFO", "TODAY", "today.json", new AircraftRun("SKW3398", [(5, "SKW3398"), (1, "SKW3398")])),
            Source("SFO", "MAIN", "main.json", new AircraftRun("SKW3398", [(5, "SKW3398"), (2, "SKW3398")])),
        ]);

        Assert.Equal([1, 2, 5, 5], [.. merged.Ticks.Select(t => t.T)]);
        Assert.Equal(["TODAY", "MAIN", "TODAY", "MAIN"], [.. merged.Ticks.Select(t => t.Callsign)]);
    }

    [Fact]
    public void Merge_rejects_two_recordings_that_share_a_callsign_without_labels()
    {
        TickMergeException ex = Assert.Throws<TickMergeException>(() =>
            TickRecordingMerger.Merge([
                Source("SFO", null, "a.json", new AircraftRun("SKW3398", [(1, "SKW3398")])),
                Source("SFO", null, "b.json", new AircraftRun("SKW3398", [(1, "SKW3398")])),
            ])
        );

        Assert.Equal("--ticks recordings a.json and b.json both contain callsign SKW3398; give each a LABEL= prefix", ex.Message);
    }

    [Fact]
    public void Merge_rejects_recordings_from_different_airports()
    {
        TickMergeException ex = Assert.Throws<TickMergeException>(() =>
            TickRecordingMerger.Merge([
                Source("OAK", "TODAY", "oak.json", new AircraftRun("SKW3398", [(1, "SKW3398")])),
                Source("SFO", "MAIN", "sfo.json", new AircraftRun("SKW3398", [(1, "SKW3398")])),
            ])
        );

        Assert.Equal("--ticks recordings oak.json (OAK) and sfo.json (SFO) are from different airports", ex.Message);
    }

    private static LoadedTickSource Source(string airportId, string? label, string path, params AircraftRun[] runs) =>
        new(
            label,
            path,
            new TickRecording
            {
                Version = TickRecording.CurrentVersion,
                AirportId = airportId,
                Aircraft = [.. runs.Select(r => Aircraft(r.Callsign))],
                Ticks = [.. runs.SelectMany(r => r.Ticks.Select(t => Tick(t.T, t.Callsign)))],
            }
        );

    private static AircraftMetadata Aircraft(string callsign) =>
        new()
        {
            Callsign = callsign,
            Type = "E75L",
            WingspanFt = 101.7,
            LengthFt = 106,
            Color = "#1e88e5",
        };

    private static TickEvent Tick(int t, string callsign) =>
        new()
        {
            T = t,
            Callsign = callsign,
            Lat = 37.621828,
            Lon = -122.385551,
            Hdg = 299.35,
            Gs = 1,
            Phase = "Taxi",
        };

    private sealed record AircraftRun(string Callsign, (int T, string Callsign)[] Ticks);
}
