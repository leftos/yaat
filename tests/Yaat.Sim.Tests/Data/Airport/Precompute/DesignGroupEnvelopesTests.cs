using System.Text.Json;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Data.Airport.Precompute;

/// <summary>
/// The envelope per design group the push targets are planned with: built from the pinned FAA copy by the rule (each
/// dimension's maximum over the group's fixed-wing records, taken separately, the span capped at the group's ceiling),
/// written as the shipped file, read back with every malformed shape refused, and the rule's edges on small hand-built
/// record lists — the one place synthetic records are fine, since they unit-test the rule rather than stand in for navdata.
/// </summary>
public class DesignGroupEnvelopesTests
{
    [Fact]
    public void Build_EnvelopePerGroup_FromPinnedCopy()
    {
        var types = DesignGroupEnvelopes.Build(PinnedRecords(), out IReadOnlyList<string> warnings);

        Assert.DoesNotContain(warnings, w => w.StartsWith("No FAA record", StringComparison.Ordinal));
        Assert.Equal(["I", "II", "III", "IV", "V", "VI"], types.Envelopes.Select(t => t.Group));
        Assert.Equal(["ADG-I", "ADG-II", "ADG-III", "ADG-IV", "ADG-V", "ADG-VI"], types.Envelopes.Select(t => t.EnvelopeCode));
        Assert.All(
            types.Envelopes,
            t =>
                Assert.True(
                    t.WingspanFt <= AirplaneDesignGroups.MaxWingspanFt(DesignGroupEnvelopes.GroupOf(t)),
                    $"{t.EnvelopeCode} span is over its ceiling"
                )
        );

        // Groups I to III are capped: L29B, DC3T and P8 carry a smaller group's ADG with a span over its ceiling.
        var byIcao = PinnedRecords().ToDictionary(r => r.IcaoCode, StringComparer.Ordinal);
        Assert.All(
            types.Envelopes.Take(3),
            t => Assert.True(byIcao[t.Sources.WingspanFt].WingspanFt > t.WingspanFt, $"{t.EnvelopeCode}'s span source is not over the ceiling")
        );

        // lengthFt and its source, wingspanFt (capped) and its source, wheelbaseFt and its source, mainGearWidthFt and its
        // source, and the category of the wheelbase's record.
        (double, string, double, string, double?, string?, double?, string?, AircraftCategory)[] expected =
        [
            (63.8, "F15", 49.0, "L29B", 25.0, "LJ40", 19.8, "C425", AircraftCategory.Jet),
            (106.6, "CRJ7", 79.0, "DC3T", 49.3, "CRJ7", 23.8, "SF34", AircraftCategory.Jet),
            (153.2, "B722", 118.0, "P8", 77.2, "MD90", 34.0, "P3", AircraftCategory.Jet),
            (202.2, "MD11", 170.5, "MD11", 85.8, "B764", 43.2, "DC10", AircraftCategory.Jet),
            (247.2, "A346", 213.0, "BLCF", 107.9, "A346", 47.4, "B2", AircraftCategory.Jet),
            (251.8, "B779", 261.7, "A388", 106.1, "B779", 47.0, "A388", AircraftCategory.Jet),
        ];
        Assert.Equal(
            expected,
            types.Envelopes.Select(t =>
                (
                    t.LengthFt,
                    t.Sources.LengthFt,
                    t.WingspanFt,
                    t.Sources.WingspanFt,
                    t.WheelbaseFt,
                    t.Sources.WheelbaseFt,
                    t.MainGearWidthFt,
                    t.Sources.MainGearWidthFt,
                    t.Category
                )
            )
        );

        AircraftFootprint iv = types.FootprintOf(AirplaneDesignGroup.IV);
        DesignGroupEnvelope ivType = types.Envelopes[3];
        Assert.Equal(
            ("ADG-IV", ivType.LengthFt, ivType.WingspanFt, ivType.WheelbaseFt, ivType.Category),
            (iv.TypeCode, iv.LengthFt, iv.WingspanFt, iv.WheelbaseFt, iv.Category)
        );
    }

    [Fact]
    public void ShippedFile_MatchesBuildFromPinnedFaaCopy()
    {
        var built = DesignGroupEnvelopes.Build(PinnedRecords(), out _);
        string repoRoot = TickRecorder.FindRepoRoot();
        string shippedPath = Path.Combine(repoRoot, "src", "Yaat.Sim", "Data", "PrecomputeCache", "design-group-envelopes.json");
        string expected = DesignGroupEnvelopes.ToJson(built);

        if (File.ReadAllText(shippedPath) != expected)
        {
            string actualPath = Path.Combine(repoRoot, ".tmp", "design-group-envelopes.actual.json");
            Directory.CreateDirectory(Path.GetDirectoryName(actualPath)!);
            DesignGroupEnvelopes.Write(actualPath, built);
            Assert.Fail($"{shippedPath} differs from the build over the pinned FAA copy; the built file is at {actualPath}");
        }

        Assert.Equal(built.Envelopes, DesignGroupEnvelopes.LoadShipped().Envelopes);
    }

