using Xunit;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for the contributor aircraft-performance override layer: AircraftProfileOverrides.json,
/// the partial-merge in <see cref="AircraftProfileOverride.ApplyTo"/>, the authoritative
/// <see cref="OverrideAwareProfileCorrectionAdapter"/>, and the seeded SF50 correction.
/// </summary>
public sealed class AircraftProfileOverrideTests
{
    // Pin the singletons (NavigationDatabase, AircraftProfileDatabase, FAA ACD, etc.) before any
    // test body reads them — see CLAUDE.md "Static singleton races".
    public AircraftProfileOverrideTests() => TestVnasData.EnsureInitialized();

    // --- SF50 end-to-end: the seeded correction ---

    [Fact]
    public void Sf50_HasEffectiveProfile_AfterOverrideSeed()
    {
        // Before the override layer the SF50 had no profile (generic-jet category fallback).
        // The seed gives it a real effective profile.
        Assert.NotNull(AircraftProfileDatabase.Get("SF50"));
    }

    [Fact]
    public void Sf50_ClimbSpeed_UsesOverride_NotCategoryDefault()
    {
        // The generic jet default below 10k is 250 KIAS — the reporter's complaint. The SF50
        // climbs at 170.
        double climb = AircraftPerformance.ClimbSpeed("SF50", AircraftCategory.Jet, 5000);
        Assert.Equal(170, climb, 1);
    }

    [Fact]
    public void Sf50_InitialClimbSpeed_IsAuthoritative_NotEurocontrolCapped()
    {
        // SF50 FAA ACD Vref is 87, so the Eurocontrol jet climb-speed cap (87 × 1.40 ≈ 122) would
        // pull a normal profile's 170 down. The authoritative override bypasses the cap.
        double vy = AircraftPerformance.InitialClimbSpeed("SF50", AircraftCategory.Jet);
        Assert.Equal(170, vy, 1);
        Assert.True(vy > 130, $"override must bypass the Eurocontrol climb-speed cap (~122); was {vy}");
    }

    [Fact]
    public void Sf50_InitialClimbRate_UsesOverride_NotCategoryDefault()
    {
        // Generic jet initial climb rate is 3000 fpm; the SF50 climbs ~1500.
        double rate = AircraftPerformance.InitialClimbRate("SF50", AircraftCategory.Jet);
        Assert.Equal(1500, rate, 1);
    }

    [Fact]
    public void Sf50_DownwindSpeed_UsesOverride_NotEurocontrolFloor()
    {
        // patternSpeed override of 120 must be authoritative — Eurocontrol would floor pattern to
        // max(baseline 200, Vref 87 × 1.10) = 200.
        double downwind = AircraftPerformance.DownwindSpeed("SF50", AircraftCategory.Jet);
        Assert.Equal(120, downwind, 1);
    }

    [Fact]
    public void Sf50_ApproachSpeed_ComesFromAcd_NotOverridden()
    {
        // finalApproachSpeed is intentionally left out of the override — the FAA ACD Vref (87) is
        // already authoritative, so the override only corrects what the pipeline gets wrong.
        double fas = AircraftPerformance.ApproachSpeed("SF50", AircraftCategory.Jet);
        Assert.Equal(87, fas, 1);
        Assert.False(AircraftProfileDatabase.IsOverridden("SF50", nameof(AircraftProfile.FinalApproachSpeed)));
    }

    [Fact]
    public void Sf50_Ceiling_UsesOverride()
    {
        Assert.Equal(31000, AircraftPerformance.Ceiling("SF50"));
    }

    [Fact]
    public void Sf50_UnspecifiedField_FallsToCategoryBaseline()
    {
        // SF50 has no base profile and no sibling, so the override merges onto a synthesized jet
        // category baseline. groundAccelRate is not in the override → it comes from the baseline
        // (CategoryPerformance.GroundAccelRate(Jet) = 5).
        var profile = AircraftProfileDatabase.Get("SF50");
        Assert.NotNull(profile);
        Assert.Equal(5, profile.GroundAccelRate);
    }

    // --- IsOverridden tracking ---

