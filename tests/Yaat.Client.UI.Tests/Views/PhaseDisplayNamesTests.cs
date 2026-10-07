using Xunit;
using Yaat.Client.ContextMenus;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The display name of a raw sim phase (<see cref="PhaseDisplayNames.For"/>): the table of phases whose sim name does not
/// read as English, the sentence-cased CamelCase fallback, and the acronym- and callsign-keeping behaviour that leaves a
/// dynamic tail alone.
/// </summary>
public class PhaseDisplayNamesTests
{
    [Theory]
    [InlineData("FinalApproach", "Final")]
    [InlineData("ApproachNav", "Approach")]
    [InlineData("InterceptCourse", "Approach")]
    [InlineData("LinedUpAndWaiting", "Line up and wait")]
    [InlineData("LiningUp", "Lining up")]
    [InlineData("Holding After Pushback", "Holding after push")]
    [InlineData("InitialClimb", "Departure")]
    [InlineData("DepartureProcedure", "Departure")]
    [InlineData("GoAround", "Go around")]
    [InlineData("HoldingPattern", "Holding")]
    [InlineData("TouchAndGo", "Touch and go")]
    [InlineData("StopAndGo", "Stop and go")]
    [InlineData("LowApproach", "Low approach")]
    [InlineData("Approach-H", "Approach")]
    [InlineData("Landing-H", "Landing")]
    [InlineData("Takeoff-H", "Takeoff")]
    [InlineData("AirTaxi", "Air taxi")]
    [InlineData("At Parking", "At parking")]
    public void For_MapsTheTable(string raw, string display) => Assert.Equal(display, PhaseDisplayNames.For(raw));

    [Theory]
    [InlineData("MidfieldCrossing", "Midfield crossing")]
    [InlineData("ProcedureTurn", "Procedure turn")]
    [InlineData("PatternExit", "Pattern exit")]
    [InlineData("Holding After Exit", "Holding after exit")]
    [InlineData("Following N123", "Following N123")]
    [InlineData("Holding Short RWY 28R", "Holding short RWY 28R")]
    [InlineData("Turn L270", "Turn L270")]
    [InlineData("TurnL270", "Turn L270")]
    [InlineData("TurnR90", "Turn R90")]
    public void For_WithNoTableRow_FallsBackToSentenceCase(string raw, string display) => Assert.Equal(display, PhaseDisplayNames.For(raw));

    [Fact]
    public void For_Empty_IsEmpty() => Assert.Equal(string.Empty, PhaseDisplayNames.For(string.Empty));
}