    [Fact]
    public void Build_TakesEachMaximumSeparately_CategoryFromWheelbaseRecord()
    {
        FaaAircraftRecord[] records =
        [
            Record("LONG", "III", "Fixed-wing", new Dims(LengthFt: 150.0, SpanFt: 100.0, WheelbaseFt: 50.0, GearWidthFt: 20.0)),
            Record("WIDE", "III", "Fixed-wing", new Dims(LengthFt: 120.0, SpanFt: 117.0, WheelbaseFt: 40.0, GearWidthFt: 21.0)),
            Record("C172", "III", "Fixed-wing", new Dims(LengthFt: 120.0, SpanFt: 36.0, WheelbaseFt: 55.0, GearWidthFt: null)),
            Record("GEAR", "III", "Fixed-wing", new Dims(LengthFt: null, SpanFt: null, WheelbaseFt: null, GearWidthFt: 30.0)),
        ];

        DesignGroupEnvelope envelope = Assert.Single(DesignGroupEnvelopes.Build(records, out _).Envelopes);

        Assert.Equal((150.0, 117.0, 55.0, 30.0), (envelope.LengthFt, envelope.WingspanFt, envelope.WheelbaseFt, envelope.MainGearWidthFt));
        Assert.Equal(
            new DesignGroupSources
            {
                LengthFt = "LONG",
                WingspanFt = "WIDE",
                WheelbaseFt = "C172",
                MainGearWidthFt = "GEAR",
            },
            envelope.Sources
        );
        Assert.Equal(AircraftCategorization.Categorize("C172"), envelope.Category);
        Assert.Equal("ADG-III", envelope.EnvelopeCode);
    }

    [Fact]
    public void Build_GroupIRecordWith54Point3FtSpan_IsCappedAt49()
    {
        FaaAircraftRecord[] records =
        [
            Record("WIDE", "I", "Fixed-wing", new Dims(LengthFt: 40.0, SpanFt: 54.3, WheelbaseFt: 10.0, GearWidthFt: 8.0)),
        ];

        DesignGroupEnvelope envelope = Assert.Single(DesignGroupEnvelopes.Build(records, out _).Envelopes);

        Assert.Equal(("I", 49.0, "WIDE"), (envelope.Group, envelope.WingspanFt, envelope.Sources.WingspanFt));
        Assert.Equal(49.0, DesignGroupEnvelopes.Build(records, out _).FootprintOf(AirplaneDesignGroup.I).WingspanFt);
    }

    [Fact]
    public void Build_ExcludesNonFixedWing()
    {
        FaaAircraftRecord[] records =
        [
            Record("SHRT", "II", "Fixed-wing", new Dims(LengthFt: 60.0, SpanFt: 70.0, WheelbaseFt: 20.0, GearWidthFt: 10.0)),
            Record("HELO", "II", "Helicopter", new Dims(LengthFt: 90.0, SpanFt: 75.0, WheelbaseFt: 30.0, GearWidthFt: 12.0)),
            Record("AMPH", "II", "Amphibian", new Dims(LengthFt: 90.0, SpanFt: 75.0, WheelbaseFt: 30.0, GearWidthFt: 12.0)),
            Record("CASE", "II", "FIXED-WING", new Dims(LengthFt: 65.0, SpanFt: 60.0, WheelbaseFt: 15.0, GearWidthFt: 9.0)),
        ];

        DesignGroupEnvelope envelope = Assert.Single(DesignGroupEnvelopes.Build(records, out _).Envelopes);

        Assert.Equal((65.0, 70.0, 20.0, 10.0), (envelope.LengthFt, envelope.WingspanFt, envelope.WheelbaseFt, envelope.MainGearWidthFt));
        Assert.Equal(("CASE", "SHRT"), (envelope.Sources.LengthFt, envelope.Sources.WingspanFt));
    }

