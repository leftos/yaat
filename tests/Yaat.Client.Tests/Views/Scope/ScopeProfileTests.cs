using System.Text.Json;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.Views.Scope.Profiles;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// The Scope profiles: the kind test that picks one from the viewer's position, the three shipped profile files, and the
/// passthrough of a field this build does not know.
/// </summary>
public class ScopeProfileTests
{
    private static readonly string[] ShippedToolbar =
    [
        "profile",
        "template",
        "range",
        "others",
        "vector",
        "hist",
        "tools",
        "auto",
        "maps",
        "tdm",
        "mva",
        "display",
        "rr",
        "db",
        "views",
        "brite",
        "pref",
        "deletetearoff",
    ];

    private static ActivePositionDto Position(string positionType, string? eramSectorId) =>
        new(
            Callsign: "TEST_CTR",
            Name: "Test",
            RadioName: null,
            Frequency: "120.000",
            FacilityId: "ZOA",
            FacilityName: "Test Facility",
            PositionType: positionType,
            HandoffCode: "T1",
            EramSectorId: eramSectorId
        );

    [Theory]
    [InlineData("Artcc", "44", ScopeProfileKind.Eram)]
    [InlineData("Tracon", "1", ScopeProfileKind.Eram)]
    [InlineData("Tracon", null, ScopeProfileKind.Stars)]
    [InlineData("Atct", null, ScopeProfileKind.Stars)]
    [InlineData("Artcc", null, ScopeProfileKind.Mixed)]
    [InlineData("Other", null, ScopeProfileKind.Mixed)]
    public void KindFor_PicksByTheSectorThenThePositionType(string positionType, string? eramSectorId, ScopeProfileKind expected) =>
        Assert.Equal(expected, ScopeProfilePicker.KindFor(Position(positionType, eramSectorId)));

    [Fact]
    public void KindFor_NoPosition_IsMixed() => Assert.Equal(ScopeProfileKind.Mixed, ScopeProfilePicker.KindFor(null));

    [Fact]
    public void ShippedMixed_IsTheCanvasProfile()
    {
        ScopeProfile mixed = ScopeProfileStore.Shipped(ScopeProfileKind.Mixed);

        Assert.Equal("mixed", mixed.Name);
        Assert.Equal("mixed", mixed.Kind);
        Assert.Equal("mixed", mixed.Template);
        Assert.Equal(
            [
                new ScopeZoomBand(30, "full", 0, 5, "jring"),
                new ScopeZoomBand(120, "partial", 1, 5, "jring"),
                new ScopeZoomBand(null, "partial", 4, 3, "dri"),
            ],
            mixed.Bands
        );
        Assert.Equal(ShippedToolbar, mixed.Toolbar);
        Assert.Equal(["ssa", "ca"], mixed.Views);
    }

    [Fact]
    public void ShippedStars_IsMixedUpTo120Nm_ThenPartialWithJRings()
    {
        ScopeProfile stars = ScopeProfileStore.Shipped(ScopeProfileKind.Stars);

        Assert.Equal(("stars", "stars", "stars"), (stars.Name, stars.Kind, stars.Template));
        Assert.Equal(
            [
                new ScopeZoomBand(30, "full", 0, 5, "jring"),
                new ScopeZoomBand(120, "partial", 1, 5, "jring"),
                new ScopeZoomBand(null, "partial", 1, 5, "jring"),
            ],
            stars.Bands
        );
        Assert.Equal(ShippedToolbar, stars.Toolbar);
        Assert.Equal(["ssa", "ca"], stars.Views);
    }

    [Fact]
    public void ShippedEram_IsOneBandWithDriHalos()
    {
        ScopeProfile eram = ScopeProfileStore.Shipped(ScopeProfileKind.Eram);

        Assert.Equal(("eram", "eram", "eram"), (eram.Name, eram.Kind, eram.Template));
        Assert.Equal([new ScopeZoomBand(null, "partial", 1, 5, "dri")], eram.Bands);
        Assert.Equal(ShippedToolbar, eram.Toolbar);
        Assert.Equal(["ssa", "ca"], eram.Views);
    }

    [Fact]
    public void UnknownField_RoundTripsThroughExtra()
    {
        const string json = """
            {
              "name": "mixed",
              "kind": "mixed",
              "bands": [{ "maxNm": null, "othersLevel": "full", "vectorMin": 0, "history": 5, "rings": "jring" }],
              "template": "mixed",
              "toolbar": [],
              "views": [],
              "orientation": "magnetic"
            }
            """;

        ScopeProfile profile = JsonSerializer.Deserialize<ScopeProfile>(json, ScopeProfile.JsonOptions)!;
        string written = JsonSerializer.Serialize(profile, ScopeProfile.JsonOptions);
        using var document = JsonDocument.Parse(written);

        Assert.Equal("magnetic", profile.Extra!["orientation"].GetString());
        Assert.Equal("magnetic", document.RootElement.GetProperty("orientation").GetString());
        Assert.Equal("full", document.RootElement.GetProperty("bands")[0].GetProperty("othersLevel").GetString());
    }
}
