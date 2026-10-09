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

namespace Yaat.Sim.Tests.Data.Airport.Precompute;

/// <summary>
/// <see cref="PushTargetPlanner.Compute"/> on the real KOAK layout and its shipped sidecar: the targets a stand offers per
/// design group, planned as a live push off the stand plans them, with the footprint of the group's envelope.
/// </summary>
public class PushTargetPlannerTests
{
    private const string GeoJsonMd5 = "5d41402abc4b2a76b9719d911017c592";

    /// <summary>KOAK's GA20, a stand whose aircraft park facing their way out (parking node 689 of the test layout).</summary>
    private const int Ga20NodeId = 689;

    /// <summary>The ARTCCs folder both the sidecar catalog and <see cref="AirportSidecarHash.For"/> read.</summary>
    private static readonly string ArtccsDir = Path.Combine(AppContext.BaseDirectory, "Data", "ARTCCs");

    /// <summary>The shipped airport sidecars.</summary>
    internal static readonly Lazy<AirportSidecarCatalog> Sidecars = new(() =>
    {
        if (!Directory.Exists(ArtccsDir))
        {
            throw new InvalidOperationException($"The shipped sidecars are missing from the test output: {ArtccsDir}");
        }

        return new AirportSidecarCatalog(AirportSidecarLoader.LoadAll(ArtccsDir).Airports);
    });

