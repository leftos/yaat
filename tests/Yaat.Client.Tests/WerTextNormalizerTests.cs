using Xunit;
using Yaat.SpeechSandbox;

namespace Yaat.Client.Tests;

/// <summary>
/// The WER-side transcript normalizer: UWB-ATCC writes every number as spelled-out digits and
/// Whisper writes digits, punctuation and NATO spellings, so both sides meet in one comparable form.
/// </summary>
public sealed class WerTextNormalizerTests
{
    [Theory]
    [InlineData("Runway 24R, cleared to land.", "runway two four r cleared to land")]
    [InlineData("FL350", "flight level three five zero")]
    [InlineData("contact Praha 118.1", "contact praha one one eight decimal one")]
    [InlineData("Alpha, Juliet, X-ray, x ray", "alfa juliett xray xray")]
    [InlineData("niner tree fife", "nine three five")]
    [InlineData("take-off", "takeoff")]
    [InlineData("we'll", "well")]
    [InlineData("OKAY", "ok")]
    [InlineData("1,500", "one five zero zero")]
    [InlineData("csa one two three", "csa one two three")]
    [InlineData("Ruzyně", "ruzyne")]
    [InlineData("stand by for push back", "standby for pushback")]
    [InlineData("cleared for take off", "cleared for takeoff")]
    [InlineData("takeoff", "takeoff")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void Normalize_Produces_A_Comparable_Form(string input, string expected) => Assert.Equal(expected, WerTextNormalizer.Normalize(input));
}
