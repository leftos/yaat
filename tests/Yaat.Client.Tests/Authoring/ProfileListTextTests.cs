using System.Globalization;
using Avalonia.Data;
using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Client.Views.Authoring;
using Yaat.Sim.Authoring.Stamping;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The Apply Route window's profile list text and number fields: each list format reads back what it writes, a STAR
/// alias line that is not two words and a preset line that is a lone number or whose seconds are signed or too large for
/// an int are problems named by line, and a number field reads blank as null, finite digits as the number and anything
/// else as a validation error.
/// </summary>
public sealed class ProfileListTextTests
{
    [Fact]
    public void Pool_RoundTrips()
    {
        string text = ProfileListText.JoinPool(["B738", "A320"]);

        Assert.Equal("B738, A320", text);
        Assert.Equal(["B738", "A320"], ProfileListText.SplitPool(text));
    }

    [Fact]
    public void Aliases_RoundTrip()
    {
        StarAlias[] aliases = [new("SERFR4", "SERFR5"), new("BDEGA3", "BDEGA4")];

        ProfileListRead<StarAlias> read = ProfileListText.ReadAliases(ProfileListText.JoinAliases(aliases));

        Assert.Equal(aliases, read.Items);
        Assert.Empty(read.Problems);
    }

    [Fact]
    public void Presets_RoundTrip()
    {
        StampPresetCommand[] presets = [new("CM 50", 30), new("SAY {CS}", 0)];

        ProfileListRead<StampPresetCommand> read = ProfileListText.ReadPresets(ProfileListText.JoinPresets(presets));

        Assert.Equal(presets, read.Items);
        Assert.Empty(read.Problems);
    }

    [Theory]
    [InlineData("MOD")]
    [InlineData("A B C")]
    public void Aliases_ALineThatIsNotTwoWords_IsAProblem(string text)
    {
        ProfileListRead<StarAlias> read = ProfileListText.ReadAliases(text);

        Assert.Empty(read.Items);
        Assert.Equal(["Line 1 of STAR aliases: write the STAR and the STAR replacing it, two words"], read.Problems);
    }

    [Theory]
    [InlineData("5 CM 50", 5, "CM 50")]
    [InlineData("0 CM 50", 0, "CM 50")]
    [InlineData("CM 50", 0, "CM 50")]
    public void Presets_WholeSecondsThenACommand_RunAtThatOffset(string text, int seconds, string command)
    {
        ProfileListRead<StampPresetCommand> read = ProfileListText.ReadPresets(text);

        Assert.Equal([new StampPresetCommand(command, seconds)], read.Items);
        Assert.Empty(read.Problems);
    }

    [Theory]
    [InlineData("30", "Line 1 of preset commands: a command must follow the seconds")]
    [InlineData("5", "Line 1 of preset commands: a command must follow the seconds")]
    public void Presets_ALoneNumber_IsAProblem(string text, string problem)
    {
        ProfileListRead<StampPresetCommand> read = ProfileListText.ReadPresets(text);

        Assert.Empty(read.Items);
        Assert.Equal([problem], read.Problems);
    }

    [Theory]
    [InlineData("-5 CM 50", "Line 1 of preset commands: the seconds can't be negative")]
    [InlineData("+5 CM 50", "Line 1 of preset commands: write the seconds without a sign")]
    [InlineData("3000000000 CM 50", "Line 1 of preset commands: the seconds are too large")]
    public void Presets_ASignedOrOverlongSecondsToken_IsAProblem(string text, string problem)
    {
        ProfileListRead<StampPresetCommand> read = ProfileListText.ReadPresets(text);

        Assert.Empty(read.Items);
        Assert.Equal([problem], read.Problems);
    }

    [Fact]
    public void Presets_AProblemNamesItsLine_AndTheOtherLinesStillRead()
    {
        ProfileListRead<StampPresetCommand> read = ProfileListText.ReadPresets("10 CM 50\n\n30\nSAY HI");

        Assert.Equal([new StampPresetCommand("CM 50", 10), new StampPresetCommand("SAY HI", 0)], read.Items);
        Assert.Equal(["Line 3 of preset commands: a command must follow the seconds"], read.Problems);
    }

    [Fact]
    public void Number_Blank_IsNull() => Assert.Null(ConvertBack("  ", typeof(int?)));

    [Fact]
    public void Number_Digits_AreTheNumber()
    {
        Assert.Equal(2500, ConvertBack("2500", typeof(int?)));
        Assert.Equal(1.5, ConvertBack("1.5", typeof(double?)));
        Assert.Equal("2500", ApplyRouteWindow.Number.Convert(2500, typeof(string), null, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("abc", typeof(double?))]
    [InlineData("1.5", typeof(int?))]
    [InlineData("NaN", typeof(double?))]
    [InlineData("Infinity", typeof(double?))]
    public void Number_AnythingElse_IsAValidationError(string text, Type target)
    {
        BindingNotification error = Assert.IsType<BindingNotification>(ConvertBack(text, target));
        Assert.Equal(BindingErrorType.DataValidationError, error.ErrorType);
    }

    private static object? ConvertBack(string text, Type target) =>
        ApplyRouteWindow.Number.ConvertBack(text, target, null, CultureInfo.InvariantCulture);
}
