using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

/// <summary>
/// Tests for <see cref="SpeechRecognitionService.ExtractAndStripCallsign"/> — the pipeline
/// step that pulls the callsign out of a Whisper transcript before the transcript is handed to
/// the rule or LLM command mappers. Both the returned ICAO and the stripped-down command text
/// are validated.
/// </summary>
public class SpeechCallsignExtractionTests
{
    [Fact]
    public void Leading_Airline_Returns_IcaoCallsign_And_StrippedText()
    {
        string[] active = ["SWA123"];
        var (commandText, callsign) = SpeechRecognitionService.ExtractAndStripCallsign("southwest one two three fly heading two seven zero", active);
        Assert.Equal("SWA123", callsign);
        Assert.Equal("fly heading 270", commandText);
    }

    [Fact]
    public void Trailing_Airline_Returns_IcaoCallsign_And_StrippedText()
    {
        string[] active = ["SWA123"];
        var (commandText, callsign) = SpeechRecognitionService.ExtractAndStripCallsign("fly heading two seven zero southwest one two three", active);
        Assert.Equal("SWA123", callsign);
        Assert.Equal("fly heading 270", commandText);
    }

    [Fact]
    public void Us_Ga_Digits_Plus_NatoLetter_Returns_IcaoAndStripped()
    {
        string[] active = ["N346G"];
        var (commandText, callsign) = SpeechRecognitionService.ExtractAndStripCallsign(
            "november three four six golf turn left heading three one zero",
            active
        );
        Assert.Equal("N346G", callsign);
        Assert.Equal("turn left heading 310", commandText);
    }

    [Fact]
    public void Us_Ga_Digits_Plus_MisheardSuffix_Recovers_FullCallsign()
    {
        // Whisper transcribed the suffix as "gulf" (a one-edit mishear of "golf"). The parser
        // must still recover N346G — stopping at "N346" would leave the trailing "gulf" in the
        // command text and cause the canonical to dispatch against a non-existent callsign.
        // See the S2-OAK-1 bug report: "november three four six gulf runway 28R cleared for takeoff".
        string[] active = ["N346G"];
        var (commandText, callsign) = SpeechRecognitionService.ExtractAndStripCallsign(
            "november three four six gulf runway two eight right cleared for takeoff",
            active
        );
        Assert.Equal("N346G", callsign);
        Assert.Equal("runway 28R cleared for takeoff", commandText);
    }

    [Fact]
    public void Unknown_Telephony_Returns_NormalizedTranscript_NullCallsign()
    {
        string[] active = ["SWA123"];
        var (commandText, callsign) = SpeechRecognitionService.ExtractAndStripCallsign("fly heading two seven zero", active);
        Assert.Null(callsign);
        Assert.Equal("fly heading 270", commandText);
    }

    [Fact]
    public void Empty_Transcript_Returns_Empty_NullCallsign()
    {
        var (commandText, callsign) = SpeechRecognitionService.ExtractAndStripCallsign("", ["SWA123"]);
        Assert.Null(callsign);
        Assert.Equal("", commandText);
    }

    [Fact]
    public void Us_Ga_HybridForm_StripsTwoTokens()
    {
        // "november N9225L climb and maintain 2000" — Whisper emitted the hybrid form.
        string[] active = ["N9225L"];
        var (commandText, callsign) = SpeechRecognitionService.ExtractAndStripCallsign("november N9225L climb and maintain 2000", active);
        Assert.Equal("N9225L", callsign);
        // "n9225l" is in the tokenized/normalized form — the mapper will see lowercased input.
        Assert.Equal("climb and maintain 2000", commandText);
    }
}
