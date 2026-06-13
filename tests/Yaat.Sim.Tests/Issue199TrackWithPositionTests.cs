using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests;

/// <summary>
/// GitHub issue #199: <c>TRACK [position]</c> must track the selected aircraft WITH the named
/// position (e.g. <c>TRACK 4U</c> claims the track for 4U), not with the acting/active identity.
/// The position argument was parsed into <see cref="TrackAircraftCommand.TcpCode"/> but dropped
/// at dispatch, so the aircraft was always tracked by the default/active sector.
///
/// Covers the shared Sim dispatcher (<see cref="TrackEngine.Dispatch"/>, used by the replay
/// applier). The signature help advertises <c>TRACK [position]</c> / "Track with position".
/// </summary>
public class Issue199TrackWithPositionTests
{
    private static TrackOwner Owner(string callsign, int subset, string sectorId) => TrackOwner.CreateStars(callsign, "ZOA", subset, sectorId);

    private static AircraftState Aircraft() => new() { Callsign = "N123AB", AircraftType = "C172" };

    private static ResolvedAtcPosition Atc(TrackOwner owner, int subset, string sectorId) =>
        new()
        {
            Source = new ScenarioAtc { Id = owner.Callsign },
            Owner = owner,
            Tcp = new Tcp(subset, sectorId, owner.Callsign, null),
        };

    private static SimScenarioState Scenario(TrackOwner studentPosition, params ResolvedAtcPosition[] atc) =>
        new()
        {
            ScenarioId = "s",
            ScenarioName = "s",
            RngSeed = 0,
            OriginalScenarioJson = "{}",
            ElapsedSeconds = 0,
            StudentPosition = studentPosition,
            AtcPositions = [.. atc],
        };

    [Fact]
    public void Dispatch_TrackWithPosition_TracksWithNamedPosition_NotIdentity()
    {
        var ac = Aircraft();
        var student = Owner("OAK_TWR", 3, "O");
        var dep = Owner("SFO_DEP", 4, "U");
        var scenario = Scenario(student, Atc(dep, 4, "U"));

        // Acting identity is the student (3O); the command names 4U explicitly — 4U must win.
        var result = TrackEngine.Dispatch(new TrackAircraftCommand("4U"), ac, identity: student, scenario);

        Assert.NotNull(result);
        Assert.True(result.Success, result.Message);
        Assert.NotNull(ac.Track.Owner);
        Assert.Equal("SFO_DEP", ac.Track.Owner.Callsign);
    }

    [Fact]
    public void Dispatch_TrackWithPosition_NoActiveIdentity_StillTracks()
    {
        var ac = Aircraft();
        var dep = Owner("SFO_DEP", 4, "U");
        var scenario = Scenario(Owner("OAK_TWR", 3, "O"), Atc(dep, 4, "U"));

        // The position argument supplies the owner, so a null identity must not block the track.
        var result = TrackEngine.Dispatch(new TrackAircraftCommand("4U"), ac, identity: null, scenario);

        Assert.NotNull(result);
        Assert.True(result.Success, result.Message);
        Assert.Equal("SFO_DEP", ac.Track.Owner!.Callsign);
    }

    [Fact]
    public void Dispatch_TrackWithUnknownPosition_ReturnsError()
    {
        var ac = Aircraft();
        var student = Owner("OAK_TWR", 3, "O");
        var scenario = Scenario(student);

        var result = TrackEngine.Dispatch(new TrackAircraftCommand("ZZ"), ac, identity: student, scenario);

        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Contains("Unknown position", result.Message ?? "");
        Assert.Null(ac.Track.Owner);
    }

    [Fact]
    public void Dispatch_TrackWithoutPosition_UsesIdentity()
    {
        var ac = Aircraft();
        var student = Owner("OAK_TWR", 3, "O");
        var scenario = Scenario(student);

        var result = TrackEngine.Dispatch(new TrackAircraftCommand(null), ac, identity: student, scenario);

        Assert.NotNull(result);
        Assert.True(result.Success, result.Message);
        Assert.Equal("OAK_TWR", ac.Track.Owner!.Callsign);
    }
}
