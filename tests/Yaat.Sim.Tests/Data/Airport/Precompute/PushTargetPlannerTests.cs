using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;
using static Yaat.Sim.Tests.Data.Airport.Precompute.PushTargetPlannerFixtures;

namespace Yaat.Sim.Tests.Data.Airport.Precompute;

/// <summary>
/// <see cref="PushTargetPlanner.Compute"/> on the real KOAK layout and its shipped sidecar: the targets a stand offers per
/// design group, planned as a live push off the stand plans them, with the footprint of the group's envelope.
/// </summary>
public class PushTargetPlannerTests
{
    /// <summary>The shipped airport sidecars.</summary>
    internal static readonly Lazy<AirportSidecarCatalog> Sidecars = new(() =>
    {
        if (!Directory.Exists(ArtccsDir))
        {
            throw new InvalidOperationException($"The shipped sidecars are missing from the test output: {ArtccsDir}");
        }

        return new AirportSidecarCatalog(AirportSidecarLoader.LoadAll(ArtccsDir).Airports);
    });

    /// <summary>Stand loop options that plan the stands one after another.</summary>
    public static ParallelOptions Sequential => new() { MaxDegreeOfParallelism = 1 };

    public PushTargetPlannerTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Oak_Gate26_GroupIV_OffersTeAndTc()
    {
        PushTargetEntry entry = Entry("26", "IV");

        foreach (string taxiway in (string[])["TE", "TC"])
        {
            PrecomputedPushTarget target = Assert.Single(entry.Targets, t => t.Name == taxiway);
            Assert.Equal($"PUSH {taxiway}", target.Command);
            Assert.Contains(target.Note, (string[])["alongside", "across"]);
            Assert.NotEmpty(target.Moves);
            Assert.True(target.PathLengthFt > 0.0);
        }
    }

    [Fact]
    public void NoStandOrRampTargets()
    {
        Assert.NotEmpty(OakEntries.Value);
        foreach (PrecomputedPushTarget target in OakEntries.Value.SelectMany(e => e.Targets))
        {
            Assert.False(target.Name.Equals("RAMP", StringComparison.OrdinalIgnoreCase), $"{target.Command} is a ramp target");
            Assert.DoesNotContain('@', target.Command);
            Assert.True(Enum.IsDefined(target.Kind), target.Command);
            Assert.True((target.Kind != PushTargetKind.Spot) || target.Command.StartsWith("PUSH $", StringComparison.Ordinal), target.Command);
        }
    }

    [Fact]
    public void SortedByKindThenLengthThenName()
    {
        IReadOnlyList<PushTargetEntry> entries = OakEntries.Value;

        Assert.Equal(entries.OrderBy(e => e.StandName, StringComparer.Ordinal).ThenBy(e => e.DesignGroup, StringComparer.Ordinal), entries);
        Assert.Contains(entries, e => e.Targets.Count > 1);
        foreach (PushTargetEntry entry in entries)
        {
            Assert.Equal(entry.Targets.OrderBy(t => t.Kind).ThenBy(t => t.PathLengthFt).ThenBy(t => t.Name, StringComparer.Ordinal), entry.Targets);
        }
    }

