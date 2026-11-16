using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Tests;

/// <summary>
/// The Notes window's view model: the text is saved a second after the last change, at once on a flush or a clear, and the
/// stamp reads the sim time and the selected callsign.
/// </summary>
public sealed class NotesViewModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("notes-vm-").FullName;
    private readonly FakeTimeProvider _clock = new();

    private string FilePath => Path.Combine(_dir, "notes.txt");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void The_Text_Starts_As_The_Stored_Text()
    {
        File.WriteAllText(FilePath, "from last time");

        Assert.Equal("from last time", NewViewModel().Text);
    }

    [Fact]
    public void A_Change_Is_Saved_One_Second_After_It_And_Not_Before()
    {
        NotesViewModel vm = NewViewModel();

        vm.Text = "typed";
        _clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(File.Exists(FilePath));

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal("typed", File.ReadAllText(FilePath));
    }

    [Fact]
    public void A_Second_Change_Inside_The_Second_Restarts_The_Wait()
    {
        NotesViewModel vm = NewViewModel();

        vm.Text = "t";
        _clock.Advance(TimeSpan.FromMilliseconds(600));
        vm.Text = "ty";
        _clock.Advance(TimeSpan.FromMilliseconds(600));
        Assert.False(File.Exists(FilePath));

        _clock.Advance(TimeSpan.FromMilliseconds(400));
        Assert.Equal("ty", File.ReadAllText(FilePath));
    }

    [Fact]
    public void A_Flush_Saves_At_Once()
    {
        NotesViewModel vm = NewViewModel();
        vm.Text = "closing";

        vm.Flush();

        Assert.Equal("closing", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Clear_Empties_The_Text_And_Saves_It_At_Once()
    {
        File.WriteAllText(FilePath, "old notes");
        NotesViewModel vm = NewViewModel();

        vm.ClearCommand.Execute(null);

        Assert.Equal("", vm.Text);
        Assert.Equal("", File.ReadAllText(FilePath));
    }

    [Theory]
    [InlineData("75:05", "N123AB", "[75:05 N123AB] ")]
    [InlineData("75:05", null, "[75:05] ")]
    [InlineData(null, "N123AB", null)]
    [InlineData(null, null, null)]
    public void FormatStamp_Gives_The_Time_And_Callsign_Or_Nothing_Without_A_Scenario(string? simTime, string? callsign, string? expected) =>
        Assert.Equal(expected, NotesViewModel.FormatStamp(simTime, callsign));

    [Theory]
    [InlineData(1, 3)]
    [InlineData(3, 1)]
    public void InsertStamp_Replaces_The_Selection_In_Either_Order_And_Puts_The_Caret_After_It(int start, int end)
    {
        NotesViewModel vm = NewViewModel();
        vm.Text = "abcd";

        int? caret = vm.InsertStamp(start, end);

        Assert.Equal("a[75:05 N123AB] d", vm.Text);
        Assert.Equal(1 + "[75:05 N123AB] ".Length, caret);
    }

    [Fact]
    public void The_Stamp_Cannot_Run_Without_A_Scenario()
    {
        NotesViewModel vm = NewViewModel(simTime: null, callsign: "N123AB");

        Assert.False(vm.StampCommand.CanExecute(null));
    }

    private NotesViewModel NewViewModel(string? simTime = "75:05", string? callsign = "N123AB") =>
        new(new NotesStore(FilePath, NullLogger.Instance), _clock, () => simTime, () => callsign);
}