    [Fact]
    public void IsOverridden_TrueForSpecified_FalseForUnspecifiedAndUnseededTypes()
    {
        Assert.True(AircraftProfileDatabase.IsOverridden("SF50", nameof(AircraftProfile.ClimbSpeedInitial)));
        Assert.True(AircraftProfileDatabase.IsOverridden("SF50", nameof(AircraftProfile.PatternSpeed)));
        Assert.False(AircraftProfileDatabase.IsOverridden("SF50", nameof(AircraftProfile.FinalApproachSpeed)));
        Assert.False(AircraftProfileDatabase.IsOverridden("B738", nameof(AircraftProfile.ClimbSpeedInitial)));
    }

    [Fact]
    public void IsOverridden_StripsTypePrefix()
    {
        // A wake/equipment prefix on the type must still resolve the override set.
        Assert.True(AircraftProfileDatabase.IsOverridden("H/SF50", nameof(AircraftProfile.ClimbSpeedInitial)));
    }

    // --- ApplyTo partial merge (pure unit) ---

    [Fact]
    public void ApplyTo_OverridesOnlySpecifiedFields()
    {
        var baseProfile = new AircraftProfile
        {
            TypeCode = "TEST",
            ClimbSpeedInitial = 250,
            FinalApproachSpeed = 140,
            PatternSpeed = 200,
            Ceiling = 41000,
        };
        var ov = new AircraftProfileOverride { TypeCode = "TEST", ClimbSpeedInitial = 170 };

        var (merged, fields) = ov.ApplyTo(baseProfile);

        Assert.Equal(170, merged.ClimbSpeedInitial); // overridden
        Assert.Equal(140, merged.FinalApproachSpeed); // untouched
        Assert.Equal(200, merged.PatternSpeed); // untouched
        Assert.Equal(41000, merged.Ceiling); // untouched
        Assert.Contains(nameof(AircraftProfile.ClimbSpeedInitial), fields);
        Assert.DoesNotContain(nameof(AircraftProfile.FinalApproachSpeed), fields);
        Assert.DoesNotContain(nameof(AircraftProfile.PatternSpeed), fields);
    }

    [Fact]
    public void ApplyTo_ExplicitZero_IsAnOverride_NotIgnored()
    {
        // 0 is meaningful in AircraftProfile (e.g. "can't reach"), so an explicit 0 must override.
        var baseProfile = new AircraftProfile { TypeCode = "TEST", ClimbSpeedFinal = 0.78 };
        var ov = new AircraftProfileOverride { TypeCode = "TEST", ClimbSpeedFinal = 0 };

        var (merged, fields) = ov.ApplyTo(baseProfile);

        Assert.Equal(0, merged.ClimbSpeedFinal);
        Assert.Contains(nameof(AircraftProfile.ClimbSpeedFinal), fields);
    }

    // --- committed JSON validates (guards future contributor entries) ---

    [Fact]
    public void OverridesJson_LoadsAndIsSane()
    {
        var path = System.IO.Path.Combine(System.AppContext.BaseDirectory, "Data", "AircraftProfileOverrides.json");
        Assert.True(System.IO.File.Exists(path), "AircraftProfileOverrides.json must be copied to the build output");

        var overrides = AircraftProfileDatabase.LoadOverridesFromFile(path);
        Assert.NotEmpty(overrides);

        foreach (var ov in overrides)
        {
            Assert.False(string.IsNullOrWhiteSpace(ov.TypeCode), "every override needs a typeCode");
            Assert.NotNull(AircraftProfileDatabase.Get(ov.TypeCode));

            if (ov.ClimbSpeedInitial is { } cs)
            {
                Assert.InRange(cs, 40, 350);
            }
            if (ov.FinalApproachSpeed is { } fas)
            {
                Assert.InRange(fas, 30, 200);
            }
            if (ov.Ceiling is { } ceiling)
            {
                Assert.InRange(ceiling, 3000, 60000);
            }
            if (ov.CruiseSpeed is { } cruise)
            {
                Assert.InRange(cruise, 40, 600);
            }
        }
    }
}
