using Xunit;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

public class ProcedureConstraintTests
{
    private static AircraftState CreateAircraft(double altitude = 5000, double ias = 250)
    {
        return new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Latitude = 37.0,
            Longitude = -122.0,
            TrueHeading = new TrueHeading(360),
            TrueTrack = new TrueHeading(360),
            Altitude = altitude,
            IndicatedAirspeed = ias,
        };
    }

    private static NavigationTarget MakeTarget(CifpAltitudeRestriction? alt = null, CifpSpeedRestriction? spd = null)
    {
        return new NavigationTarget
        {
            Name = "TESTFIX",
            Latitude = 37.5,
            Longitude = -122.0,
            AltitudeRestriction = alt,
            SpeedRestriction = spd,
        };
    }

    [Fact]
    public void SidViaMode_AtOrAbove_ClimbsToRestriction()
    {
        var aircraft = CreateAircraft(altitude: 3000);
        aircraft.SidViaMode = true;
        aircraft.ActiveSidId = "PORTE3";

        var target = MakeTarget(alt: new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 5000));
        FlightPhysics.ApplyFixConstraints(aircraft, target);

        Assert.Equal(5000, aircraft.Targets.TargetAltitude);
    }

    [Fact]
    public void SidViaMode_AtOrAbove_AlreadyAbove_NoChange()
    {
        var aircraft = CreateAircraft(altitude: 7000);
        aircraft.SidViaMode = true;
        aircraft.ActiveSidId = "PORTE3";

        var target = MakeTarget(alt: new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 5000));
        FlightPhysics.ApplyFixConstraints(aircraft, target);

        Assert.Null(aircraft.Targets.TargetAltitude);
    }

    [Fact]
    public void SidViaMode_WithCeiling_CapsAltitude()
    {
        var aircraft = CreateAircraft(altitude: 3000);
        aircraft.SidViaMode = true;
        aircraft.ActiveSidId = "PORTE3";
        aircraft.SidViaCeiling = 10000;

        var target = MakeTarget(alt: new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 15000));
        FlightPhysics.ApplyFixConstraints(aircraft, target);

        Assert.Equal(10000, aircraft.Targets.TargetAltitude);
    }

    [Fact]
    public void StarViaMode_AtOrBelow_DescendsToRestriction()
    {
        var aircraft = CreateAircraft(altitude: 15000);
        aircraft.StarViaMode = true;
        aircraft.ActiveStarId = "BDEGA3";

        var target = MakeTarget(alt: new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrBelow, 12000));
        FlightPhysics.ApplyFixConstraints(aircraft, target);

        Assert.Equal(12000, aircraft.Targets.TargetAltitude);
    }

    [Fact]
    public void StarViaMode_AtOrBelow_AlreadyBelow_NoChange()
    {
        var aircraft = CreateAircraft(altitude: 10000);
        aircraft.StarViaMode = true;
        aircraft.ActiveStarId = "BDEGA3";

        var target = MakeTarget(alt: new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrBelow, 12000));
        FlightPhysics.ApplyFixConstraints(aircraft, target);

        Assert.Null(aircraft.Targets.TargetAltitude);
    }

    [Fact]
    public void StarViaMode_AtOrAbove_DescendsToRestriction()
    {
        var aircraft = CreateAircraft(altitude: 9000);
        aircraft.StarViaMode = true;
        aircraft.ActiveStarId = "BDEGA3";

        var target = MakeTarget(alt: new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 5000));
        FlightPhysics.ApplyFixConstraints(aircraft, target);

        // STAR via mode: AtOrAbove 5000 means "descend to 5000" (the depicted altitude)
        Assert.Equal(5000, aircraft.Targets.TargetAltitude);
    }

    [Fact]
    public void StarViaMode_WithFloor_PreventsOverDescent()
    {
        var aircraft = CreateAircraft(altitude: 15000);
        aircraft.StarViaMode = true;
        aircraft.ActiveStarId = "BDEGA3";
        aircraft.StarViaFloor = 10000;

        var target = MakeTarget(alt: new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 8000));
        FlightPhysics.ApplyFixConstraints(aircraft, target);

        Assert.Equal(10000, aircraft.Targets.TargetAltitude);
    }

    [Fact]
    public void ViaMode_AtRestriction_SetsExactAltitude()
    {
        var aircraft = CreateAircraft(altitude: 5000);
        aircraft.SidViaMode = true;
        aircraft.ActiveSidId = "PORTE3";

        var target = MakeTarget(alt: new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 8000));
        FlightPhysics.ApplyFixConstraints(aircraft, target);

        Assert.Equal(8000, aircraft.Targets.TargetAltitude);
    }

    [Fact]
    public void ViaMode_BetweenRestriction_TooHigh_DescendsToLower()
    {
        var aircraft = CreateAircraft(altitude: 15000);
        aircraft.StarViaMode = true;
        aircraft.ActiveStarId = "BDEGA3";

        var target = MakeTarget(alt: new CifpAltitudeRestriction(CifpAltitudeRestrictionType.Between, 12000, 10000));
        FlightPhysics.ApplyFixConstraints(aircraft, target);

        // STAR via: always target lower bound — upper is permissiveness
        Assert.Equal(10000, aircraft.Targets.TargetAltitude);
    }

    [Fact]
    public void ViaMode_BetweenRestriction_TooLow_ClimbsToUpper()
    {
        var aircraft = CreateAircraft(altitude: 8000);
        aircraft.SidViaMode = true;
        aircraft.ActiveSidId = "PORTE3";

        var target = MakeTarget(alt: new CifpAltitudeRestriction(CifpAltitudeRestrictionType.Between, 12000, 10000));
        FlightPhysics.ApplyFixConstraints(aircraft, target);

        // SID via: target upper bound — pilots want to get high fast
        Assert.Equal(12000, aircraft.Targets.TargetAltitude);
    }

    [Fact]
    public void ViaMode_BetweenRestriction_WithinRange_DescendsToLowerBound()
    {
        var aircraft = CreateAircraft(altitude: 11000);
        aircraft.StarViaMode = true;
        aircraft.ActiveStarId = "BDEGA3";

        var target = MakeTarget(alt: new CifpAltitudeRestriction(CifpAltitudeRestrictionType.Between, 12000, 10000));
        FlightPhysics.ApplyFixConstraints(aircraft, target);

        // STAR via mode: when within range, target the lower bound to continue descent
        Assert.Equal(10000, aircraft.Targets.TargetAltitude);
    }

    [Fact]
    public void SpeedRestriction_Applied()
    {
        var aircraft = CreateAircraft();
        aircraft.SidViaMode = true;
        aircraft.ActiveSidId = "PORTE3";

        var target = MakeTarget(spd: new CifpSpeedRestriction(210, IsMaximum: true));
        FlightPhysics.ApplyFixConstraints(aircraft, target);

        Assert.Equal(210, aircraft.Targets.TargetSpeed);
    }

    [Fact]
    public void NoViaMode_ConstraintsStillApplied()
    {
        var aircraft = CreateAircraft(altitude: 3000);
        // No via mode set — constraints now apply universally for CFIX/drawn routes

        var target = MakeTarget(
            alt: new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 8000),
            spd: new CifpSpeedRestriction(210, IsMaximum: true)
        );
        FlightPhysics.ApplyFixConstraints(aircraft, target);

        Assert.Equal(8000, aircraft.Targets.TargetAltitude);
        Assert.Equal(210, aircraft.Targets.TargetSpeed);
    }

    // --- 14 CFR 91.117: 250 KIAS below 10,000 ft ---

    [Fact]
    public void UpdateSpeed_Below10000_CapsAt250()
    {
        var aircraft = CreateAircraft(altitude: 5000, ias: 250);
        aircraft.Targets.TargetSpeed = 300;

        FlightPhysics.Update(aircraft, 1.0);

        // Should be decelerating toward 250 (the capped goal), not accelerating toward 300
        Assert.True(aircraft.IndicatedAirspeed <= 250);
    }

    [Fact]
    public void UpdateSpeed_Above10000_NoSpeedCap()
    {
        var aircraft = CreateAircraft(altitude: 12000, ias: 280);
        aircraft.Targets.TargetSpeed = 300;

        FlightPhysics.Update(aircraft, 1.0);

        // Should be accelerating toward 300 (no cap above 10,000 ft)
        Assert.True(aircraft.IndicatedAirspeed > 280);
    }

    [Fact]
    public void UpdateSpeed_OnGround_NoSpeedCap()
    {
        var aircraft = CreateAircraft(altitude: 100, ias: 30);
        aircraft.IsOnGround = true;
        aircraft.Targets.TargetSpeed = 300;

        FlightPhysics.Update(aircraft, 1.0);

        // Ground ops are not subject to 250 KIAS rule
        Assert.True(aircraft.IndicatedAirspeed > 30);
    }

    [Fact]
    public void ApplyFixConstraints_SpeedRestrictionCappedBelow10000()
    {
        var aircraft = CreateAircraft(altitude: 5000);
        aircraft.SidViaMode = true;
        aircraft.ActiveSidId = "PORTE3";

        var target = MakeTarget(spd: new CifpSpeedRestriction(280, IsMaximum: true));
        FlightPhysics.ApplyFixConstraints(aircraft, target);

        // Speed restriction of 280 should be capped to 250 below 10,000 ft
        Assert.Equal(250, aircraft.Targets.TargetSpeed);
    }

    [Fact]
    public void ApplyFixConstraints_SpeedRestrictionNotCappedAbove10000()
    {
        var aircraft = CreateAircraft(altitude: 12000);
        aircraft.SidViaMode = true;
        aircraft.ActiveSidId = "PORTE3";

        var target = MakeTarget(spd: new CifpSpeedRestriction(280, IsMaximum: true));
        FlightPhysics.ApplyFixConstraints(aircraft, target);

        // Above 10,000 ft — no cap
        Assert.Equal(280, aircraft.Targets.TargetSpeed);
    }

    [Fact]
    public void ProcedureState_ClearedOnRouteCompletion()
    {
        var aircraft = CreateAircraft(altitude: 5000);
        aircraft.ActiveSidId = "PORTE3";
        aircraft.SidViaMode = true;
        aircraft.SidViaCeiling = 10000;
        aircraft.ActiveStarId = "BDEGA3";
        aircraft.StarViaMode = true;
        aircraft.StarViaFloor = 5000;

        // Place aircraft near the last fix so it will be reached
        aircraft.Latitude = 37.5;
        aircraft.Longitude = -122.0;

        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "LASTFIX",
                Latitude = 37.5,
                Longitude = -122.0,
            }
        );

        // Run a physics tick — aircraft is at the fix, route will empty
        FlightPhysics.Update(aircraft, 1.0);

        Assert.Null(aircraft.ActiveSidId);
        Assert.Null(aircraft.ActiveStarId);
        Assert.False(aircraft.SidViaMode);
        Assert.False(aircraft.StarViaMode);
        Assert.Null(aircraft.SidViaCeiling);
        Assert.Null(aircraft.StarViaFloor);
    }

    [Fact]
    public void ConstraintsApplied_WhenAdvancingToNextFix()
    {
        var aircraft = CreateAircraft(altitude: 3000);
        aircraft.SidViaMode = true;
        aircraft.ActiveSidId = "PORTE3";

        // Place aircraft near the first fix
        aircraft.Latitude = 37.1;
        aircraft.Longitude = -122.0;

        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "FIX1",
                Latitude = 37.1,
                Longitude = -122.0,
            }
        );
        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "FIX2",
                Latitude = 38.0,
                Longitude = -122.0,
                AltitudeRestriction = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 7000),
                SpeedRestriction = new CifpSpeedRestriction(230, IsMaximum: true),
            }
        );

        // Run physics — aircraft reaches FIX1, advances to FIX2, applies constraints
        FlightPhysics.Update(aircraft, 1.0);

        Assert.Equal(7000, aircraft.Targets.TargetAltitude);
        Assert.Equal(230, aircraft.Targets.TargetSpeed);
        Assert.Single(aircraft.Targets.NavigationRoute);
        Assert.Equal("FIX2", aircraft.Targets.NavigationRoute[0].Name);
    }
}
