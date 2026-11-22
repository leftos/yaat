using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The Airport Editor asks vNAS for a tower-cab image under the ARTCC whose bundled lateral boundary holds the map's
/// centre, else under the user's ARTCC preference, else under none.
/// </summary>
public sealed class AirportEditorArtccResolverTests
{
    private static readonly LatLon Koak = new(37.7213, -122.2208);

    // Mid South Atlantic, off every US center's boundary.
    private static readonly LatLon Outside = new(-30, -10);

    [Fact]
    public void KoaksCentre_IsInZoa_WhateverThePreference()
    {
        Assert.Equal("ZOA", AirportEditorArtccResolver.Resolve(Koak, "ZLA"));
        Assert.Equal("ZOA", AirportEditorArtccResolver.Resolve(Koak, null));
    }

    [Fact]
    public void APointOutsideEveryBoundary_TakesThePreference() => Assert.Equal("ZLA", AirportEditorArtccResolver.Resolve(Outside, "ZLA"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void APointOutsideEveryBoundary_WithNoPreference_HasNoArtcc(string? preference) =>
        Assert.Null(AirportEditorArtccResolver.Resolve(Outside, preference));
}
