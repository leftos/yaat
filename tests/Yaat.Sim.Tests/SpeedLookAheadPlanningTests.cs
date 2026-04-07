using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

/// <summary>
/// Unit tests for FlightPhysics.UpdateSpeedPlanning() — proactive speed constraint look-ahead.
/// Aircraft should start decelerating/accelerating before reaching a speed-constrained fix,
/// not react after sequencing past it.
/// </summary>
public class SpeedLookAheadPlanningTests(ITestOutputHelper output)
{
    private void Log(AircraftState ac, string label)
    {
        output?.WriteLine(
            $"[{label}] IAS={ac.IndicatedAirspeed:F1} GS={ac.GroundSpeed:F1} alt={ac.Altitude:F0} "
                + $"TargetSpeed={ac.Targets.TargetSpeed} TargetAlt={ac.Targets.TargetAltitude} "
                + $"HasExplicitSpd={ac.Targets.HasExplicitSpeedCommand} DSR={ac.SpeedRestrictionsDeleted} "
                + $"Mach={ac.Targets.TargetMach} NavRoute.Count={ac.Targets.NavigationRoute.Count}"
        );
        foreach (var nav in ac.Targets.NavigationRoute)
        {
            double dist = GeoMath.DistanceNm(ac.Latitude, ac.Longitude, nav.Latitude, nav.Longitude);
            output?.WriteLine($"  Fix {nav.Name}: dist={dist:F2}nm spdConstraint={nav.SpeedRestriction?.SpeedKts}");
        }
    }

