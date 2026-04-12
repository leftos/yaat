using System.Diagnostics;
using System.Text.Json;
using Yaat.Sim.Data.Airport;

var text = File.ReadAllText("X:/dev/vzoa/training-files/atctrainer-airport-files/oak.geojson");

// Warm up JIT
GeoJsonParser.Parse("OAK", text, null, applyFillets: false);

var sw = Stopwatch.StartNew();
var noFillet = GeoJsonParser.Parse("OAK", text, null, applyFillets: false);
Console.WriteLine($"Parse (no fillets):    {sw.ElapsedMilliseconds}ms — {noFillet.Nodes.Count} nodes, {noFillet.Edges.Count} edges");

sw.Restart();
var withFillet = GeoJsonParser.Parse("OAK", text, null, applyFillets: true);
Console.WriteLine(
    $"Parse (with fillets):  {sw.ElapsedMilliseconds}ms — {withFillet.Nodes.Count} nodes, {withFillet.Edges.Count} edges, {withFillet.Arcs.Count} arcs"
);

sw.Restart();
var json = JsonSerializer.SerializeToUtf8Bytes(withFillet);
Console.WriteLine($"Serialize to JSON:     {sw.ElapsedMilliseconds}ms — {json.Length / 1024}KB");

sw.Restart();
var deserialized = JsonSerializer.Deserialize<AirportGroundLayout>(json);
deserialized!.RebuildAdjacencyLists();
Console.WriteLine($"Deserialize + rebuild: {sw.ElapsedMilliseconds}ms — {deserialized.Nodes.Count} nodes");

// SFO too
text = File.ReadAllText("X:/dev/vzoa/training-files/atctrainer-airport-files/sfo.geojson");
sw.Restart();
var sfo = GeoJsonParser.Parse("SFO", text, null, applyFillets: true);
Console.WriteLine($"\nSFO parse+fillet:      {sw.ElapsedMilliseconds}ms — {sfo.Nodes.Count} nodes, {sfo.Edges.Count} edges, {sfo.Arcs.Count} arcs");
