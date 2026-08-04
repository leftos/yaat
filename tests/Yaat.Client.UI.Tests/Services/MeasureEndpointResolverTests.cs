using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests;

// Covers endpoint resolution for the ".rbl A B" / "*T A B" measuring command: exact callsign, then
// fix/FRD, then partial callsign, with ambiguity and navdata-not-ready reporting.
public class MeasureEndpointResolverTests
{
    private static readonly LatLon Oakland = new(37.7213, -122.2208);

    private static AircraftModel Plane(string callsign) => new() { Callsign = callsign };

    [Fact]
    public void ExactCallsignWinsEvenWhenTheTokenIsAlsoAFix()
    {
        var (endpoint, error) = MeasureEndpointResolver.Resolve("UAL123", [Plane("UAL123")], _ => Oakland);

        Assert.Null(error);
        Assert.True(endpoint!.Value.IsLatched);
        Assert.Equal("UAL123", endpoint.Value.Callsign);
    }

    [Fact]
    public void FixWinsOverAPartialCallsignMatch()
    {
        // "OAK" is a substring of the callsign but names a fix — the fix must win, or common navaids
        // become untypeable while certain aircraft are on frequency.
        var (endpoint, error) = MeasureEndpointResolver.Resolve("OAK", [Plane("N123OAK")], _ => Oakland);

        Assert.Null(error);
        Assert.False(endpoint!.Value.IsLatched);
        Assert.Equal("OAK", endpoint.Value.Label);
        Assert.Equal(Oakland.Lat, endpoint.Value.FixedPosition.Lat, 6);
    }

    [Fact]
    public void PartialCallsignResolvesWhenTheTokenIsNotAFix()
    {
        var (endpoint, error) = MeasureEndpointResolver.Resolve("123", [Plane("UAL123"), Plane("SWA45")], _ => null);

        Assert.Null(error);
        Assert.Equal("UAL123", endpoint!.Value.Callsign);
    }

    [Fact]
    public void FixLabelIsUppercased()
    {
        var (endpoint, _) = MeasureEndpointResolver.Resolve("oak169015", [], _ => Oakland);

        Assert.Equal("OAK169015", endpoint!.Value.Label);
    }

    [Fact]
    public void AmbiguousPartialCallsignReportsTheCandidates()
    {
        var (endpoint, error) = MeasureEndpointResolver.Resolve("UAL", [Plane("UAL123"), Plane("UAL456")], _ => null);

        Assert.Null(endpoint);
        Assert.Contains("UAL123", error, StringComparison.Ordinal);
        Assert.Contains("UAL456", error, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownTokenReportsAnError()
    {
        var (endpoint, error) = MeasureEndpointResolver.Resolve("nope", [Plane("UAL123")], _ => null);

        Assert.Null(endpoint);
        Assert.Equal("Unknown fix or callsign: NOPE", error);
    }

    [Fact]
    public void UnknownTokenWhileNavdataLoadsSaysFixesAreUnavailable()
    {
        var (endpoint, error) = MeasureEndpointResolver.Resolve("MOD", [], null);

        Assert.Null(endpoint);
        Assert.Contains("navdata still loading", error, StringComparison.Ordinal);
    }
}
