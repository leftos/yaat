using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Yaat.Sim.Simulation.Oracle;

/// <summary>
/// A 64-bit FNV-1a hash of the tree <see cref="SnapshotTreeDiff.ToComparableNode"/> produces, over a canonical
/// serialization of it: object properties in ordinal key order at every depth, arrays in order, every leaf in its own
/// JSON text (numbers in their round-trip form). The oracle compares one of these per run kind per second and runs the
/// full <see cref="SnapshotTreeDiff.CompareNodes"/> only for a second whose hashes differ.
///
/// Equal hashes mean equal canonical text, and equal text means the diff has nothing to report — so the hash can only
/// skip work, never hide a divergence. The converse does not hold and does not need to: the diff folds virtual node ids
/// together, keys the aircraft list by callsign and re-parses embedded JSON, so a second can hash differently and still
/// diff empty. The hash is stable across processes, which <see cref="string.GetHashCode()"/> is not.
/// </summary>
public static class SnapshotStateHash
{
    private const ulong FnvOffsetBasis = 14695981039346656037UL;

    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>Hashes one comparable tree. A null tree (a JSON null) hashes like the text <c>null</c>.</summary>
    public static ulong Compute(JsonNode? comparableNode) => Fnv1a(CanonicalUtf8(comparableNode));

    /// <summary>The canonical serialization <see cref="Compute"/> hashes, as compact UTF-8 JSON.</summary>
    public static byte[] CanonicalUtf8(JsonNode? comparableNode)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteCanonical(writer, comparableNode);
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                return;
            case JsonObject obj:
                WriteObject(writer, obj);
                return;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (JsonNode? element in array)
                {
                    WriteCanonical(writer, element);
                }

                writer.WriteEndArray();
                return;
            default:
                node.WriteTo(writer);
                return;
        }
    }

    private static void WriteObject(Utf8JsonWriter writer, JsonObject obj)
    {
        writer.WriteStartObject();
        foreach (KeyValuePair<string, JsonNode?> property in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            writer.WritePropertyName(property.Key);
            WriteCanonical(writer, property.Value);
        }

        writer.WriteEndObject();
    }

    private static ulong Fnv1a(ReadOnlySpan<byte> bytes)
    {
        ulong hash = FnvOffsetBasis;
        foreach (byte b in bytes)
        {
            hash ^= b;
            hash *= FnvPrime;
        }

        return hash;
    }
}
