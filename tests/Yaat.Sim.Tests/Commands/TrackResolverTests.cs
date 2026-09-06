using Xunit;
using Yaat.Sim.Commands;
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

    private TrackOwner NctApproach() => _zoa!.ResolvePosition(_zoa!.FindPositionByCallsign("NCT_APP")!.Id)!;

    [Fact]
    public void ResolveTcpToOwner_PositionCallsign_IsTheLastFallback()
    {
        Assert.SkipWhen(_zoa is null, "ZOA config not available");
        var student = TrackOwner.CreateStars("OAK_TWR", "NCT", 3, "O");
        var scenario = Scenario(student, new Tcp(3, "O", "tcp-3o", null), _zoa);

        var ground = TrackResolver.ResolveTcpToOwner(scenario, "OAK_GND");

        Assert.NotNull(ground);
        Assert.Equal("OAK_GND", ground.Callsign);
        Assert.Equal(student, TrackResolver.ResolveTcpToOwner(scenario, "3O"));
        Assert.Null(TrackResolver.ResolveTcpToOwner(scenario, "NOT_A_POSITION"));
    }

    [Fact]
    public void ResolveTcpToOwner_CallsignAtCode_PicksAmongPositionsSharingACallsign()
    {
        Assert.SkipWhen(_zoa is null, "ZOA config not available");
        var scenario = Scenario(TrackOwner.CreateStars("OAK_TWR", "NCT", 3, "O"), new Tcp(3, "O", "tcp-3o", null), _zoa);
        var byCallsign = TrackResolver.ResolveTcpToOwner(scenario, "NCT_APP");
        var byCode = TrackResolver.ResolveTcpToOwner(scenario, "1M");
        Assert.NotNull(byCallsign);
        Assert.NotNull(byCode);
        Assert.NotEqual("M", byCallsign.SectorId);
        Assert.NotEqual("NCT_APP", byCode.Callsign);

        var qualified = TrackResolver.ResolveTcpToOwner(scenario, "NCT_APP@1M");

        Assert.NotNull(qualified);
        Assert.Equal("NCT_APP", qualified.Callsign);
        Assert.Equal(1, qualified.Subset);
        Assert.Equal("M", qualified.SectorId);
        Assert.Null(TrackResolver.ResolveTcpToOwner(scenario, "NCT_APP@9Z"));
    }

    [Fact]
    public void ResolveTcpToOwner_StudentTcpWins_OverScenarioAtcSharingIt()
    {
        var student = TrackOwner.CreateStars("OAK_TWR", "OAK", 3, "O");
        var ground = TrackOwner.CreateStars("OAK_GND", "OAK", 3, "O");
        var tcp = new Tcp(3, "O", "tcp-3o", null);
        var scenario = Scenario(student, tcp, config: null, Atc(ground, tcp));

        Assert.Equal(student, TrackResolver.ResolveTcpToOwner(scenario, "3O"));
    }

    [Fact]
    public void ResolveTcpToOwner_ScenarioAtcPosition_ResolvesWithoutConfig()
    {
        var student = TrackOwner.CreateStars("OAK_TWR", "OAK", 3, "O");
        var dep = TrackOwner.CreateStars("SFO_DEP", "NCT", 4, "U");
        var scenario = Scenario(student, new Tcp(3, "O", "tcp-3o", null), config: null, Atc(dep, new Tcp(4, "U", "tcp-4u", null)));

        Assert.Equal(dep, TrackResolver.ResolveTcpToOwner(scenario, "4u"));
        Assert.Null(TrackResolver.ResolveTcpToOwner(scenario, "2B"));
    }

    [Fact]
    public void ResolveTcpToOwner_StarsInterfacilityHandoffCode_ResolvesThroughTheStudentFacility()
    {
        if (_zoa is null)
        {
            return;
        }

        var scenario = Scenario(NctApproach(), null, _zoa);
        var expected = _zoa.ResolveStarsHandoffCode("NCT", "`3");
        Assert.NotNull(expected);

        Assert.Equal(expected, TrackResolver.ResolveTcpToOwner(scenario, "`3"));
        Assert.Equal(_zoa.ResolveStarsHandoffCode("NCT", "`31H"), TrackResolver.ResolveTcpToOwner(scenario, "`31H"));
    }

    [Fact]
    public void ResolveTcpToOwner_FacilityTcpAndEramCode_ResolveThroughTheConfig()
    {
        if (_zoa is null)
        {
            return;
        }

        var scenario = Scenario(NctApproach(), null, _zoa);

        var boulder = TrackResolver.ResolveTcpToOwner(scenario, "2B");
        Assert.NotNull(boulder);
        Assert.Equal("NCT", boulder.FacilityId);
        Assert.Equal(2, boulder.Subset);

        var eram = _zoa.ResolveEramCode("C44");
        Assert.NotNull(eram);
        Assert.Equal(eram, TrackResolver.ResolveTcpToOwner(scenario, "C44"));
    }

    [Fact]
    public void ResolveTcpToOwner_EramToStarsPrefixedCode_ResolvesWithoutAStudentFacility()
    {
        if (_zoa is null)
        {
            return;
        }

        var scenario = Scenario(student: null, studentTcp: null, _zoa);
        var expected = _zoa.ResolveEramToStarsHandoffCode("Q2B");
        Assert.NotNull(expected);

        Assert.Equal(expected, TrackResolver.ResolveTcpToOwner(scenario, "Q2B"));
        Assert.Null(TrackResolver.ResolveTcpToOwner(scenario, "2B"));
        Assert.Null(TrackResolver.ResolveTcpToOwner(scenario, "ZZZZ"));
    }

    [Fact]
    public void ResolveTcpToOwner_NoConfig_UnknownCodeIsNull()
    {
        var scenario = Scenario(TrackOwner.CreateStars("OAK_TWR", "OAK", 3, "O"), new Tcp(3, "O", "tcp-3o", null), config: null);

        Assert.Null(TrackResolver.ResolveTcpToOwner(scenario, "`3"));
        Assert.Null(TrackResolver.ResolveTcpToOwner(scenario, "C44"));
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
        var scenario = Scenario(NctApproach(), null, _zoa, Atc(dep, depTcp));

        Assert.Same(depTcp, TrackResolver.FindTcpByCode(scenario, "4U"));
        var boulder = TrackResolver.FindTcpByCode(scenario, "2B");
        Assert.NotNull(boulder);
        Assert.Equal("B", boulder.SectorId);
        Assert.Null(TrackResolver.FindTcpByCode(scenario, "ZZ"));
    }

    [Fact]
    public void ResolveIdentity_AsOverride_Wins()
    {
        var student = TrackOwner.CreateStars("OAK_TWR", "OAK", 3, "O");
        var dep = TrackOwner.CreateStars("SFO_DEP", "NCT", 4, "U");
        var scenario = Scenario(student, new Tcp(3, "O", "tcp-3o", null), config: null, Atc(dep, new Tcp(4, "U", "tcp-4u", null)));
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
        var scenario = Scenario(student, new Tcp(3, "O", "tcp-3o", null), config: null);
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

        var ground = TestAiPositions.OakGround(_zoa);
        var scenario = Scenario(student: null, studentTcp: null, _zoa);

        var identity = TrackResolver.ResolveIdentity(scenario, new PositionSelections(), AiConnectionId.Format(ground.PositionId), null);

        Assert.Equal(ground.Identity, identity);
    }

    [Fact]
    public void ResolveIdentity_SelectionUnderAnAiConnectionId_DoesNotDisplaceThePosition()
    {
        if (_zoa is null)
        {
            return;
        }

        var ground = TestAiPositions.OakGround(_zoa);
        var student = TrackOwner.CreateStars("OAK_TWR", "OAK", 3, "T");
        var scenario = Scenario(student, new Tcp(3, "T", "tcp-3t", null), _zoa);
        var aiConnectionId = AiConnectionId.Format(ground.PositionId);
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

        var snapshot = selections.Snapshot();
        Assert.Equal(["a", "b"], snapshot.Keys.ToArray());

        var restored = new PositionSelections();
        restored.Select("stale", dep);
        restored.Restore(snapshot.ToDictionary(kv => kv.Key, kv => kv.Value.ToSnapshot()));

        Assert.False(restored.TryGet("stale", out _));
        Assert.True(restored.TryGet("a", out var a));
        Assert.Equal(dep, a);
        Assert.True(restored.TryGet("b", out var b));
        Assert.Equal(eram, b);

        restored.Restore(null);
        Assert.Empty(restored.Snapshot());
    }
}
