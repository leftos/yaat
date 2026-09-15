using Xunit;
using Yaat.Client.Models;

namespace Yaat.Client.Tests;

/// <summary>
/// Pins <see cref="AircraftModel.HasFiledTypeMismatch"/> — the instructor-only hint that the physical
/// aircraft type differs from the filed flight-plan type. The comparison strips the wake prefix and the
/// equipment suffix on both sides, and a blank filed type means "no filed type", never a mismatch.
/// </summary>
public class AircraftModelTypeMismatchTests
{
    [Fact]
    public void Mismatch_WhenFiledTypeDiffersFromPhysicalType()
    {
        var ac = new AircraftModel
        {
            Callsign = "UAL238",
            AircraftType = "A388",
            FiledAircraftType = "B744",
        };

        Assert.True(ac.HasFiledTypeMismatch);
    }

    [Fact]
    public void NoMismatch_WhenOnlyWakePrefixAndEquipmentSuffixDiffer()
    {
        var ac = new AircraftModel
        {
            Callsign = "UAL238",
            AircraftType = "H/B763/L",
            FiledAircraftType = "B763",
        };

        Assert.False(ac.HasFiledTypeMismatch);
    }

    [Fact]
    public void NoMismatch_WhenOnlyEquipmentSuffixDiffers()
    {
        var ac = new AircraftModel
        {
            Callsign = "SWA1",
            AircraftType = "B738/L",
            FiledAircraftType = "B738",
        };

        Assert.False(ac.HasFiledTypeMismatch);
    }

    [Fact]
    public void NoMismatch_WhenFiledTypeIsBlank()
    {
        var ac = new AircraftModel
        {
            Callsign = "N2BP",
            AircraftType = "SR22",
            FiledAircraftType = "",
        };

        Assert.False(ac.HasFiledTypeMismatch);
    }

    [Fact]
    public void NoMismatch_WhenTypesDifferOnlyInCase()
    {
        var ac = new AircraftModel
        {
            Callsign = "BAW1",
            AircraftType = "a388",
            FiledAircraftType = "A388",
        };

        Assert.False(ac.HasFiledTypeMismatch);
    }

    [Fact]
    public void Tooltip_NamesTheFiledType_OnlyWhenMismatched()
    {
        var mismatched = new AircraftModel
        {
            Callsign = "UAL238",
            AircraftType = "A388",
            FiledAircraftType = "B744",
        };
        var matched = new AircraftModel
        {
            Callsign = "SWA1",
            AircraftType = "B738/L",
            FiledAircraftType = "B738",
        };

        Assert.Equal("Filed as B744", mismatched.FiledTypeMismatchTooltip);
        Assert.Null(matched.FiledTypeMismatchTooltip);
    }
}
