using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Tests;

/// <summary>
/// Pure-function coverage for <see cref="VTdlsCanonicalBuilder"/> — every vTDLS
/// UI gesture funnels through one of these builders, so they must match the
/// canonical syntax the server's <c>TdlsCommandHandler</c> accepts (see
/// <c>CommandRegistry</c> entries for TDLSQ / TDLSS / TDLSW / TDLSDUMP).
/// </summary>
public class VTdlsCanonicalBuilderTests
{
    [Fact]
    public void BuildQueue_IsStaticVerb()
    {
        Assert.Equal("TDLSQ", VTdlsCanonicalBuilder.BuildQueue());
    }

    [Fact]
    public void BuildWilco_IsStaticVerb()
    {
        Assert.Equal("TDLSW", VTdlsCanonicalBuilder.BuildWilco());
    }

    [Fact]
    public void BuildDump_IsStaticVerb()
    {
        Assert.Equal("TDLSDUMP", VTdlsCanonicalBuilder.BuildDump());
    }

    [Fact]
    public void BuildSend_PipeSeparatesNineFieldsInCanonicalOrder()
    {
        var clearance = new ClearanceDto(
            Expect: "10 MIN",
            Sid: "OAKLAND4",
            Transition: "ALTAM",
            Climbout: "ON COURSE",
            Climbvia: "EXCEPT MAINTAIN 5000",
            InitialAlt: "5000",
            ContactInfo: "OAK DEP",
            LocalInfo: "RWY 28L",
            DepFreq: "120.9"
        );

        var canonical = VTdlsCanonicalBuilder.BuildSend(clearance);

        Assert.Equal("TDLSS 10 MIN|OAKLAND4|ALTAM|ON COURSE|EXCEPT MAINTAIN 5000|5000|OAK DEP|120.9|RWY 28L", canonical);
    }

    [Fact]
    public void BuildSend_NullFieldsBecomeEmptyTokens()
    {
        var clearance = new ClearanceDto(
            Expect: null,
            Sid: "OAKLAND4",
            Transition: null,
            Climbout: null,
            Climbvia: null,
            InitialAlt: "5000",
            ContactInfo: null,
            LocalInfo: null,
            DepFreq: "120.9"
        );

        // Order: Expect | Sid | Transition | Climbout | Climbvia | InitialAlt | ContactInfo | DepFreq | LocalInfo
        Assert.Equal("TDLSS |OAKLAND4||||5000||120.9|", VTdlsCanonicalBuilder.BuildSend(clearance));
    }
}
