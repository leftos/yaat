using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="AirwayBracketing.AheadFix"/> over the real navigation data: for every airway near OAK and every heading, the fix it
/// names is the first fix a dispatched <c>JAWY</c> proceeds to after the intercept, and it names none exactly where the sim
/// refuses with no navigable segment.
/// </summary>
public class AirwayBracketingTests
{
    public AirwayBracketingTests() => TestVnasData.EnsureInitialized();

    public static TheoryData<double> Headings() => [0, 45, 90, 135, 180, 225, 270, 315];

    [Theory]
    [MemberData(nameof(Headings))]
    public void AheadFix_IsWhereTheSimProceedsAfterTheIntercept(double heading)
    {
        var position = new LatLon(37.62, -121.95);
        IReadOnlyList<NearbyAirway> airways = NavigationDatabase.Instance.AirwaysNear([], position, 30);
        Assert.True(airways.Count >= 5, $"{airways.Count} airways near");
        int refused = 0;

        foreach (NearbyAirway airway in airways)
        {
            var aircraft = new AircraftState
            {
                Callsign = "N123",
                AircraftType = "B738",
                TrueHeading = new TrueHeading(heading),
                Altitude = 9000,
                Position = position,
            };
            CommandResult result = CommandDispatcher.Dispatch(new JoinAirwayCommand(airway.Id), aircraft, TestDispatch.Context(Random.Shared));
            string? ahead = AirwayBracketing.AheadFix(NavigationDatabase.Instance, airway.Id, position, new TrueHeading(heading));

            if (!result.Success)
            {
                Assert.Contains("No navigable segment", result.Message);
                Assert.Null(ahead);
                refused++;
                continue;
            }

            CommandBlock block = Assert.Single(aircraft.Queue.Blocks, b => b.Trigger is not null);
            block.ApplyAction!(aircraft, true);
            Assert.Equal(aircraft.Targets.NavigationRoute[0].Name, ahead);
        }

        Assert.True(refused < airways.Count, "every airway was refused");
    }

    [Fact]
    public void AheadFix_UnknownAirway_IsNull() =>
        Assert.Null(AirwayBracketing.AheadFix(NavigationDatabase.Instance, "V9999", new LatLon(37.62, -121.95), new TrueHeading(90)));
}
