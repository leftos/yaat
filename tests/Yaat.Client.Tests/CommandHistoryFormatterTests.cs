using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

public class CommandHistoryFormatterTests
{
    [Fact]
    public void PartialCallsignPrefix_RewritesToCanonical()
    {
        var result = CommandHistoryFormatter.Format("436 ERD 28R", resolvedCallsign: "N436MS", canonicalCommand: "ERD 28R");

        Assert.Equal("N436MS ERD 28R", result);
    }

    [Fact]
    public void AlreadyCanonical_NoVisibleChange()
    {
        var result = CommandHistoryFormatter.Format("N436MS ERD 28R", resolvedCallsign: "N436MS", canonicalCommand: "ERD 28R");

        Assert.Equal("N436MS ERD 28R", result);
    }

    [Fact]
    public void ImplicitTarget_PreservesRaw()
    {
        var result = CommandHistoryFormatter.Format("ERD 28R", resolvedCallsign: null, canonicalCommand: "ERD 28R");

        Assert.Equal("ERD 28R", result);
    }

    [Fact]
    public void ImplicitTarget_PreservesRawLowercase()
    {
        var result = CommandHistoryFormatter.Format("fh 270", resolvedCallsign: null, canonicalCommand: "FH 270");

        Assert.Equal("fh 270", result);
    }

    [Fact]
    public void LowercaseCommandWithPrefix_UsesCanonicalCommand()
    {
        var result = CommandHistoryFormatter.Format("436 erd 28R", resolvedCallsign: "N436MS", canonicalCommand: "ERD 28R");

        Assert.Equal("N436MS ERD 28R", result);
    }

    [Fact]
    public void ArgumentCallsignRewrite_ReflectedInCanonical()
    {
        var result = CommandHistoryFormatter.Format("AAL123 FOLLOW SWA", resolvedCallsign: "AAL123", canonicalCommand: "FOLLOW SWA456");

        Assert.Equal("AAL123 FOLLOW SWA456", result);
    }

    [Fact]
    public void CompoundCommand_UsesCanonicalSeparators()
    {
        var result = CommandHistoryFormatter.Format("436 fh 270, cm 240", resolvedCallsign: "N436MS", canonicalCommand: "FH 270, CM 240");

        Assert.Equal("N436MS FH 270, CM 240", result);
    }
}
