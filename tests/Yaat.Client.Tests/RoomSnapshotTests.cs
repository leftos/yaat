using Xunit;
using Yaat.Client.Services;
using Yaat.Sim;
using Yaat.Sim.Speech.Context;
using Yaat.Sim.Testing;

namespace Yaat.Client.Tests;

/// <summary>The pool-thread step that turns a captured <see cref="RoomSnapshot"/> into the decoder's <see cref="RoomContextRequest"/>.</summary>
public class RoomSnapshotTests
{
    public RoomSnapshotTests() => TestVnasData.EnsureInitialized();

    [Fact]
    public void ToRequest_ResolvesEachAircraftsProgrammedFixes_AsProgrammedFixResolverDoes()
    {
        var arrival = new RoomAircraftSnapshot("N4926F", "C172", "28R", "SAC OAK", "I28R", "KOAK", "KSAC", "", "28R");
        var departure = new RoomAircraftSnapshot("SWA2495", "B738", null, "OAK SAC", null, "KSAC", "KOAK", "", "");
        var room = new RoomSnapshot("KOAK", true, null, [arrival, departure], ["NorCal Approach", "Oakland Tower"]);

        RoomContextRequest request = RoomSnapshot.ToRequest(room);

        Assert.Equal("KOAK", request.AirportIcao);
        Assert.True(request.ApproachPosition);
        Assert.Null(request.Layout);
        Assert.Equal(["NorCal Approach", "Oakland Tower"], request.FacilityRadioNames);
        Assert.Equal(["N4926F", "SWA2495"], request.Aircraft.Select(a => a.Callsign));
        foreach ((RoomAircraftSnapshot captured, RoomAircraftInput input) in new[] { arrival, departure }.Zip(request.Aircraft))
        {
            HashSet<string> expected = ProgrammedFixResolver.Resolve(
                captured.Route,
                captured.ExpectedApproach,
                captured.Destination,
                captured.Departure,
                null,
                captured.ActiveStarId,
                captured.DestinationRunway
            );
            Assert.NotEmpty(expected);
            Assert.Contains("SAC", input.ProgrammedFixes);
            Assert.Equal(expected.Order(StringComparer.Ordinal), input.ProgrammedFixes.Order(StringComparer.Ordinal));
            Assert.Equal(captured.AircraftType, input.AircraftType);
            Assert.Equal(captured.AssignedRunway, input.AssignedRunway);
        }
    }
}
