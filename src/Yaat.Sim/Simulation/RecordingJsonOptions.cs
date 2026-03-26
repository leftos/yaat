using System.Text.Encodings.Web;
using System.Text.Json;

namespace Yaat.Sim.Simulation;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> for recording serialization.
/// </summary>
public static class RecordingJsonOptions
{
    public static JsonSerializerOptions Default { get; } =
        new()
        {
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = new LenientRequiredResolver(),
        };
}
