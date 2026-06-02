using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// Unit coverage for the instructor NOTE command: parsing (case/spaces preserved, bare clears),
/// canonical + natural describers, the 40-char cap helper, and AircraftState snapshot round-trip.
/// </summary>
public class NoteCommandTests
{
    [Fact]
    public void Note_ParsesFreetext_PreservingCaseAndSpaces()
    {
        var result = CommandParser.Parse("NOTE Watch wake KSFO");
        var cmd = Assert.IsType<NoteCommand>(result.Value);
        Assert.Equal("Watch wake KSFO", cmd.Text);
    }

    [Fact]
    public void Note_Bare_ClearsNote()
    {
        var result = CommandParser.Parse("NOTE");
        var cmd = Assert.IsType<NoteCommand>(result.Value);
        Assert.Equal("", cmd.Text);
    }

    [Fact]
    public void Note_Canonical_RoundTrips()
    {
        Assert.Equal("NOTE foo bar", CommandDescriber.DescribeCommand(new NoteCommand("foo bar")));
        Assert.Equal("NOTE", CommandDescriber.DescribeCommand(new NoteCommand("")));
    }

    [Fact]
    public void Note_MapsToCanonicalType()
    {
        Assert.Equal(CanonicalCommandType.Note, CommandDescriber.ToCanonicalType(new NoteCommand("x")));
    }

    [Fact]
    public void Note_NaturalDescription()
    {
        Assert.Equal("Set note: trainee struggling", CommandDescriber.DescribeNatural(new NoteCommand("trainee struggling")));
        Assert.Equal("Clear note", CommandDescriber.DescribeNatural(new NoteCommand("")));
    }

    [Fact]
    public void TruncateNote_CapsAt40Chars()
    {
        var longText = new string('x', 50);
        Assert.Equal(40, AircraftState.TruncateNote(longText).Length);
        Assert.Equal("short", AircraftState.TruncateNote("short  "));
    }

    [Fact]
    public void Note_SurvivesSnapshotRoundTrip()
    {
        var ac = new AircraftState
        {
            Callsign = "AAL100",
            AircraftType = "B738",
            Note = "Exam: vector to final",
        };

        var restored = AircraftState.FromSnapshot(ac.ToSnapshot(), groundLayout: null);

        Assert.Equal("Exam: vector to final", restored.Note);
    }

    [Fact]
    public void Note_DefaultsToEmptyWhenSnapshotOmitsIt()
    {
        var ac = new AircraftState { Callsign = "AAL100", AircraftType = "B738" };

        var restored = AircraftState.FromSnapshot(ac.ToSnapshot(), groundLayout: null);

        Assert.Equal("", restored.Note);
    }
}
