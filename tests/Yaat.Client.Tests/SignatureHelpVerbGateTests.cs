using Xunit;
using Yaat.Client.Services;
using Yaat.Sim.Commands;

namespace Yaat.Client.Tests;

public class SignatureHelpVerbGateTests
{
    private static readonly CommandScheme Scheme = CommandScheme.Default();

    [Fact]
    public void BareVerb_NoTrailingSpace_DoesNotShowSignatureHelp()
    {
        var c = new CommandInputController();
        c.UpdateSignatureHelp("FH", "FH".Length, Scheme);
        Assert.False(c.SignatureHelp.IsVisible);
    }

    [Fact]
    public void BareVerb_WithTrailingSpace_ShowsSignatureHelp()
    {
        var c = new CommandInputController();
        c.UpdateSignatureHelp("FH ", "FH ".Length, Scheme);
        Assert.True(c.SignatureHelp.IsVisible);
    }

    [Fact]
    public void CallsignThenVerb_NoTrailingSpace_DoesNotShowSignatureHelp()
    {
        var c = new CommandInputController();
        c.UpdateSignatureHelp("UAL1 FH", "UAL1 FH".Length, Scheme);
        // The verb has just been typed after the callsign; no space after the verb yet.
        // Contract: signature help waits until the user types past the verb.
        Assert.False(c.SignatureHelp.IsVisible);
    }
}
