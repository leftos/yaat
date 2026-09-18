using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

public class CommandHistoryFormatterTests
{
    [Fact]
    public void PartialCallsignPrefix_StripsCallsignAndCanonicalizes()
    {
        string result = CommandHistoryFormatter.Format("436 ERD 28R", resolvedCallsign: "N436MS", canonicalCommand: "ERD 28R");

        Assert.Equal("ERD 28R", result);
    }

    [Fact]
    public void FullCallsignPrefix_StripsCallsign()
    {
        string result = CommandHistoryFormatter.Format("N436MS ERD 28R", resolvedCallsign: "N436MS", canonicalCommand: "ERD 28R");

        Assert.Equal("ERD 28R", result);
    }

    [Fact]
    public void ImplicitTarget_PreservesRaw()
    {
        string result = CommandHistoryFormatter.Format("ERD 28R", resolvedCallsign: null, canonicalCommand: "ERD 28R");

        Assert.Equal("ERD 28R", result);
    }

    [Fact]
    public void ImplicitTarget_UppercasesRawInput()
    {
        string result = CommandHistoryFormatter.Format("fh 270", resolvedCallsign: null, canonicalCommand: "FH 270");

        Assert.Equal("FH 270", result);
    }

    [Fact]
    public void LowercaseCommandWithPrefix_UsesCanonicalCommand()
    {
        string result = CommandHistoryFormatter.Format("436 erd 28R", resolvedCallsign: "N436MS", canonicalCommand: "ERD 28R");

        Assert.Equal("ERD 28R", result);
    }

    [Fact]
    public void ArgumentCallsignRewrite_KeepsCanonicalArgument()
    {
        string result = CommandHistoryFormatter.Format("AAL123 FOLLOW SWA", resolvedCallsign: "AAL123", canonicalCommand: "FOLLOW SWA456");

        Assert.Equal("FOLLOW SWA456", result);
    }

    [Fact]
    public void CompoundCommand_StripsLeadingCallsign()
    {
        string result = CommandHistoryFormatter.Format("436 fh 270, cm 240", resolvedCallsign: "N436MS", canonicalCommand: "FH 270, CM 240");

        Assert.Equal("FH 270, CM 240", result);
    }
}
