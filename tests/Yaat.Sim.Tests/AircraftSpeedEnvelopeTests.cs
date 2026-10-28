using Xunit;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for the per-type maximum operating speed lookup: the category class table
/// (AircraftSpeedEnvelopeClasses.json), the Vmo/Mmo override figures in AircraftProfileOverrides.json, and the
/// profile floor that keeps the limit at or above every speed the type's own profile flies.
/// </summary>
public sealed class AircraftSpeedEnvelopeTests
{
    private static readonly HashSet<string> NoLimitTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "VEH1",
        "VEH2",
        "VEH3",
        "VEH4",
        "VEH5",
        "VEH6",
        "VEH7",
        "F15",
        "F16",
        "F18",
        "F22",
    };

    private static readonly HashSet<string> JetClasses = new(StringComparer.Ordinal) { "BZJT", "J2M", "J2H", "J4H" };

    // Category dummies for the OpenAP types that have neither a profile nor a sibling to take a class from.
    private static readonly Dictionary<string, string> NoClassFamilies = new(StringComparer.OrdinalIgnoreCase) { ["A388"] = "J4H", ["B3XM"] = "J2M" };

    public AircraftSpeedEnvelopeTests() => TestVnasData.EnsureInitialized();

    [Fact]
    public void A320_MaxIas_IsVmoBelowTheCrossoverAndMmoAbove()
    {
        Assert.Equal(350.0, AircraftPerformance.MaxOperatingIasKts("A320", 10000)!.Value, 1);
        Assert.Equal(WindInterpolator.MachToIas(0.82, 34000), AircraftPerformance.MaxOperatingIasKts("A320", 34000)!.Value, 1);
        Assert.Equal(0.82, AircraftPerformance.MaxOperatingMach("A320")!.Value, 3);
    }

    [Fact]
    public void EveryProfileType_HasAClassOrIsListedAsNone()
    {
        Dictionary<string, string> classes = LoadClassTable();
        HashSet<string> profileTypes = ProfileTypes();

        foreach (string type in profileTypes)
        {
            Assert.True(classes.ContainsKey(type) || NoLimitTypes.Contains(type), $"{type} has no speed envelope class and is not a no-limit type");
            Assert.False(classes.ContainsKey(type) && NoLimitTypes.Contains(type), $"{type} is a no-limit type but carries a class");
        }

        foreach (string type in classes.Keys)
        {
            Assert.True(profileTypes.Contains(type), $"class table entry {type} is not a profile type");
        }
    }

    [Fact]
    public void ClassAgreesWithCategory()
    {
        foreach ((string type, string code) in LoadClassTable())
        {
            AircraftCategory cat = AircraftCategorization.Categorize(type);
            AircraftCategory expected = code switch
            {
                "DUMH" => AircraftCategory.Helicopter,
                "GA" => AircraftCategory.Piston,
                "TP2M" => AircraftCategory.Turboprop,
                _ when JetClasses.Contains(code) => AircraftCategory.Jet,
                _ => throw new InvalidOperationException($"unknown class {code} for {type}"),
            };
            Assert.True(cat == expected, $"{type} is class {code} but categorizes as {cat}");
        }
    }

    [Fact]
    public void OpenApOverrides_AreWithin15PercentOfTheirClass()
    {
        AircraftProfileOverride[] openAp = [.. ShippedOverrides().Where(o => o.Note.Contains("OpenAP", StringComparison.Ordinal))];
        Assert.NotEmpty(openAp);

        foreach (AircraftProfileOverride ov in openAp)
        {
            SpeedEnvelopeClass cls =
                AircraftSpeedEnvelope.ClassFor(ov.TypeCode)
                ?? AircraftSpeedEnvelope.ClassesByCode[
                    NoClassFamilies.GetValueOrDefault(ov.TypeCode) ?? throw new InvalidOperationException(ov.TypeCode)
                ];

            if (ov.Vmo is { } vmo)
            {
                Assert.InRange(vmo, cls.VmoKts * 0.85, cls.VmoKts * 1.15);
            }

            if (ov.Mmo is { } mmo)
            {
                Assert.NotNull(cls.Mmo);
                Assert.InRange(mmo, cls.Mmo.Value * 0.85, cls.Mmo.Value * 1.15);
            }
        }
    }

    [Fact]
    public void FighterAndVehicle_HaveNoLimit()
    {
        Assert.Null(AircraftPerformance.MaxOperatingIasKts("F16", 10000));
        Assert.Null(AircraftPerformance.MaxOperatingMach("F16"));
        Assert.Null(AircraftPerformance.MaxOperatingIasKts("VEH1", 0));
        Assert.Null(AircraftPerformance.MaxOperatingMach("VEH1"));
    }

    [Fact]
    public void UnknownType_HasNoLimit()
    {
        Assert.Null(AircraftPerformance.MaxOperatingIasKts("ZZZZ", 10000));
        Assert.Null(AircraftPerformance.MaxOperatingMach("ZZZZ"));
    }

    [Fact]
    public void VmoMmoOnlyOverride_CreatesNoProfile()
    {
        // A388 has neither a profile nor a sibling: its Vmo/Mmo-only override must not synthesize one.
        Assert.Null(AircraftProfileDatabase.Get("A388"));

        // A20N falls back to its sibling's profile, untouched by its Vmo/Mmo-only override.
        Assert.Equal("A320", AircraftProfileDatabase.Get("A20N")?.TypeCode);

        // The override figures still give A388 a limit: OpenAP's Vmo, with no profile floor.
        Assert.Equal(340.0, AircraftPerformance.MaxOperatingIasKts("A388", 10000)!.Value, 1);
    }

    private static Dictionary<string, string> LoadClassTable() =>
        AircraftSpeedEnvelope.LoadFromFile(Path.Combine(AppContext.BaseDirectory, "Data", "AircraftSpeedEnvelopeClasses.json"));

    private static HashSet<string> ProfileTypes() =>
        AircraftProfileDatabase
            .LoadFromFile(Path.Combine(AppContext.BaseDirectory, "Data", "AircraftProfiles.json"))
            .Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<AircraftProfileOverride> ShippedOverrides() =>
        AircraftProfileDatabase.LoadOverridesFromFile(Path.Combine(AppContext.BaseDirectory, "Data", "AircraftProfileOverrides.json"));
}
