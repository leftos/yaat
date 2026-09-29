using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// <see cref="TrackResolver"/> is the one TCP-to-owner chain and the one identity resolver for every run kind
/// (live, replay, reconstruction, test). Pins the chain order the server used to keep privately — student TCP,
/// scenario ATC positions, facility TCP, ERAM code, STARS interfacility handoff code, ERAM-to-STARS prefixed
/// code — and the identity precedence: an <c>AS</c> override, then an AI connection's own position, then the
/// connection's selected position, then the student.
/// </summary>
public class TrackResolverTests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public TrackResolverTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static SimScenarioState Scenario(TrackOwner? student, Tcp? studentTcp, ArtccConfigRoot? config, params ResolvedAtcPosition[] atc) =>
        new()
        {
            ScenarioId = "s",
            ScenarioName = "s",
            RngSeed = 0,
            OriginalScenarioJson = "{}",
            StudentPosition = student,
            StudentTcp = studentTcp,
            ArtccConfig = config,
            ArtccId = "ZOA",
            AtcPositions = [.. atc],
        };

    private static ResolvedAtcPosition Atc(TrackOwner owner, Tcp tcp) =>
        new()
        {
            Source = new ScenarioAtc { Id = owner.Callsign },
            Owner = owner,
            Tcp = tcp,
        };

    private TrackOwner NctApproach() => _zoa!.ResolvePosition(_zoa!.FindPositionByCallsign("NCT_APP", facilityHint: null)!.Id)!;

    [Fact]
    public void ResolveEramCode_SectorWithALeadingZero_ResolvesTheAdaptedSector()
    {
        Assert.SkipWhen(_zoa is null, "ZOA config not available");

        TrackOwner? padded = _zoa!.ResolveEramCode("C044");

        Assert.NotNull(padded);
        Assert.Equal(_zoa!.ResolveEramCode("C44"), padded);
        Assert.Equal("44", padded.SectorId);
    }

    /// <summary>
    /// A position's acting identity (<see cref="ArtccConfigResolver.ResolvePosition"/>) and every owner a command stores for it
    /// (a TCP code within the facility, the callsign-qualified TCP code, the callsign alone) carry one facility, so
    /// <see cref="TrackOwner.MatchesPosition"/> can veto a same-callsign match across facilities without splitting one position.
    /// A tower holding its TRACON's TCP resolves to the TRACON on every path.
    /// </summary>
    [Theory]
    [InlineData("OAK_TWR", "O90")]
    [InlineData("OAK_TWR", "NCT")]
    [InlineData("NCT_APP", "NCT")]
    public void ResolvedOwners_ForOneStarsPosition_CarryOneFacility(string callsign, string expectedFacility)
    {
        Assert.SkipWhen(_zoa is null, "ZOA config not available");
        PositionConfig position = _zoa!.FindPositionsByCallsign(callsign).First(p => _zoa.ResolvePosition(p.Id)!.FacilityId == expectedFacility);
        TrackOwner acting = _zoa.ResolvePosition(position.Id)!;
        Tcp tcp = _zoa.GetTcpForPosition(position.Id)!;
        string code = $"{tcp.Subset}{tcp.SectorId}";
        SimScenarioState scenario = Scenario(null, null, _zoa);

        TrackOwner? byFacilityTcp = _zoa.ResolveTcpCode(expectedFacility, code);
        TrackOwner? byQualifiedCode = TrackResolver.ResolveTcpToOwner(scenario, $"{callsign}@{code}", facilityHint: null);
        TrackOwner? byCallsign = TrackResolver.ResolveTcpToOwner(scenario, callsign, expectedFacility);

        Assert.Equal(expectedFacility, acting.FacilityId);
        Assert.Equal(expectedFacility, byFacilityTcp?.FacilityId);
        Assert.Equal(acting, byQualifiedCode);
        Assert.Equal(acting, byCallsign);
    }

    [Fact]
    public void ResolvedOwners_ForOneEramPosition_CarryOneFacility()
    {
        Assert.SkipWhen(_zoa is null, "ZOA config not available");
        TrackOwner byCode = _zoa!.ResolveEramCode("C44")!;

        TrackOwner acting = _zoa.ResolvePosition(_zoa.FindPositionByCallsign(byCode.Callsign, byCode.FacilityId)!.Id)!;

        Assert.Equal(acting.FacilityId, byCode.FacilityId);
        Assert.Equal("ZOA", acting.FacilityId);
    }

    /// <summary>The position with this callsign whose owner carries <paramref name="facility"/> (ZOA lists OAK_TWR under NCT and O90).</summary>
    private TrackOwner Twin(string callsign, string facility) =>
        _zoa!.FindPositionsByCallsign(callsign).Select(p => _zoa!.ResolvePosition(p.Id)!).Single(owner => owner.FacilityId == facility);

    [Theory]
    [InlineData("O90")]
    [InlineData("NCT")]
    public void ResolveIdentity_AsTwinCallsign_ResolvesToTheTwinInTheActingControllersFacility(string facility)
    {
        Assert.SkipWhen(_zoa is null, "ZOA config not available");
        SimScenarioState scenario = Scenario(Twin("OAK_TWR", "NCT"), new Tcp(3, "O", "tcp-3o", null), _zoa);
        var selections = new PositionSelections();
        selections.Select("conn", Twin("OAK_GND", facility));

        TrackOwner? identity = TrackResolver.ResolveIdentity(scenario, selections, "conn", "OAK_TWR");

        Assert.Equal(Twin("OAK_TWR", facility), identity);
    }

    [Fact]
    public void HandoffToATwinCallsign_FromO90_IsOfferedToO90sTwin_WhichAcceptsIt()
    {
        Assert.SkipWhen(_zoa is null, "ZOA config not available");
        TrackOwner o90Approach = Twin("BAY_B_APP", "O90");
        TrackOwner o90Tower = Twin("OAK_TWR", "O90");
        SimScenarioState scenario = Scenario(Twin("OAK_TWR", "NCT"), new Tcp(3, "O", "tcp-3o", null), _zoa);
        var world = new SimulationWorld();
        AircraftState ac = new()
        {
            Callsign = "UAL1",
            AircraftType = "B738",
            Position = new LatLon(37.7, -122.2),
            TrueHeading = new TrueHeading(090),
            Altitude = 3_000,
            IndicatedAirspeed = 180,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan(),
        };
        ac.Track.Owner = o90Approach;
        world.AddAircraft(ac);

        CommandResult offered = TrackEngine.ApplyHandoff(ac, scenario, o90Approach, "OAK_TWR", redirect: null);
        CommandResult accepted = TrackEngine.DispatchGlobal(new AcceptAllHandoffsCommand(), world, scenario, o90Tower);

        Assert.True(offered.Success, offered.Message);
        Assert.Equal("Accepted 1 handoff(s)", accepted.Message);
        Assert.Equal(o90Tower, ac.Track.Owner);
    }

    [Fact]
    public void ResolveTcpToOwner_PositionCallsign_IsTheLastFallback()
    {
        Assert.SkipWhen(_zoa is null, "ZOA config not available");
        var student = TrackOwner.CreateStars("OAK_TWR", "NCT", 3, "O");
        SimScenarioState scenario = Scenario(student, new Tcp(3, "O", "tcp-3o", null), _zoa);

        TrackOwner? ground = TrackResolver.ResolveTcpToOwner(scenario, "OAK_GND", facilityHint: null);

        Assert.NotNull(ground);
        Assert.Equal("OAK_GND", ground.Callsign);
        Assert.Equal(student, TrackResolver.ResolveTcpToOwner(scenario, "3O", facilityHint: null));
        Assert.Null(TrackResolver.ResolveTcpToOwner(scenario, "NOT_A_POSITION", facilityHint: null));
    }

    [Fact]
    public void ResolveTcpToOwner_CallsignAtCode_PicksAmongPositionsSharingACallsign()
    {
        Assert.SkipWhen(_zoa is null, "ZOA config not available");
        SimScenarioState scenario = Scenario(TrackOwner.CreateStars("OAK_TWR", "NCT", 3, "O"), new Tcp(3, "O", "tcp-3o", null), _zoa);
        TrackOwner? byCallsign = TrackResolver.ResolveTcpToOwner(scenario, "NCT_APP", facilityHint: null);
        TrackOwner? byCode = TrackResolver.ResolveTcpToOwner(scenario, "1M", facilityHint: null);
        Assert.NotNull(byCallsign);
        Assert.NotNull(byCode);
        Assert.NotEqual("M", byCallsign.SectorId);
        Assert.NotEqual("NCT_APP", byCode.Callsign);

        TrackOwner? qualified = TrackResolver.ResolveTcpToOwner(scenario, "NCT_APP@1M", facilityHint: null);

        Assert.NotNull(qualified);
        Assert.Equal("NCT_APP", qualified.Callsign);
        Assert.Equal(1, qualified.Subset);
        Assert.Equal("M", qualified.SectorId);
        Assert.Null(TrackResolver.ResolveTcpToOwner(scenario, "NCT_APP@9Z", facilityHint: null));
    }

    [Fact]
    public void ResolveTcpToOwner_StudentTcpWins_OverScenarioAtcSharingIt()
    {
        var student = TrackOwner.CreateStars("OAK_TWR", "OAK", 3, "O");
        var ground = TrackOwner.CreateStars("OAK_GND", "OAK", 3, "O");
        var tcp = new Tcp(3, "O", "tcp-3o", null);
        SimScenarioState scenario = Scenario(student, tcp, config: null, Atc(ground, tcp));

        Assert.Equal(student, TrackResolver.ResolveTcpToOwner(scenario, "3O", facilityHint: null));
    }

    [Fact]
    public void ResolveTcpToOwner_ScenarioAtcPosition_ResolvesWithoutConfig()
    {
        var student = TrackOwner.CreateStars("OAK_TWR", "OAK", 3, "O");
        var dep = TrackOwner.CreateStars("SFO_DEP", "NCT", 4, "U");
        SimScenarioState scenario = Scenario(student, new Tcp(3, "O", "tcp-3o", null), config: null, Atc(dep, new Tcp(4, "U", "tcp-4u", null)));

        Assert.Equal(dep, TrackResolver.ResolveTcpToOwner(scenario, "4u", facilityHint: null));
        Assert.Null(TrackResolver.ResolveTcpToOwner(scenario, "2B", facilityHint: null));
    }

    [Fact]
    public void ResolveTcpToOwner_StarsInterfacilityHandoffCode_ResolvesThroughTheStudentFacility()
    {
        if (_zoa is null)
        {
            return;
        }

        SimScenarioState scenario = Scenario(NctApproach(), null, _zoa);
        TrackOwner? expected = _zoa.ResolveStarsHandoffCode("NCT", "`3");
        Assert.NotNull(expected);

        Assert.Equal(expected, TrackResolver.ResolveTcpToOwner(scenario, "`3", facilityHint: null));
        Assert.Equal(_zoa.ResolveStarsHandoffCode("NCT", "`31H"), TrackResolver.ResolveTcpToOwner(scenario, "`31H", facilityHint: null));
    }

    [Fact]
    public void ResolveTcpToOwner_FacilityTcpAndEramCode_ResolveThroughTheConfig()
    {
        if (_zoa is null)
        {
            return;
        }

        SimScenarioState scenario = Scenario(NctApproach(), null, _zoa);

        TrackOwner? boulder = TrackResolver.ResolveTcpToOwner(scenario, "2B", facilityHint: null);
        Assert.NotNull(boulder);
        Assert.Equal("NCT", boulder.FacilityId);
        Assert.Equal(2, boulder.Subset);

        TrackOwner? eram = _zoa.ResolveEramCode("C44");
        Assert.NotNull(eram);
        Assert.Equal(eram, TrackResolver.ResolveTcpToOwner(scenario, "C44", facilityHint: null));
    }

    [Fact]
    public void ResolveTcpToOwner_EramToStarsPrefixedCode_ResolvesWithoutAStudentFacility()
    {
        if (_zoa is null)
        {
            return;
        }

        SimScenarioState scenario = Scenario(student: null, studentTcp: null, _zoa);
        TrackOwner? expected = _zoa.ResolveEramToStarsHandoffCode("Q2B");
        Assert.NotNull(expected);

        Assert.Equal(expected, TrackResolver.ResolveTcpToOwner(scenario, "Q2B", facilityHint: null));
        Assert.Null(TrackResolver.ResolveTcpToOwner(scenario, "2B", facilityHint: null));
        Assert.Null(TrackResolver.ResolveTcpToOwner(scenario, "ZZZZ", facilityHint: null));
    }

    [Fact]
    public void ResolveTcpToOwner_NeighbourCenterCode_ResolvesAfterTheOwnCentreAndStarsCodes()
    {
        if (_zoa is null)
        {
            return;
        }

        // A copy: the shared ZOA snapshot is not mutated.
        ArtccConfigRoot zoa = JsonSerializer.Deserialize<ArtccConfigRoot>(JsonSerializer.Serialize(_zoa))!;
        zoa.NeighborCenterNasIds["ZLA"] = "L";
        SimScenarioState scenario = Scenario(NctApproach(), null, zoa);

        Assert.Equal(TrackOwner.CreateEram("ZLA_25_CTR", "ZLA", "25"), TrackResolver.ResolveTcpToOwner(scenario, "L25", facilityHint: null));
        Assert.Equal(TrackOwner.CreateEram("ZLA_00_CTR", "ZLA", "00"), TrackResolver.ResolveTcpToOwner(scenario, "L00", facilityHint: null));
        Assert.Null(TrackResolver.ResolveTcpToOwner(scenario, "L129", facilityHint: null));
        Assert.Null(TrackResolver.ResolveTcpToOwner(scenario, "X25", facilityHint: null));
        Assert.Equal(zoa.ResolveEramToStarsHandoffCode("Q2B"), TrackResolver.ResolveTcpToOwner(scenario, "Q2B", facilityHint: null));
        Assert.Equal("NCT", TrackResolver.ResolveTcpToOwner(scenario, "Q2B", facilityHint: null)?.FacilityId);
        TrackOwner? ownCentre = TrackResolver.ResolveTcpToOwner(scenario, "C44", facilityHint: null);
        Assert.Equal(zoa.ResolveEramCode("C44"), ownCentre);
        Assert.Equal("ZOA", ownCentre?.FacilityId);
    }

    [Fact]
    public void ResolveTcpToOwner_NoConfig_UnknownCodeIsNull()
    {
        SimScenarioState scenario = Scenario(TrackOwner.CreateStars("OAK_TWR", "OAK", 3, "O"), new Tcp(3, "O", "tcp-3o", null), config: null);

        Assert.Null(TrackResolver.ResolveTcpToOwner(scenario, "`3", facilityHint: null));
        Assert.Null(TrackResolver.ResolveTcpToOwner(scenario, "C44", facilityHint: null));
    }

    [Fact]
    public void FindTcpByCode_ScenarioThenConfig()
    {
        if (_zoa is null)
        {
            return;
        }

        var dep = TrackOwner.CreateStars("SFO_DEP", "NCT", 4, "U");
        var depTcp = new Tcp(4, "U", "tcp-4u", null);
        SimScenarioState scenario = Scenario(NctApproach(), null, _zoa, Atc(dep, depTcp));

        Assert.Same(depTcp, TrackResolver.FindTcpByCode(scenario, "4U"));
        Tcp? boulder = TrackResolver.FindTcpByCode(scenario, "2B");
        Assert.NotNull(boulder);
        Assert.Equal("B", boulder.SectorId);
        Assert.Null(TrackResolver.FindTcpByCode(scenario, "ZZ"));
    }

    [Fact]
    public void ResolveIdentity_AsOverride_Wins()
    {
        var student = TrackOwner.CreateStars("OAK_TWR", "OAK", 3, "O");
        var dep = TrackOwner.CreateStars("SFO_DEP", "NCT", 4, "U");
        SimScenarioState scenario = Scenario(student, new Tcp(3, "O", "tcp-3o", null), config: null, Atc(dep, new Tcp(4, "U", "tcp-4u", null)));
        var selections = new PositionSelections();
        selections.Select("conn", student);

        Assert.Equal(dep, TrackResolver.ResolveIdentity(scenario, selections, "conn", "4U"));
        Assert.Null(TrackResolver.ResolveIdentity(scenario, selections, "conn", "9Z"));
    }

    [Fact]
    public void ResolveIdentity_SelectionBeatsStudent_StudentIsTheFallback()
    {
        var student = TrackOwner.CreateStars("OAK_TWR", "OAK", 3, "O");
        var dep = TrackOwner.CreateStars("SFO_DEP", "NCT", 4, "U");
        SimScenarioState scenario = Scenario(student, new Tcp(3, "O", "tcp-3o", null), config: null);
        var selections = new PositionSelections();

        Assert.Equal(student, TrackResolver.ResolveIdentity(scenario, selections, "conn", null));

        selections.Select("conn", dep);
        Assert.Equal(dep, TrackResolver.ResolveIdentity(scenario, selections, "conn", null));
        Assert.Equal(student, TrackResolver.ResolveIdentity(scenario, selections, "other-conn", null));
    }

    [Fact]
    public void ResolveIdentity_AiConnection_ResolvesItsPositionWithoutStudentOrSelection()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        SimScenarioState scenario = Scenario(student: null, studentTcp: null, _zoa);

        TrackOwner? identity = TrackResolver.ResolveIdentity(scenario, new PositionSelections(), AiConnectionId.Format(ground.PositionId), null);

        Assert.Equal(ground.Identity, identity);
    }

    [Fact]
    public void ResolveIdentity_SelectionUnderAnAiConnectionId_DoesNotDisplaceThePosition()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ground = TestAiPositions.OakGround(_zoa);
        var student = TrackOwner.CreateStars("OAK_TWR", "OAK", 3, "T");
        SimScenarioState scenario = Scenario(student, new Tcp(3, "T", "tcp-3t", null), _zoa);
        string aiConnectionId = AiConnectionId.Format(ground.PositionId);
        var selections = new PositionSelections();
        selections.Select(aiConnectionId, student);

        Assert.Equal(ground.Identity, TrackResolver.ResolveIdentity(scenario, selections, aiConnectionId, null));
    }

    [Fact]
    public void PositionSelections_SnapshotAndRestore_RoundTrip()
    {
        var dep = TrackOwner.CreateStars("SFO_DEP", "NCT", 4, "U");
        var eram = TrackOwner.CreateEram("OAK_CTR", "ZOA", "44");
        var selections = new PositionSelections();
        selections.Select("b", eram);
        selections.Select("a", dep);

        SortedDictionary<string, TrackOwner> snapshot = selections.Snapshot();
        Assert.Equal(["a", "b"], [.. snapshot.Keys]);

        var restored = new PositionSelections();
        restored.Select("stale", dep);
        restored.Restore(snapshot.ToDictionary(kv => kv.Key, kv => kv.Value.ToSnapshot()));

        Assert.False(restored.TryGet("stale", out _));
        Assert.True(restored.TryGet("a", out TrackOwner? a));
        Assert.Equal(dep, a);
        Assert.True(restored.TryGet("b", out TrackOwner? b));
        Assert.Equal(eram, b);

        restored.Restore(null);
        Assert.Empty(restored.Snapshot());
    }
}
