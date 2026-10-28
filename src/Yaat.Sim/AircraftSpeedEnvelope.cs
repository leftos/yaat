using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;

namespace Yaat.Sim;

/// <summary>
/// A category's maximum operating speeds: Vmo in KCAS and Mmo (null where the class has none, as for helicopters).
/// </summary>
public sealed record SpeedEnvelopeClass(string Code, double VmoKts, double? Mmo);

/// <summary>
/// Category maximum operating speeds and the per-type class table (Data/AircraftSpeedEnvelopeClasses.json, a flat
/// <c>{ "A320": "J2M", … }</c> object over the profile types). The class figures are the dummy aircraft models
/// shipped with EUROCONTROL's pybada (<c>eurocontrol-bada/pybada</c>, EUPL-1.2). A type with no entry of its own
/// takes its <see cref="AircraftSiblingMap"/> sibling's class; a type with neither has no class.
/// </summary>
public static class AircraftSpeedEnvelope
{
    private static readonly ILogger Log = SimLog.CreateLogger("AircraftSpeedEnvelope");

    private static readonly SpeedEnvelopeClass[] Classes =
    [
        // pybada src/aircraft/BADA3/DUMMY/BZJT__.OPF: business jet.
        new("BZJT", 292, 0.755),
        // pybada src/aircraft/BADA3/DUMMY/J2M___.OPF: twin-engine medium jet.
        new("J2M", 340, 0.82),
        // pybada src/aircraft/BADA3/DUMMY/J2H___.OPF: twin-engine heavy jet.
        new("J2H", 335, 0.82),
        // pybada src/aircraft/BADA3/DUMMY/J4H___.OPF: four-engine heavy jet.
        new("J4H", 365, 0.92),
        // pybada src/aircraft/BADA3/DUMMY/TP2M__.OPF: twin turboprop.
        new("TP2M", 250, 0.55),
        // pybada src/aircraft/BADA3/DUMMY/GA____.OPF: light piston.
        new("GA", 126, 0.24),
        // pybada src/aircraft/BADAH/DUMMY/DUMH/DUMH.xml: helicopter, Vne 150 and no Mmo.
        new("DUMH", 150, null),
    ];

    /// <summary>The class figures keyed by class code.</summary>
    public static IReadOnlyDictionary<string, SpeedEnvelopeClass> ClassesByCode { get; } = Classes.ToDictionary(c => c.Code, StringComparer.Ordinal);

    private static Dictionary<string, SpeedEnvelopeClass> _typeClasses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Install the per-type class table. Throws on a class code that names no class, so a typo in the data file fails
    /// at load rather than leaving the type without a limit.
    /// </summary>
    public static void Initialize(Dictionary<string, string> typeToClassCode)
    {
        var lookup = new Dictionary<string, SpeedEnvelopeClass>(typeToClassCode.Count, StringComparer.OrdinalIgnoreCase);
        foreach ((string type, string code) in typeToClassCode)
        {
            if (!ClassesByCode.TryGetValue(code, out SpeedEnvelopeClass? cls))
            {
                throw new InvalidOperationException(
                    $"Speed envelope class '{code}' for type {type} is not one of {string.Join(", ", ClassesByCode.Keys)}"
                );
            }

            lookup[type] = cls;
        }

        _typeClasses = lookup;
        AircraftPerformance.ClearSpeedEnvelopeCache();
        Log.LogInformation("Aircraft speed envelope classes loaded: {Count} types", _typeClasses.Count);
    }

    /// <summary>
    /// The class for <paramref name="aircraftType"/>: its own entry, else its sibling's, else null (no limit known).
    /// </summary>
    public static SpeedEnvelopeClass? ClassFor(string? aircraftType)
    {
        if (string.IsNullOrEmpty(aircraftType))
        {
            return null;
        }

        string baseType = AircraftState.NormalizedBaseType(aircraftType);
        if (_typeClasses.TryGetValue(baseType, out SpeedEnvelopeClass? cls))
        {
            return cls;
        }

        if (AircraftSiblingMap.TryResolve(baseType, out string sibling) && _typeClasses.TryGetValue(sibling, out SpeedEnvelopeClass? sibCls))
        {
            return sibCls;
        }

        return null;
    }

    /// <summary>Load the per-type class table (type code to class code) from its JSON file.</summary>
    public static Dictionary<string, string> LoadFromFile(string path)
    {
        string json = File.ReadAllText(path);
        Dictionary<string, string> table =
            JsonSerializer.Deserialize<Dictionary<string, string>>(json)
            ?? throw new InvalidOperationException($"Failed to deserialize aircraft speed envelope classes from {path}");
        return new Dictionary<string, string>(table, StringComparer.OrdinalIgnoreCase);
    }
}