    /// <summary>A whole airport takes minutes, so the tests compute gate 26 and the two stands nearest it.</summary>
    private static readonly Lazy<IReadOnlySet<string>> SampleStands = new(() =>
    {
        AirportGroundLayout layout = Oak();
        GroundNode gate = Stand(layout, "26");
        return layout
            .Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && (n.Name is not null))
            .OrderBy(n => GeoMath.DistanceNm(gate.Position, n.Position))
            .ThenBy(n => n.Id)
            .Take(3)
            .Select(n => n.Name!)
            .ToHashSet(StringComparer.Ordinal);
    });

    /// <summary>Stand loop options that plan the stands one after another.</summary>
    public static ParallelOptions Sequential => new() { MaxDegreeOfParallelism = 1 };

    private static readonly Lazy<IReadOnlyList<PushTargetEntry>> OakEntries = new(() =>
        PushTargetPlanner.ComputeStands(Oak(), DesignGroupEnvelopes.LoadShipped(), Sidecars.Value, SampleStands.Value, Sequential)
    );

    private static readonly IReadOnlySet<string> Gate26 = new HashSet<string>(StringComparer.Ordinal) { "26" };

    /// <summary>The golden's options: the stored ones, indented with LF line ends so a review diff shows one field a line.</summary>
    private static readonly JsonSerializerOptions IndentedOptions = new(GroundLayoutSerializer.Options) { WriteIndented = true, NewLine = "\n" };

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
    public void RefusedTargetsAreOmitted()
    {
        AirportGroundLayout layout = Oak();
        GroundNode gate = Stand(layout, "26");
        AircraftFootprint footprint = DesignGroupEnvelopes.LoadShipped().FootprintOf(AirplaneDesignGroup.IV);
        List<UncappedPlan> plans = UncappedPlans(layout, gate, footprint, Classification(layout));
        List<UncappedPlan> taxiways = [.. plans.Where(p => p.Kind != PushTargetKind.Spot)];

        Assert.Contains(taxiways, p => p.PathLengthFt is null);
        Assert.Contains(taxiways, p => p.PathLengthFt is not null);

        List<string> offered = [.. Entry("26", "IV").Targets.Where(t => t.Kind != PushTargetKind.Spot).Select(t => t.Name)];
        Assert.All(taxiways.Where(p => p.PathLengthFt is null), p => Assert.DoesNotContain(p.Name, offered));
        Assert.All(offered, name => Assert.Contains(taxiways, p => (p.Name == name) && (p.PathLengthFt is not null)));
        double capFt = PushTargetPlanner.PathCapFt(footprint);
        Assert.All(
            taxiways.Where(p => (p.PathLengthFt is not null) && !offered.Contains(p.Name)),
            p => Assert.True(p.PathLengthFt > capFt, $"PUSH {p.Name} was accepted at {p.PathLengthFt} ft, under the cap, yet not offered")
        );
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

    [Fact]
    public void Compute_IsByteIdenticalAcrossRuns()
    {
        AirportGroundLayout layout = Oak();
        IReadOnlyList<PushTargetEntry> second = PushTargetPlanner.ComputeStands(
            layout,
            DesignGroupEnvelopes.LoadShipped(),
            Sidecars.Value,
            SampleStands.Value,
            Sequential
        );
        string firstRoot = NewRoot();
        string secondRoot = NewRoot();

        try
        {
            var key = PrecomputeKey.Current(GeoJsonMd5, 12_345, AirportSidecarHash.For(ArtccsDir, "KOAK"));
            var firstStore = new PrecomputeStore(firstRoot);
            var secondStore = new PrecomputeStore(secondRoot);
            firstStore.Write(new PrecomputeEntry(layout.AirportId, key, layout, OakEntries.Value));
            secondStore.Write(new PrecomputeEntry(layout.AirportId, key, layout, second));

            Assert.Equal(File.ReadAllBytes(firstStore.PathFor(layout.AirportId)), File.ReadAllBytes(secondStore.PathFor(layout.AirportId)));
        }
        finally
        {
            DeleteRoot(firstRoot);
            DeleteRoot(secondRoot);
        }
    }

    /// <summary>
    /// The parallel planner writes byte-identical store output to the sequential one: the same targets for every stand,
    /// whatever the degree of parallelism and whatever order the stands finish in. The stand set is the eight named
    /// parking nodes nearest the airport's reference gate, so a run is cheap and yet wide enough for the stands to
    /// overlap. Two parallel runs, because a race need not show on every one.
    /// </summary>
    [Theory]
    [InlineData("KOAK")]
    [InlineData("KSFO")]
    public void Parallel_MatchesSequential_ByteForByte(string airport)
    {
        AirportGroundLayout layout = airport == "KOAK" ? Oak() : Sfo();
        GroundNode reference = Stand(layout, airport == "KOAK" ? "26" : "D5");
        IReadOnlySet<string> stands = NearestStandNames(layout, reference, 8);
        var envelopes = DesignGroupEnvelopes.LoadShipped();
        IReadOnlyList<PushTargetEntry> sequential = PushTargetPlanner.ComputeStands(layout, envelopes, Sidecars.Value, stands, Sequential);
        var key = PrecomputeKey.Current(GeoJsonMd5, 12_345, AirportSidecarHash.For(ArtccsDir, airport));
        string sequentialRoot = NewRoot();
        string[] parallelRoots = [NewRoot(), NewRoot()];

        try
        {
            var sequentialStore = new PrecomputeStore(sequentialRoot);
            sequentialStore.Write(new PrecomputeEntry(layout.AirportId, key, layout, sequential));
            byte[] expected = File.ReadAllBytes(sequentialStore.PathFor(layout.AirportId));

            foreach (string root in parallelRoots)
            {
                IReadOnlyList<PushTargetEntry> parallel = PushTargetPlanner.ComputeStands(
                    layout,
                    envelopes,
                    Sidecars.Value,
                    stands,
                    new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }
                );
                var store = new PrecomputeStore(root);
                store.Write(new PrecomputeEntry(layout.AirportId, key, layout, parallel));
                Assert.Equal(expected, File.ReadAllBytes(store.PathFor(layout.AirportId)));
            }
        }
        finally
        {
            DeleteRoot(sequentialRoot);
            foreach (string root in parallelRoots)
            {
                DeleteRoot(root);
            }
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
            Start = new TugPose(gate.Position, gate.TrueHeading!.Value.Degrees),
            StartsAtStand = true,
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
    /// KOAK's sidecar names no movement-area or non-movement lanes, so both verdicts are the derived ones. TE is the terminal
    /// taxilane behind gate 26 (non-movement); F is the movement-area taxiway beside stand OLD1, and the shortest push from
    /// OLD1 for group IV (354 ft), so it survives the length cap. Both were looked up once with the classifier. OLD1 is
    /// already push-back by its geometry and the catalog passed here has no area rule, so its push-back override changes
    /// nothing today; it stays as a guard that keeps the test on its target kinds whatever OLD1's geometry or the shipped
    /// rules say.
    /// </summary>
    [Fact]
    public void Kind_FollowsMovementArea()
    {
        var old1PushBack = new AirportSidecarCatalog([
            new AirportSidecar("KOAK")
            {
                StandDepartureOverrides = new Dictionary<string, StandDeparture>(StringComparer.OrdinalIgnoreCase)
                {
                    ["OLD1"] = StandDeparture.PushBack,
                },
            },
        ]);
        IReadOnlyList<PushTargetEntry> old1 = PushTargetPlanner.ComputeStands(
            Oak(),
            DesignGroupEnvelopes.LoadShipped(),
            old1PushBack,
            new HashSet<string>(StringComparer.Ordinal) { "OLD1" },
            Sequential
        );

        Assert.Equal(PushTargetKind.Taxilane, Assert.Single(Entry("26", "IV").Targets, t => t.Name == "TE").Kind);
        PushTargetEntry old1Iv = Assert.Single(old1, e => e.DesignGroup == "IV");
        Assert.Equal(PushTargetKind.Taxiway, Assert.Single(old1Iv.Targets, t => t.Name == "F").Kind);
    }

    /// <summary>
    /// The plans follow the sidecar catalog passed in, not the navigation database's. KSFO's shipped sidecar lists no
    /// movement-area lane, so T5A (gate D5 hangs off it) is a ramp taxilane in the global catalog; the catalog passed makes
    /// it movement area. Every target stored for D5 is a plan made under the passed catalog, every plan under it within the
    /// length cap is stored, and the passed catalog changes at least one of D5's plans, so a planner reading the global
    /// catalog fails here.
    /// </summary>
    [Fact]
    public void Compute_PlansWithThePassedSidecars()
    {
        AirportGroundLayout sfo = Sfo();
        var passed = new AirportSidecarCatalog([new AirportSidecar("KSFO") { MovementAreaTaxiways = ["T5A"] }]);
        var passedClassification = MovementAreaClassification.Build(sfo, passed);
        var globalClassification = MovementAreaClassification.For(sfo);
        Assert.True(passedClassification.IsMovementArea("T5A"), "the passed catalog does not make T5A movement area");
        Assert.False(globalClassification.IsMovementArea("T5A"), "the global catalog already makes T5A movement area");
        var envelopes = DesignGroupEnvelopes.LoadShipped();
        GroundNode d5 = Stand(sfo, "D5");

        IReadOnlyList<PushTargetEntry> entries = PushTargetPlanner.ComputeStands(
            sfo,
            envelopes,
            passed,
            new HashSet<string>(StringComparer.Ordinal) { "D5" },
            Sequential
        );

        bool differs = false;
        foreach (PushTargetEntry entry in entries)
        {
            AircraftFootprint footprint = DesignGroupEnvelopes.FootprintOf(envelopes.Envelopes.Single(e => e.Group == entry.DesignGroup));
            double capFt = PushTargetPlanner.PathCapFt(footprint);
            List<UncappedPlan> underPassed = UncappedPlans(sfo, d5, footprint, passedClassification);
            differs |= !underPassed.SequenceEqual(UncappedPlans(sfo, d5, footprint, globalClassification));
            Assert.All(
                entry.Targets,
                t =>
                    Assert.True(
                        underPassed.Any(p => (p.Command == t.Command) && (p.PathLengthFt == t.PathLengthFt)),
                        $"group {entry.DesignGroup}: {t.Command} ({t.PathLengthFt} ft) is not the plan under the passed catalog"
                    )
            );
            Assert.All(
                underPassed.Where(p => p.PathLengthFt <= capFt),
                p => Assert.True(entry.Targets.Any(t => t.Command == p.Command), $"group {entry.DesignGroup}: {p.Command} is not stored")
            );
        }

        Assert.True(differs, "The passed catalog changes none of D5's plans, so this test cannot tell the two catalogs apart");
    }

    /// <summary>
    /// A target the planner accepts but whose path is over the cap (<see cref="PushTargetPlanner.PathCapFt"/>) is a tow-out
    /// and is not stored, while the stand's shorter targets are.
    /// </summary>
    [Fact]
    public void LongTowOuts_AreDropped()
    {
        AirportGroundLayout layout = Oak();
        var envelopes = DesignGroupEnvelopes.LoadShipped();
        bool found = false;

        foreach (string standName in SampleStands.Value.Order(StringComparer.Ordinal))
        {
            GroundNode stand = Stand(layout, standName);
            foreach (DesignGroupEnvelope envelope in envelopes.Envelopes)
            {
                AircraftFootprint footprint = DesignGroupEnvelopes.FootprintOf(envelope);
                double capFt = PushTargetPlanner.PathCapFt(footprint);
                List<UncappedPlan> accepted =
                [
                    .. UncappedPlans(layout, stand, footprint, Classification(layout)).Where(p => p.PathLengthFt is not null),
                ];
                if (!accepted.Any(p => p.PathLengthFt > capFt) || !accepted.Any(p => p.PathLengthFt <= capFt))
                {
                    continue;
                }

                found = true;
                List<string> stored = [.. Entry(standName, envelope.Group).Targets.Select(t => t.Command)];
                Assert.Equal(
                    accepted.Where(p => p.PathLengthFt <= capFt).Select(p => p.Command).Order(StringComparer.Ordinal),
                    stored.Order(StringComparer.Ordinal)
                );
            }
        }

        Assert.True(found, "No sample stand and group has both a target over the length cap and one under it");
    }

    /// <summary>
    /// Three times the group-I envelope's 63.8 ft length is under the 600 ft floor of the cap, so gate 26 keeps group I's
    /// TE (about 227 ft) and TC (about 546 ft) and nothing longer than 600 ft.
    /// </summary>
    [Fact]
    public void Gate26_GroupI_KeepsTeAndTcUnderThe600FtFloor()
    {
        PushTargetEntry entry = Entry("26", "I");

        Assert.Equal(600.0, PushTargetPlanner.PathCapFt(DesignGroupEnvelopes.LoadShipped().FootprintOf(AirplaneDesignGroup.I)));
        Assert.Contains(entry.Targets, t => t.Name == "TE");
        Assert.Contains(entry.Targets, t => t.Name == "TC");
        Assert.All(entry.Targets, t => Assert.True(t.PathLengthFt <= 600.0, $"{t.Command} is {t.PathLengthFt} ft, over 600 ft"));
    }

    /// <summary>
    /// No footprint corner of any sample of any plan the planner accepts, from the three stands nearest a runway holding
    /// position at KOAK and at KSFO, lies nearer a runway's centreline than that runway's nearest hold-short node (AIM
    /// 2-3-5.a.1: no part of the aircraft past the hold line). At least one checked corner lies within 200 ft of a hold, so
    /// the check can fail.
    /// </summary>
    [Theory]
    [InlineData("KOAK")]
    [InlineData("KSFO")]
    public void NoTargetPathPassesARunwayHoldShort(string airport)
    {
        AirportGroundLayout layout = airport == "KOAK" ? Oak() : Sfo();
        List<RunwayLine> runways = RunwayLines(layout);
        List<RunwayHold> holds = Holds(layout, runways);
        List<(RunwayLine Runway, double HoldFt)> closest = [.. holds.GroupBy(h => h.Runway).Select(g => (g.Key, g.Min(h => h.HoldFt)))];
        Assert.NotEmpty(runways);
        Assert.NotEmpty(holds);
        var envelopes = DesignGroupEnvelopes.LoadShipped();
        MovementAreaClassification classification = Classification(layout);
        List<GroundNode> stands =
        [
            .. layout
                .Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && (n.Name is not null) && (n.TrueHeading is not null))
                .OrderBy(n => holds.Min(h => DistanceFt(h.Hold.Position, n.Position)))
                .ThenBy(n => n.Id)
                .Take(3),
        ];
        double nearestHoldFt = double.PositiveInfinity;

        foreach (GroundNode stand in stands)
        {
            foreach (DesignGroupEnvelope envelope in envelopes.Envelopes)
            {
                AircraftFootprint footprint = DesignGroupEnvelopes.FootprintOf(envelope);
                foreach ((string command, TugPlan plan) in AcceptedPlans(layout, stand, footprint, classification))
                {
                    foreach (LatLon corner in plan.Moves.SelectMany(m => m.Samples).SelectMany(s => Corners(s, footprint)))
                    {
                        nearestHoldFt = Math.Min(nearestHoldFt, holds.Min(h => DistanceFt(h.Hold.Position, corner)));
                        foreach ((RunwayLine runway, double holdFt) in closest)
                        {
                            Assert.True(
                                runway.DistanceFt(corner) > holdFt,
                                $"{command} from stand {stand.Name} (group {envelope.Group}) puts a corner at {corner.Lat},{corner.Lon}, "
                                    + $"{runway.DistanceFt(corner):F0} ft from {runway.Name}, within its {holdFt:F0} ft hold"
                            );
                        }
                    }
                }
            }
        }

        Assert.True(nearestHoldFt <= 200.0, $"No checked corner comes within 200 ft of a hold (nearest {nearestHoldFt:F0} ft)");
    }

    /// <summary>
    /// SFO's layout carries stand <c>2-2B</c> on two nodes: it gets one entry per group, planned from the lower node id,
    /// with a warning.
    /// </summary>
    [Fact]
    public void DuplicateStandName_UsesFirstNodeAndWarns()
    {
        AirportGroundLayout sfo = Sfo();
        List<GroundNode> nodes = [.. sfo.Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && (n.Name == "2-2B")).OrderBy(n => n.Id)];
        Assert.Equal(2, nodes.Count);
        var capture = WarningLogCapture.Install();
        var envelopes = DesignGroupEnvelopes.LoadShipped();

        IReadOnlyList<PushTargetEntry> entries = PushTargetPlanner.ComputeStands(
            sfo,
            envelopes,
            Sidecars.Value,
            new HashSet<string>(StringComparer.Ordinal) { "2-2B" },
            Sequential
        );

        Assert.Equal(envelopes.Envelopes.Select(e => e.Group), entries.Select(e => e.DesignGroup));
        Assert.Contains($"Stand name 2-2B at {sfo.AirportId} is used by 2 nodes; push targets use node {nodes[0].Id}", capture.Warnings);
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

    /// <summary>
    /// GA20's aircraft park facing the taxiway they leave by: the layout stores it as a taxi-out stand, and it gets an empty
    /// target list for every group (it once kept <c>PUSH L</c>, a 1,965 ft tow).
    /// </summary>
    [Fact]
    public void Ga20_IsTaxiOutStand_HasNoTargets()
    {
        AirportGroundLayout layout = Oak();
        GroundNode ga20 = layout.Nodes[Ga20NodeId];
        Assert.Equal((GroundNodeType.Parking, "GA20"), (ga20.Type, ga20.Name));

        IReadOnlyList<PushTargetEntry> entries = PushTargetPlanner.ComputeStands(
            layout,
            DesignGroupEnvelopes.LoadShipped(),
            Sidecars.Value,
            new HashSet<string>(StringComparer.Ordinal) { "GA20" },
            Sequential
        );

        Assert.Equal(StandDeparture.TaxiOut, ga20.StandDeparture);
        Assert.Equal(StandDeparture.TaxiOut, StandDepartures.StandDepartureOf(layout, ga20, Sidecars.Value));
        Assert.Equal(DesignGroupEnvelopes.LoadShipped().Envelopes.Select(e => e.Group), entries.Select(e => e.DesignGroup));
        Assert.All(entries, e => Assert.Empty(e.Targets));
    }

    /// <summary>
    /// MTN1 is an either stand with the shipped sidecars: its targets are planned exactly as they are when it is named a
    /// push-back stand, and at least one group has some. The two match only while KOAK's shipped sidecar names no
    /// movement-area or non-movement lanes; once it names one, the push-back comparison catalog must carry it too.
    /// </summary>
    [Fact]
    public void Planner_EitherStand_PlansPushTargets()
    {
        AirportGroundLayout layout = Oak();
        var mtn1 = new HashSet<string>(StringComparer.Ordinal) { "MTN1" };
        var pushBack = new AirportSidecarCatalog([
            new AirportSidecar("KOAK")
            {
                StandDepartureOverrides = new Dictionary<string, StandDeparture>(StringComparer.OrdinalIgnoreCase)
                {
                    ["MTN1"] = StandDeparture.PushBack,
                },
            },
        ]);

        IReadOnlyList<PushTargetEntry> either = PushTargetPlanner.ComputeStands(
            layout,
            DesignGroupEnvelopes.LoadShipped(),
            Sidecars.Value,
            mtn1,
            Sequential
        );
        IReadOnlyList<PushTargetEntry> pushed = PushTargetPlanner.ComputeStands(
            layout,
            DesignGroupEnvelopes.LoadShipped(),
            pushBack,
            mtn1,
            Sequential
        );

        Assert.Equal(StandDeparture.Either, StandDepartures.StandDepartureOf(layout, Stand(layout, "MTN1"), Sidecars.Value));
        Assert.Contains(either, e => e.Targets.Count > 0);
        Assert.Equal(Json(pushed), Json(either));
    }

    /// <summary>
    /// OLD1 reads push-back by its geometry, but the shipped sidecar's North Field area rule makes it a taxi-out stand, so
    /// it gets an empty target list for every group.
    /// </summary>
    [Fact]
    public void Planner_AreaTaxiOutStand_HasEmptyTargets()
    {
        AirportGroundLayout layout = Oak();
        GroundNode old1 = Stand(layout, "OLD1");

        IReadOnlyList<PushTargetEntry> entries = PushTargetPlanner.ComputeStands(
            layout,
            DesignGroupEnvelopes.LoadShipped(),
            Sidecars.Value,
            new HashSet<string>(StringComparer.Ordinal) { "OLD1" },
            Sequential
        );

        Assert.Equal(StandDeparture.PushBack, old1.StandDeparture);
        Assert.Equal(StandDeparture.TaxiOut, StandDepartures.StandDepartureOf(layout, old1, Sidecars.Value));
        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.Empty(e.Targets));
    }

    /// <summary>Gate 26 is an airline gate, parked nose-in and pushed back.</summary>
    [Fact]
    public void Gate26_IsPushBackStand()
    {
        AirportGroundLayout layout = Oak();
        GroundNode gate = Stand(layout, "26");

        Assert.Equal(StandDeparture.PushBack, gate.StandDeparture);
        Assert.Equal(StandDeparture.PushBack, StandDepartures.StandDepartureOf(layout, gate, Sidecars.Value));
    }

    /// <summary>
    /// A sidecar override wins over the geometry both ways: gate 26 made taxi-out loses its targets, GA20 made push-back
    /// reads as push-back.
    /// </summary>
    [Fact]
    public void StandDepartureOverride_FlipsAStandBothWays()
    {
        AirportGroundLayout layout = Oak();
        var flipped = new AirportSidecarCatalog([
            new AirportSidecar("KOAK")
            {
                StandDepartureOverrides = new Dictionary<string, StandDeparture>(StringComparer.OrdinalIgnoreCase)
                {
                    ["26"] = StandDeparture.TaxiOut,
                    ["ga20"] = StandDeparture.PushBack,
                },
            },
        ]);

        Assert.Equal(StandDeparture.TaxiOut, StandDepartures.StandDepartureOf(layout, Stand(layout, "26"), flipped));
        Assert.Equal(StandDeparture.PushBack, StandDepartures.StandDepartureOf(layout, layout.Nodes[Ga20NodeId], flipped));
        IReadOnlyList<PushTargetEntry> gate26 = PushTargetPlanner.ComputeStands(
            layout,
            DesignGroupEnvelopes.LoadShipped(),
            flipped,
            Gate26,
            Sequential
        );
        Assert.NotEmpty(gate26);
        Assert.All(gate26, e => Assert.Empty(e.Targets));
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

    private static PushTargetEntry Entry(string stand, string group) =>
        Assert.Single(OakEntries.Value, e => (e.StandName == stand) && (e.DesignGroup == group));

    private static MovementAreaClassification Classification(AirportGroundLayout layout) => MovementAreaClassification.Build(layout, Sidecars.Value);

    private static GroundNode Stand(AirportGroundLayout layout, string name) =>
        layout.Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && (n.Name == name)).OrderBy(n => n.Id).First();

    /// <summary>The <paramref name="count"/> named parking nodes nearest <paramref name="reference"/>, nearest first then by node id.</summary>
    private static IReadOnlySet<string> NearestStandNames(AirportGroundLayout layout, GroundNode reference, int count) =>
        layout
            .Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && (n.Name is not null))
            .OrderBy(n => GeoMath.DistanceNm(reference.Position, n.Position))
            .ThenBy(n => n.Id)
            .Take(count)
            .Select(n => n.Name!)
            .ToHashSet(StringComparer.Ordinal);

    private static TugPose StartOf(GroundNode stand) => new(stand.Position, stand.TrueHeading!.Value.Degrees);

    private static TugGoal StraightBack(AirportGroundLayout layout, GroundNode stand, string taxiway) =>
        TugGoal.StraightBackTo(
            layout.FindExitByTaxiway(stand.Position, taxiway) ?? throw new InvalidOperationException($"No exit onto {taxiway}"),
            taxiway
        );

    /// <summary>
    /// Every bare taxiway and spot candidate of <paramref name="stand"/>, planned straight through the tug planner with no
    /// length cap: the path length, rounded as stored, or null when the planner refuses it.
    /// </summary>
    private static List<UncappedPlan> UncappedPlans(
        AirportGroundLayout layout,
        GroundNode stand,
        AircraftFootprint footprint,
        MovementAreaClassification classification
    ) =>
        [
            .. Candidates(layout, stand)
                .Select(g =>
                {
                    TugPlan? plan = TugMovePlanner.Plan(
                        layout,
                        PushTargetPlanner.RequestFor(StartOf(stand), footprint, classification, g.Goal),
                        out _
                    );
                    double? lengthFt = plan is null ? null : Math.Round(plan.Moves.Sum(m => m.PathLengthFt), 1);
                    return new UncappedPlan(g.Kind, g.Name, CommandOf(g.Kind, g.Name), lengthFt);
                }),
        ];

    /// <summary>Every candidate of <paramref name="stand"/> the tug planner accepts, with its plan, uncapped.</summary>
    private static List<(string Command, TugPlan Plan)> AcceptedPlans(
        AirportGroundLayout layout,
        GroundNode stand,
        AircraftFootprint footprint,
        MovementAreaClassification classification
    )
    {
        var accepted = new List<(string Command, TugPlan Plan)>();
        foreach ((PushTargetKind kind, string name, TugGoal goal) in Candidates(layout, stand))
        {
            if (TugMovePlanner.Plan(layout, PushTargetPlanner.RequestFor(StartOf(stand), footprint, classification, goal), out _) is { } plan)
            {
                accepted.Add((CommandOf(kind, name), plan));
            }
        }

        return accepted;
    }

    private static List<(PushTargetKind Kind, string Name, TugGoal Goal)> Candidates(AirportGroundLayout layout, GroundNode stand)
    {
        var goals = new List<(PushTargetKind Kind, string Name, TugGoal Goal)>();
        IEnumerable<string> taxiways = layout
            .Edges.OfType<GroundEdge>()
            .Where(e => !e.IsRamp && !e.IsRunwayCenterline && !e.IsRunwayCrossingLink)
            .Where(e => e.Nodes.Any(n => DistanceFt(stand.Position, n.Position) <= PushTargetPlanner.CandidateSearchFt))
            .Select(e => e.TaxiwayName)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string taxiway in taxiways)
        {
            if (layout.FindExitByTaxiway(stand.Position, taxiway) is { } exit)
            {
                goals.Add((PushTargetKind.Taxiway, taxiway, TugGoal.StraightBackTo(exit, taxiway)));
            }
        }

        IEnumerable<string> spots = layout
            .Nodes.Values.Where(n => (n.Type == GroundNodeType.Spot) && !string.IsNullOrEmpty(n.Name))
            .Where(n => DistanceFt(stand.Position, n.Position) <= PushTargetPlanner.CandidateSearchFt)
            .Select(n => n.Name!)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string spot in spots)
        {
            if (layout.FindSpotNodeByName(spot) is { } node)
            {
                goals.Add((PushTargetKind.Spot, spot, TugGoal.Spot(node)));
            }
        }

        return goals;
    }

    private static string CommandOf(PushTargetKind kind, string name) => kind == PushTargetKind.Spot ? $"PUSH ${name}" : $"PUSH {name}";

    /// <summary>The four corners of the footprint (length × wingspan, centred on the sample) at one flown sample.</summary>
    private static IEnumerable<LatLon> Corners(TugPose sample, AircraftFootprint footprint)
    {
        double noseRad = sample.NoseTrueDeg * Math.PI / 180.0;
        double feetPerDegree = 60.0 * GeoMath.FeetPerNm;
        foreach (double along in (double[])[footprint.LengthFt / 2.0, -footprint.LengthFt / 2.0])
        {
            foreach (double across in (double[])[footprint.WingspanFt / 2.0, -footprint.WingspanFt / 2.0])
            {
                double eastFt = (along * Math.Sin(noseRad)) + (across * Math.Cos(noseRad));
                double northFt = (along * Math.Cos(noseRad)) - (across * Math.Sin(noseRad));
                yield return new LatLon(
                    sample.Position.Lat + (northFt / feetPerDegree),
                    sample.Position.Lon + (eastFt / (feetPerDegree * Math.Cos(sample.Position.Lat * Math.PI / 180.0)))
                );
            }
        }
    }

    private static List<RunwayLine> RunwayLines(AirportGroundLayout layout) =>
        [
            .. layout
                .Edges.Where(e => e.IsRunwayCenterline)
                .GroupBy(e => e.TaxiwayName, StringComparer.OrdinalIgnoreCase)
                .Select(g => new RunwayLine(g.Key, [.. g.Select(EdgePoints)])),
        ];

    /// <summary>Each runway holding position, with the runway whose centreline it lies nearest and how far from it, feet.</summary>
    private static List<RunwayHold> Holds(AirportGroundLayout layout, List<RunwayLine> runways) =>
        [
            .. layout
                .Nodes.Values.Where(n => n.Type == GroundNodeType.RunwayHoldShort)
                .Select(n => (Node: n, Runway: runways.MinBy(r => r.DistanceFt(n.Position))!))
                .Select(h => new RunwayHold(h.Node, h.Runway, h.Runway.DistanceFt(h.Node.Position))),
        ];

    private static LatLon FirstPointAlong(AirportGroundLayout layout, GroundEdge edge, GroundNode from)
    {
        if (edge.IntermediatePoints.Count == 0)
        {
            return layout.Nodes[edge.OtherNodeId(from.Id)].Position;
        }

        (double Lat, double Lon) first = edge.Nodes[0].Id == from.Id ? edge.IntermediatePoints[0] : edge.IntermediatePoints[^1];
        return new LatLon(first.Lat, first.Lon);
    }

    private static List<LatLon> EdgePoints(GroundEdge edge) =>
        [edge.Nodes[0].Position, .. edge.IntermediatePoints.Select(q => new LatLon(q.Lat, q.Lon)), edge.Nodes[1].Position];

    private static double DistanceFt(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "precompute-" + Guid.NewGuid());

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record UncappedPlan(PushTargetKind Kind, string Name, string Command, double? PathLengthFt);

    private sealed record RunwayHold(GroundNode Hold, RunwayLine Runway, double HoldFt);

    /// <summary>A runway's centreline pieces, flattened around their first point to measure point distances in feet.</summary>
    private sealed record RunwayLine(string Name, List<List<LatLon>> Pieces)
    {
        public double DistanceFt(LatLon point) =>
            Pieces.SelectMany(piece => piece.Zip(piece.Skip(1), (a, b) => SegmentFt(point, a, b))).DefaultIfEmpty(double.PositiveInfinity).Min();

        private static double SegmentFt(LatLon p, LatLon a, LatLon b)
        {
            double cos = Math.Cos(a.Lat * Math.PI / 180.0);
            double feetPerDegree = 60.0 * GeoMath.FeetPerNm;
            (double X, double Y) Local(LatLon q) => ((q.Lon - a.Lon) * cos * feetPerDegree, (q.Lat - a.Lat) * feetPerDegree);
            (double px, double py) = Local(p);
            (double bx, double by) = Local(b);
            double lengthSq = (bx * bx) + (by * by);
            double t = lengthSq <= 0.0 ? 0.0 : Math.Clamp(((px * bx) + (py * by)) / lengthSq, 0.0, 1.0);
            return Math.Sqrt(Math.Pow(px - (t * bx), 2) + Math.Pow(py - (t * by), 2));
        }
    }
}