    [Fact]
    public void Build_EmptyAdg_UsesSmallestGroupCoveringSpan()
    {
        FaaAircraftRecord[] records =
        [
            Record("BLNK", "", "Fixed-wing", new Dims(LengthFt: 80.0, SpanFt: 100.0, WheelbaseFt: 30.0, GearWidthFt: null)),
        ];

        var types = DesignGroupEnvelopes.Build(records, out IReadOnlyList<string> warnings);

        DesignGroupEnvelope only = Assert.Single(types.Envelopes);
        Assert.Equal(("III", "BLNK"), (only.Group, only.Sources.LengthFt));
        Assert.DoesNotContain(warnings, w => w.Contains("BLNK", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_UnknownAdg_FallsBackAndWarns()
    {
        FaaAircraftRecord[] records =
        [
            Record("ODD1", "X", "Fixed-wing", new Dims(LengthFt: 50.0, SpanFt: 60.0, WheelbaseFt: 20.0, GearWidthFt: null)),
        ];

        var types = DesignGroupEnvelopes.Build(records, out IReadOnlyList<string> warnings);

        Assert.Equal("II", Assert.Single(types.Envelopes).Group);
        Assert.Contains("ADG 'X' on ODD1 is not I-VI; using group II from its 60 ft span", warnings);
    }

    [Fact]
    public void GroupWithNoCandidate_IsOmitted_AndWarned()
    {
        FaaAircraftRecord[] records =
        [
            Record("ONE1", "I", "Fixed-wing", new Dims(LengthFt: 30.0, SpanFt: 40.0, WheelbaseFt: 10.0, GearWidthFt: null)),
        ];

        var types = DesignGroupEnvelopes.Build(records, out IReadOnlyList<string> warnings);

        string[] omitted = ["II", "III", "IV", "V", "VI"];
        Assert.Equal("I", Assert.Single(types.Envelopes).Group);
        Assert.Equal(omitted.Select(g => $"No FAA record qualifies for design group {g}; it gets no push targets"), warnings);
        Assert.Throws<KeyNotFoundException>(() => types.FootprintOf(AirplaneDesignGroup.II));
    }

    /// <summary>
    /// Group I's greatest FAA wheelbase is B18T's 30.0 ft, measured to its tailwheel; the envelope leaves taildraggers out,
    /// so its wheelbase comes from a tricycle type, one whose wheelbase is under the taildragger share of its length.
    /// </summary>
    [Fact]
    public void Build_GroupIWheelbase_ComesFromATricycleType()
    {
        DesignGroupEnvelope groupI = DesignGroupEnvelopes.Build(PinnedRecords(), out _).Envelopes[0];
        FaaAircraftRecord source = PinnedRecords().Single(r => r.IcaoCode == groupI.Sources.WheelbaseFt);

        Assert.Equal("I", groupI.Group);
        Assert.NotEqual(("B18T", (double?)30.0), (groupI.Sources.WheelbaseFt, groupI.WheelbaseFt));
        Assert.True(
            (AircraftCategorization.Categorize(source.IcaoCode) == AircraftCategory.Jet)
                || ((source.WheelbaseFt / source.LengthFt) < TurnAboutFit.TaildraggerWheelbaseRatio),
            $"{source.IcaoCode}'s {source.WheelbaseFt} ft wheelbase is a non-jet taildragger's share of its {source.LengthFt} ft length"
        );
        Assert.Equal(("LJ40", (double?)25.0, AircraftCategory.Jet), (groupI.Sources.WheelbaseFt, groupI.WheelbaseFt, groupI.Category));
    }

    [Fact]
    public void Load_NotJson_Throws() => AssertLoadRefuses("this is not JSON", "is not an array of design group envelopes");

    [Fact]
    public void Load_NotAnArray_Throws() => AssertLoadRefuses(Envelope("I"), "is not an array of design group envelopes");

    [Fact]
    public void Load_Null_Throws() => AssertLoadRefuses("null", "holds no array");

    [Fact]
    public void Load_NullElement_Throws() => AssertLoadRefuses($"[{Envelope("I")}, null]", "null element at index 1");

    [Fact]
    public void Load_MissingField_Throws() => AssertLoadRefuses($"[{Envelope("I").Replace("\"lengthFt\": 40,", "")}]", "lengthFt");

    [Fact]
    public void Load_UnknownGroup_Throws() => AssertLoadRefuses($"[{Envelope("VII")}]", "unknown group 'VII' at index 0");

    [Fact]
    public void Load_RepeatedGroup_Throws() => AssertLoadRefuses($"[{Envelope("II")}, {Envelope("ii")}]", "repeats group II at index 1");

    private static void AssertLoadRefuses(string json, string problem)
    {
        string path = Path.Combine(Path.GetTempPath(), "design-group-envelopes-" + Guid.NewGuid() + ".json");
        File.WriteAllText(path, json);
        try
        {
            InvalidDataException ex = Assert.Throws<InvalidDataException>(() => DesignGroupEnvelopes.Load(path));
            Assert.Contains(path, ex.Message, StringComparison.Ordinal);
            Assert.Contains(problem, ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string Envelope(string group) =>
        $$$"""
            {"group": "{{{group}}}", "envelopeCode": "ADG-{{{group}}}", "lengthFt": 40, "wingspanFt": 45, "wheelbaseFt": null, "mainGearWidthFt": null,
             "category": "Piston", "sources": {"lengthFt": "AAAA", "wingspanFt": "BBBB", "wheelbaseFt": null, "mainGearWidthFt": null}}
            """;

    private static Dictionary<string, FaaAircraftRecord>.ValueCollection PinnedRecords()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "FaaAcd.json");
        Dictionary<string, FaaAircraftRecord> records =
            JsonSerializer.Deserialize<Dictionary<string, FaaAircraftRecord>>(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"Failed to deserialize {path}");
        return records.Values;
    }

    private static FaaAircraftRecord Record(string icao, string adg, string recordClass, Dims dims) =>
        new()
        {
            IcaoCode = icao,
            Adg = adg,
            Class = recordClass,
            LengthFt = dims.LengthFt,
            WingspanFtWithoutWinglets = dims.SpanFt,
            WheelbaseFt = dims.WheelbaseFt,
            MainGearWidthFt = dims.GearWidthFt,
        };

    private sealed record Dims(double? LengthFt, double? SpanFt, double? WheelbaseFt, double? GearWidthFt);
}
