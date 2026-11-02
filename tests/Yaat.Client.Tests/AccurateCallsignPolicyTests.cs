using Xunit;
using Yaat.Client.Services;
using Yaat.Sim.Speech.Context;

namespace Yaat.Client.Tests;

/// <summary>
/// The callsign an unresolved accurate decode may accept: the decoder's best guess on a say-again, a callsign outside the
/// room on a no-callsign, and nothing else, so a doubtful callsign never addresses a command to the wrong aircraft.
/// </summary>
public sealed class AccurateCallsignPolicyTests
{
    private static readonly string[] Room = ["UAL12", "DAL34"];

    [Theory]
    [InlineData("DAL34", "DAL34")]
    [InlineData("DAL34", "dal34")]
    public void SayAgain_Accepts_The_Decoders_Best_Guess(string bestGuess, string candidate) =>
        Assert.Equal(candidate, SpeechRecognitionService.AcceptUnresolvedCallsign(DecodeState.SayAgain, bestGuess, candidate, Room));

    [Fact]
    public void SayAgain_Rejects_Another_Callsign() =>
        Assert.Null(SpeechRecognitionService.AcceptUnresolvedCallsign(DecodeState.SayAgain, "DAL34", "UAL12", Room));

    [Fact]
    public void SayAgain_Without_A_Best_Guess_Accepts_Nothing() =>
        Assert.Null(SpeechRecognitionService.AcceptUnresolvedCallsign(DecodeState.SayAgain, null, "UAL12", Room));

    [Theory]
    [InlineData("UAL12")]
    [InlineData("ual12")]
    public void NoCallsign_Rejects_A_Room_Callsign(string candidate) =>
        Assert.Null(SpeechRecognitionService.AcceptUnresolvedCallsign(DecodeState.NoCallsign, null, candidate, Room));

    [Fact]
    public void NoCallsign_Accepts_A_Callsign_Outside_The_Room() =>
        Assert.Equal("N346G", SpeechRecognitionService.AcceptUnresolvedCallsign(DecodeState.NoCallsign, null, "N346G", Room));

    [Fact]
    public void No_Candidate_Is_Accepted()
    {
        Assert.Null(SpeechRecognitionService.AcceptUnresolvedCallsign(DecodeState.SayAgain, "DAL34", null, Room));
        Assert.Null(SpeechRecognitionService.AcceptUnresolvedCallsign(DecodeState.NoCallsign, null, string.Empty, Room));
    }

    [Fact]
    public void A_Resolved_Decode_Is_Never_Filtered() =>
        Assert.Throws<ArgumentException>(() => SpeechRecognitionService.AcceptUnresolvedCallsign(DecodeState.Resolved, "DAL34", "DAL34", Room));
}