    /// <summary>A degree of parallelism below one is a caller error, refused before any planning.</summary>
    [Fact]
    public void MaxDegreeOfParallelism_BelowOne_Throws()
    {
        AirportGroundLayout layout = Oak();
        var envelopes = DesignGroupEnvelopes.LoadShipped();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PushTargetPlanner.ComputeStands(layout, envelopes, Sidecars.Value, Gate26, new ParallelOptions())
        );
        Assert.Throws<ArgumentOutOfRangeException>(() => PushTargetPlanner.Compute(layout, envelopes, Sidecars.Value, new ParallelOptions()));
    }

    /// <summary>
    /// Gate 26's group-IV entry as indented JSON, compared byte for byte with the committed golden, so a change to any
    /// stored figure (a plan, a rounding, a field) shows in review. On a difference the computed text is written under
    /// <c>.tmp/</c>.
    /// </summary>
    [Fact]
    public void Gate26_GroupIV_MatchesCommittedGolden()
    {
        string repoRoot = TickRecorder.FindRepoRoot();
        string goldenPath = Path.Combine(repoRoot, "tests", "Yaat.Sim.Tests", "TestData", "Precompute", "oak-gate26-adg-iv.json");
        byte[] actual = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Entry("26", "IV"), IndentedOptions) + "\n");

        if (!File.Exists(goldenPath) || !File.ReadAllBytes(goldenPath).AsSpan().SequenceEqual(actual))
        {
            string actualPath = Path.Combine(repoRoot, ".tmp", "oak-gate26-adg-iv.actual.json");
            Directory.CreateDirectory(Path.GetDirectoryName(actualPath)!);
            File.WriteAllBytes(actualPath, actual);
            Assert.Fail($"Gate 26's group-IV entry differs from {goldenPath}; the computed entry is at {actualPath}");
        }
    }

    [Fact]
    public void Facings_AreTrueBearings()
    {
        AirportGroundLayout layout = Oak();
        GroundNode gate = Stand(layout, "26");
        GroundNode exit = layout.FindExitByTaxiway(gate.Position, "TE") ?? throw new InvalidOperationException("No exit onto TE from OAK gate 26");
        GroundEdge edge = exit.Edges.OfType<GroundEdge>().First(e => e.MatchesTaxiway("TE"));
        double trueBearing = GeoMath.BearingTo(exit.Position, FirstPointAlong(layout, edge, exit));
        double magnetic = MagneticDeclination.TrueToMagnetic(trueBearing, exit.Position);

        PrecomputedPushTarget te = Assert.Single(Entry("26", "IV").Targets, t => t.Name == "TE");

        Assert.Equal(2, te.Facings.Count);
        Assert.Equal(180.0, te.Facings[1] - te.Facings[0], 1);
        Assert.Contains(te.Facings, f => new TrueHeading(f).AbsAngleTo(new TrueHeading(trueBearing)) < 0.06);
        Assert.DoesNotContain(te.Facings, f => new TrueHeading(f).AbsAngleTo(new TrueHeading(magnetic)) < 1.0);
        Assert.All(OakEntries.Value.SelectMany(e => e.Targets).Where(t => t.Kind == PushTargetKind.Spot), t => Assert.Empty(t.Facings));
    }

    /// <summary>The plan a live push with the group-IV envelope's footprint makes, its request built by hand, is the stored one.</summary>
    [Fact]
    public void Plan_MatchesLivePushForGroupEnvelope()
    {
        AirportGroundLayout layout = Oak();
        GroundNode gate = Stand(layout, "26");
        var live = new TugRequest
        {
            Start = new PushbackPose(gate.Position, gate.TrueHeading!.Value.Degrees),
            StartsAtStand = true,
            StandName = null,
            Footprint = DesignGroupEnvelopes.LoadShipped().FootprintOf(AirplaneDesignGroup.IV),
            MovementArea = Classification(layout),
            Goals = [StraightBack(layout, gate, "TE")],
            ParkedNeighbours = [],
            FinalFacingTrueDeg = null,
            PreviousKind = null,
            Forced = false,
        };
        TugPlan? plan = TugMovePlanner.Plan(layout, live, out string refusal);
        Assert.True(plan is not null, refusal);

        PrecomputedPushTarget stored = Assert.Single(Entry("26", "IV").Targets, t => t.Name == "TE");
        Assert.Equal(plan.Moves.Select(PushMoveEntry.From), stored.Moves);
        Assert.Equal(Math.Round(plan.Moves.Sum(m => m.PathLengthFt), 1), stored.PathLengthFt);
    }

    /// <summary>
    /// Three times the group-I envelope's 63.8 ft length is under the 700 ft floor of the cap, so gate 26 keeps group I's
    /// TE (about 227 ft) and TC (about 546 ft) and nothing longer than 700 ft.
    /// </summary>
    [Fact]
    public void Gate26_GroupI_KeepsTeAndTcUnderThe700FtFloor()
    {
        PushTargetEntry entry = Entry("26", "I");

        Assert.Equal(700.0, PushTargetPlanner.PathCapFt(DesignGroupEnvelopes.LoadShipped().FootprintOf(AirplaneDesignGroup.I)));
        Assert.Contains(entry.Targets, t => t.Name == "TE");
        Assert.Contains(entry.Targets, t => t.Name == "TC");
        Assert.All(entry.Targets, t => Assert.True(t.PathLengthFt <= 700.0, $"{t.Command} is {t.PathLengthFt} ft, over 700 ft"));
    }

    /// <summary>
    /// KOAK stand 5 (heading 144° true) offers a group-III aircraft PUSH S with both facings (YAAT-1530: SCX8607, a B738,
    /// was offered no push target). Its bare push runs 665 ft, under the 700 ft floor of the cap.
    /// </summary>
    [Fact]
    public void Oak_Stand5_GroupIII_OffersPushS()
    {
        PushTargetEntry entry = Assert.IsType<PushTargetEntry>(
            PushTargetPlanner.ComputeStand(Oak(), DesignGroupEnvelopes.LoadShipped(), Sidecars.Value, "5", AirplaneDesignGroup.III)
        );

        PrecomputedPushTarget pushS = Assert.Single(entry.Targets, t => t.Command == "PUSH S");
        Assert.Equal(2, pushS.Facings.Count);
    }

    /// <summary>
    /// KOAK stand 4's group-I <c>PUSH TE</c> plans at about 658 ft, between the old 600 ft floor and the 700 ft one, so the
    /// floor of the cap keeps it.
    /// </summary>
    [Fact]
    public void Oak_Stand4_GroupI_KeepsTeUnderThe700FtFloor()
    {
        PushTargetEntry entry = Assert.IsType<PushTargetEntry>(
            PushTargetPlanner.ComputeStand(Oak(), DesignGroupEnvelopes.LoadShipped(), Sidecars.Value, "4", AirplaneDesignGroup.I)
        );

        PrecomputedPushTarget te = Assert.Single(entry.Targets, t => t.Command == "PUSH TE");
        Assert.InRange(te.PathLengthFt, 600.0, 700.0);
    }

    /// <summary>A stand with no heading has no pose to push from, so it gets no entry at all.</summary>
    [Fact]
    public void HeadinglessStand_IsSkipped()
    {
        AirportGroundLayout layout = Oak();
        GroundNode gate = Stand(layout, "26");
        JsonNode json = JsonNode.Parse(GroundLayoutSerializer.Serialize(layout))!;
        json["Nodes"]![gate.Id.ToString(CultureInfo.InvariantCulture)]!["TrueHeading"] = null;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json.ToJsonString()));
        AirportGroundLayout headingless = GroundLayoutSerializer.Deserialize(stream, layout.AirportId);
        Assert.Null(headingless.Nodes[gate.Id].TrueHeading);

        Assert.Empty(PushTargetPlanner.ComputeStands(headingless, DesignGroupEnvelopes.LoadShipped(), Sidecars.Value, Gate26, Sequential));
        Assert.NotEmpty(Entry("26", "IV").Targets);
    }

    internal static AirportGroundLayout Sfo() =>
        new TestAirportGroundData().GetLayout("KSFO")
        ?? throw new InvalidOperationException("The KSFO test layout (tests/Yaat.Sim.Tests/TestData/sfo.geojson) is missing.");

    internal static AirportGroundLayout Oak() =>
        new TestAirportGroundData().GetLayout("KOAK")
        ?? throw new InvalidOperationException("The KOAK test layout (tests/Yaat.Sim.Tests/TestData/oak.geojson) is missing.");

    internal static string Json(object value) => JsonSerializer.Serialize(value, GroundLayoutSerializer.Options);

    internal static Dictionary<string, FaaAircraftRecord> PinnedRecords()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "FaaAcd.json");
        return JsonSerializer.Deserialize<Dictionary<string, FaaAircraftRecord>>(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"Failed to deserialize {path}");
    }
}