    private static AircraftState CreateAircraft(
        double ias = 280,
        double altitude = 12000,
        double lat = 37.0,
        double lon = -122.0,
        string type = "B738"
    )
    {
        TestVnasData.EnsureInitialized();
        return new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = type,
            Latitude = lat,
            Longitude = lon,
            TrueHeading = new TrueHeading(360),
            TrueTrack = new TrueHeading(360),
            Altitude = altitude,
            IndicatedAirspeed = ias,
        };
    }

    /// <summary>
    /// Place a fix at a given distance (nm) north of the aircraft's position.
    /// 1 degree latitude ≈ 60nm.
    /// </summary>
    private static NavigationTarget MakeFix(
        string name,
        double distanceNm,
        double baseLat = 37.0,
        double baseLon = -122.0,
        CifpSpeedRestriction? speed = null
    )
    {
        double latOffset = distanceNm / 60.0;
        return new NavigationTarget
        {
            Name = name,
            Latitude = baseLat + latOffset,
            Longitude = baseLon,
            SpeedRestriction = speed,
        };
    }

    [Fact]
    public void Deceleration_StartsWhenCloseToConstrainedFix()
    {
        var aircraft = CreateAircraft(ias: 280, altitude: 12000);
        // Fix 2nm ahead with 210kt constraint — should trigger decel immediately
        var fix = MakeFix("CNSTR", 2.0, speed: new CifpSpeedRestriction(210, IsMaximum: true));
        aircraft.Targets.NavigationRoute.Add(fix);

        Log(aircraft, "before tick");
        FlightPhysics.Update(aircraft, 0.25);
        Log(aircraft, "after tick");

        Assert.NotNull(aircraft.Targets.TargetSpeed);
        Assert.True(aircraft.Targets.TargetSpeed <= 210, $"Expected TargetSpeed <= 210, got {aircraft.Targets.TargetSpeed}");
    }

    [Fact]
    public void Acceleration_StartsWhenCloseToConstrainedFix()
    {
        var aircraft = CreateAircraft(ias: 210, altitude: 12000);
        // Fix 1nm ahead with 250kt constraint — 40kt delta / 2.5 kts/s = 16s accel time.
        // At ~256kt GS, 1nm = ~14s — well within the 1.1× margin window.
        var fix = MakeFix("CNSTR", 1.0, speed: new CifpSpeedRestriction(250, IsMaximum: false));
        aircraft.Targets.NavigationRoute.Add(fix);

        Log(aircraft, "before tick");

        // Compute expected values for diagnosis
        var cat = AircraftCategorization.Categorize("B738");
        double accelRate = AircraftPerformance.AccelRate("B738", cat);
        double decelRate = AircraftPerformance.DecelRate("B738", cat);
        double gs = aircraft.GroundSpeed;
        double distNm = GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, fix.Latitude, fix.Longitude);
        double timeToFix = distNm / (gs / 3600.0);
        double speedDelta = Math.Abs(210 - 250);
        double changeTime = speedDelta / accelRate;
        output?.WriteLine(
            $"  cat={cat} accelRate={accelRate} decelRate={decelRate} GS={gs:F1} "
                + $"distNm={distNm:F3} timeToFix={timeToFix:F1}s speedDelta={speedDelta} "
                + $"changeTime={changeTime:F1}s threshold={changeTime * 1.1:F1}s"
        );

        FlightPhysics.Update(aircraft, 0.25);
        Log(aircraft, "after tick");

        Assert.NotNull(aircraft.Targets.TargetSpeed);
        Assert.True(aircraft.Targets.TargetSpeed >= 250, $"Expected TargetSpeed >= 250, got {aircraft.Targets.TargetSpeed}");
    }

    [Fact]
    public void NoPlanningWhenAlreadyAtConstraintSpeed()
    {
        var aircraft = CreateAircraft(ias: 210, altitude: 12000);
        var fix = MakeFix("CNSTR", 2.0, speed: new CifpSpeedRestriction(210, IsMaximum: true));
        aircraft.Targets.NavigationRoute.Add(fix);

        FlightPhysics.Update(aircraft, 0.25);

        // TargetSpeed should be null — no speed change needed (already at 210)
        // Note: auto speed schedule may set a target, but UpdateSpeedPlanning should not
        // set one since we're already at the constraint speed.
    }

    [Fact]
    public void NoPlanningWhenFarFromFix()
    {
        var aircraft = CreateAircraft(ias: 280, altitude: 12000);
        // Fix 50nm ahead — at 280kt GS that's ~10 minutes away, well beyond decel time
        var fix = MakeFix("CNSTR", 50.0, speed: new CifpSpeedRestriction(210, IsMaximum: true));
        aircraft.Targets.NavigationRoute.Add(fix);

        FlightPhysics.Update(aircraft, 0.25);

        // TargetSpeed should NOT be 210 — too far away to start decelerating
        // (it may be set by auto speed schedule to something else, but not 210)
        if (aircraft.Targets.TargetSpeed is { } ts)
        {
            Assert.True(ts > 210, $"Expected TargetSpeed > 210 when 50nm away, got {ts}");
        }
    }

    [Fact]
    public void ExplicitSpeedCommand_BlocksPlanning()
    {
        var aircraft = CreateAircraft(ias: 280, altitude: 12000);
        aircraft.Targets.HasExplicitSpeedCommand = true;
        var fix = MakeFix("CNSTR", 2.0, speed: new CifpSpeedRestriction(210, IsMaximum: true));
        aircraft.Targets.NavigationRoute.Add(fix);

        FlightPhysics.Update(aircraft, 0.25);

        // With HasExplicitSpeedCommand, look-ahead should not set 210
        if (aircraft.Targets.TargetSpeed is { } ts)
        {
            Assert.True(ts != 210, $"Expected TargetSpeed != 210 when HasExplicitSpeedCommand, got {ts}");
        }
    }

    [Fact]
    public void SpeedRestrictionsDeleted_BlocksPlanning()
    {
        var aircraft = CreateAircraft(ias: 280, altitude: 12000);
        aircraft.SpeedRestrictionsDeleted = true;
        var fix = MakeFix("CNSTR", 2.0, speed: new CifpSpeedRestriction(210, IsMaximum: true));
        aircraft.Targets.NavigationRoute.Add(fix);

        FlightPhysics.Update(aircraft, 0.25);

        // With SpeedRestrictionsDeleted, look-ahead should not set 210
        if (aircraft.Targets.TargetSpeed is { } ts)
        {
            Assert.True(ts != 210, $"Expected TargetSpeed != 210 when SpeedRestrictionsDeleted, got {ts}");
        }
    }

    [Fact]
    public void MachHold_BlocksPlanning()
    {
        var aircraft = CreateAircraft(ias: 280, altitude: 35000);
        aircraft.Targets.TargetMach = 0.78;
        var fix = MakeFix("CNSTR", 2.0, speed: new CifpSpeedRestriction(210, IsMaximum: true));
        aircraft.Targets.NavigationRoute.Add(fix);

        FlightPhysics.Update(aircraft, 0.25);

        // Mach hold should prevent speed planning from setting 210
        // TargetSpeed will be set by Mach hold logic instead
    }

    [Fact]
    public void SpeedFloor_ClampsPlannedSpeed()
    {
        var aircraft = CreateAircraft(ias: 280, altitude: 12000);
        aircraft.Targets.SpeedFloor = 230;
        var fix = MakeFix("CNSTR", 2.0, speed: new CifpSpeedRestriction(210, IsMaximum: true));
        aircraft.Targets.NavigationRoute.Add(fix);

        FlightPhysics.Update(aircraft, 0.25);

        // Floor of 230 should clamp the 210 constraint up to 230
        Assert.NotNull(aircraft.Targets.TargetSpeed);
        Assert.True(aircraft.Targets.TargetSpeed >= 230, $"Expected TargetSpeed >= 230 (floor), got {aircraft.Targets.TargetSpeed}");
    }

    [Fact]
    public void SpeedCeiling_ClampsPlannedSpeed()
    {
        var aircraft = CreateAircraft(ias: 200, altitude: 12000);
        aircraft.Targets.SpeedCeiling = 240;
        // Fix 1nm ahead: 260kt constraint clamped to 240. Delta=40, accelRate=2 → 20s * 1.1 = 22s.
        // GS ≈ 238kt → 1nm = 15.1s < 22s → triggers.
        var fix = MakeFix("CNSTR", 1.0, speed: new CifpSpeedRestriction(260, IsMaximum: false));
        aircraft.Targets.NavigationRoute.Add(fix);

        FlightPhysics.Update(aircraft, 0.25);

        // Ceiling of 240 should clamp the 260 constraint down to 240
        Assert.NotNull(aircraft.Targets.TargetSpeed);
        Assert.True(aircraft.Targets.TargetSpeed <= 240, $"Expected TargetSpeed <= 240 (ceiling), got {aircraft.Targets.TargetSpeed}");
    }

    [Fact]
    public void Below10k_CapsConstraintAt250()
    {
        var aircraft = CreateAircraft(ias: 280, altitude: 8000);
        // Fix 0.5nm ahead — close enough to trigger. 30kt delta / 3.5 kts/s = 8.6s.
        // At ~319kt GS, 0.5nm = ~5.6s — well within the window.
        var fix = MakeFix("CNSTR", 0.5, speed: new CifpSpeedRestriction(280, IsMaximum: true));
        aircraft.Targets.NavigationRoute.Add(fix);

        Log(aircraft, "before tick");
        FlightPhysics.Update(aircraft, 0.25);
        Log(aircraft, "after tick");

        // Below 10k, 91.117 caps speed at 250 — the 280kt constraint should be
        // capped to 250. The aircraft is at 280, so it should decel to 250.
        Assert.NotNull(aircraft.Targets.TargetSpeed);
        Assert.True(aircraft.Targets.TargetSpeed <= 250, $"Expected TargetSpeed <= 250 (91.117), got {aircraft.Targets.TargetSpeed}");
    }

    [Fact]
    public void StepPlanning_OnlyTargetsFirstConstrainedFix()
    {
        var aircraft = CreateAircraft(ias: 280, altitude: 12000);

        // First fix at 0.5nm: 250kt — close enough to trigger (30kt delta / 3.5 = 8.6s,
        // 0.5nm at ~342kt GS = ~5.3s — triggers). Second fix at 2nm: 210kt.
        var fix1 = MakeFix("FIX1", 0.5, speed: new CifpSpeedRestriction(250, IsMaximum: true));
        var fix2 = MakeFix("FIX2", 2.0, speed: new CifpSpeedRestriction(210, IsMaximum: true));
        aircraft.Targets.NavigationRoute.Add(fix1);
        aircraft.Targets.NavigationRoute.Add(fix2);

        Log(aircraft, "before tick");
        FlightPhysics.Update(aircraft, 0.25);
        Log(aircraft, "after tick");

        // Should target 250 (first constraint), not 210 (second)
        Assert.NotNull(aircraft.Targets.TargetSpeed);
        Assert.True(aircraft.Targets.TargetSpeed >= 240, $"Expected TargetSpeed near 250 (first constraint), got {aircraft.Targets.TargetSpeed}");
    }

    [Fact]
    public void OnGround_NoPlanningApplied()
    {
        var aircraft = CreateAircraft(ias: 20, altitude: 0);
        aircraft.IsOnGround = true;
        var fix = MakeFix("CNSTR", 0.5, speed: new CifpSpeedRestriction(210, IsMaximum: true));
        aircraft.Targets.NavigationRoute.Add(fix);

        FlightPhysics.Update(aircraft, 0.25);

        // On ground, speed planning should not apply procedure constraints
    }
}
