using Xunit;
using Yaat.Client.ContextMenus;

namespace Yaat.Client.Tests;

/// <summary>
/// The solo advisory box's reading of the typed call (<see cref="SoloAdvisoryText"/>): the form the first word picks, the parts
/// still unset and the next one, the filter a first word that starts no form becomes, and the command preview.
/// </summary>
public class SoloAdvisoryTextTests
{
    [Theory]
    [InlineData("2 4 w sr22 35", AdvisoryForm.Radar)]
    [InlineData("2 4 w sr22", AdvisoryForm.Radar)]
    [InlineData("nr 2 c172", AdvisoryForm.Nose)]
    [InlineData("base l 2 28l c152", AdvisoryForm.Pattern)]
    [InlineData("final 2 28r c172", AdvisoryForm.Pattern)]
    [InlineData("over vpcol sr22", AdvisoryForm.Over)]
    public void CompleteCall_OfEachForm_IsCompleteAndParses(string typed, AdvisoryForm form)
    {
        AdvisoryReading reading = SoloAdvisoryText.Read(typed);

        Assert.Equal(form, reading.Form);
        Assert.True(reading.IsComplete);
        Assert.Empty(reading.Missing);
        Assert.True(SoloAdvisoryText.Parse(reading).IsSuccess);
    }

    [Theory]
    [InlineData("2 4", "Direction,Type", AdvisoryPart.Direction)]
    [InlineData("2 4 w sr22", "", AdvisoryPart.Altitude)]
    [InlineData("13 4 w c172", "Clock", AdvisoryPart.Clock)]
    [InlineData("nr 2", "Type", AdvisoryPart.Type)]
    [InlineData("dw", "Side,Miles,Runway,Type", AdvisoryPart.Side)]
    [InlineData("final 2", "Runway,Type", AdvisoryPart.Runway)]
    [InlineData("over", "Fix,Type", AdvisoryPart.Fix)]
    public void Partial_NamesTheMissingPartsAndTheNext(string typed, string missing, AdvisoryPart next)
    {
        AdvisoryReading reading = SoloAdvisoryText.Read(typed);

        Assert.Equal(missing.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Enum.Parse<AdvisoryPart>), reading.Missing);
        Assert.Equal(next, reading.Next);
    }

    [Fact]
    public void LettersFirst_FilterTheList()
    {
        AdvisoryReading reading = SoloAdvisoryText.Read("n22");

        Assert.Equal("N22", reading.FilterText);
        Assert.Null(reading.Form);
        Assert.Null(reading.Next);
        Assert.False(reading.IsComplete);
    }

    [Fact]
    public void NothingTyped_IsEmpty()
    {
        AdvisoryReading reading = SoloAdvisoryText.Read("   ");

        Assert.True(reading.IsEmpty);
        Assert.Null(reading.Form);
        Assert.Null(reading.FilterText);
    }

    [Theory]
    [InlineData(AdvisoryPart.Clock, "clock")]
    [InlineData(AdvisoryPart.Miles, "miles")]
    [InlineData(AdvisoryPart.Direction, "direction")]
    [InlineData(AdvisoryPart.Type, "type")]
    [InlineData(AdvisoryPart.Altitude, "altitude")]
    [InlineData(AdvisoryPart.Position, "position off the nose")]
    [InlineData(AdvisoryPart.Leg, "leg")]
    [InlineData(AdvisoryPart.Side, "side")]
    [InlineData(AdvisoryPart.Runway, "runway")]
    [InlineData(AdvisoryPart.Fix, "reporting point")]
    public void KindWord_NamesEachPart(AdvisoryPart part, string word) => Assert.Equal(word, SoloAdvisoryText.KindWord(part));

    [Theory]
    [InlineData("", "RTIS ···")]
    [InlineData("n22", "RTIS ···")]
    [InlineData("2 4", "RTIS 2 4 ‹direction› ‹type›")]
    [InlineData("2 4 w sr22 035", "RTIS 2 4 W SR22 035")]
    [InlineData("final 2", "RTIS FINAL 2 ‹runway› ‹type›")]
    [InlineData("over", "RTIS OVER ‹reporting point› ‹type›")]
    public void Preview_ShowsUnsetPartsAsGaps(string typed, string preview) =>
        Assert.Equal(preview, SoloAdvisoryText.Preview(SoloAdvisoryText.Read(typed)));

    [Fact]
    public void Command_UpperCasesAndSingleSpacesTheTypedWords() =>
        Assert.Equal("RTIS 2 4 W SR22", SoloAdvisoryText.Command(SoloAdvisoryText.Read(" 2  4 w   sr22 ")));
}
