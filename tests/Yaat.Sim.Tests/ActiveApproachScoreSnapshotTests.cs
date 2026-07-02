using Xunit;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests;

/// <summary>
/// Round-trip tests for <see cref="ApproachScore"/> snapshot serialization. An approach score
/// captured at localizer establishment (but before the full-stop landing) must survive a snapshot /
/// rewind / recording reload — otherwise <see cref="PhaseRunner"/> never stamps the landing time,
/// so the debrief shows the approach as "established, never landed" and the landing-interval
/// (spacing) computation drops it. See issue #220.
/// </summary>
public class ActiveApproachScoreSnapshotTests
{
    [Fact]
    public void ActiveApproachScore_SurvivesSnapshotRoundTrip()
    {
        var ac = new AircraftState
        {
            Callsign = "UAL123",
            AircraftType = "B738",
            TrueHeading = new TrueHeading(280),
            Altitude = 3000,
            IndicatedAirspeed = 160,
            Position = new LatLon(37.72, -122.22),
            FlightPlan = new AircraftFlightPlan { Destination = "OAK" },
        };
        ac.ActiveApproachScore = new ApproachScore
        {
            Callsign = "UAL123",
            AircraftType = "B738",
            ApproachId = "I28R",
            RunwayId = "28R",
            AirportCode = "OAK",
            EstablishedAtSeconds = 120.0,
        };

        var dto = ac.ToSnapshot();
        var restored = AircraftState.FromSnapshot(dto, groundLayout: null);

        Assert.NotNull(restored.ActiveApproachScore);
        Assert.Equal("I28R", restored.ActiveApproachScore!.ApproachId);
    }

    [Fact]
    public void ActiveApproachScore_Null_RoundTripsAsNull()
    {
        var ac = new AircraftState
        {
            Callsign = "SWA55",
            AircraftType = "B737",
            Position = new LatLon(37.72, -122.22),
        };

        var restored = AircraftState.FromSnapshot(ac.ToSnapshot(), groundLayout: null);

        Assert.Null(restored.ActiveApproachScore);
    }

    [Fact]
    public void ApproachScore_AllFields_RoundTrip()
    {
        var score = new ApproachScore
        {
            Callsign = "AAL987",
            AircraftType = "A320",
            ApproachId = "I28L",
            RunwayId = "28L",
            AirportCode = "OAK",
            InterceptAngleDeg = 24.5,
            InterceptDistanceNm = 8.2,
            MinInterceptDistanceNm = 7.9,
            GlideSlopeDeviationFt = 145.0,
            SpeedAtInterceptKts = 165.0,
            WasForced = true,
            IsPatternTraffic = true,
            MaxAllowedAngleDeg = 30.0,
            IsInterceptAngleLegal = true,
            IsInterceptDistanceLegal = false,
            EstablishedAtSeconds = 210.0,
            LandedAtSeconds = 305.0,
            EstablishedLat = 37.701,
            EstablishedLon = -122.212,
        };

        var restored = ApproachScore.FromSnapshot(score.ToSnapshot());

        Assert.Equal(score.Callsign, restored.Callsign);
        Assert.Equal(score.AircraftType, restored.AircraftType);
        Assert.Equal(score.ApproachId, restored.ApproachId);
        Assert.Equal(score.RunwayId, restored.RunwayId);
        Assert.Equal(score.AirportCode, restored.AirportCode);
        Assert.Equal(score.InterceptAngleDeg, restored.InterceptAngleDeg);
        Assert.Equal(score.InterceptDistanceNm, restored.InterceptDistanceNm);
        Assert.Equal(score.MinInterceptDistanceNm, restored.MinInterceptDistanceNm);
        Assert.Equal(score.GlideSlopeDeviationFt, restored.GlideSlopeDeviationFt);
        Assert.Equal(score.SpeedAtInterceptKts, restored.SpeedAtInterceptKts);
        Assert.Equal(score.WasForced, restored.WasForced);
        Assert.Equal(score.IsPatternTraffic, restored.IsPatternTraffic);
        Assert.Equal(score.MaxAllowedAngleDeg, restored.MaxAllowedAngleDeg);
        Assert.Equal(score.IsInterceptAngleLegal, restored.IsInterceptAngleLegal);
        Assert.Equal(score.IsInterceptDistanceLegal, restored.IsInterceptDistanceLegal);
        Assert.Equal(score.EstablishedAtSeconds, restored.EstablishedAtSeconds);
        Assert.Equal(score.LandedAtSeconds, restored.LandedAtSeconds);
        Assert.Equal(score.EstablishedLat, restored.EstablishedLat);
        Assert.Equal(score.EstablishedLon, restored.EstablishedLon);
    }

    [Fact]
    public void ActiveApproachScore_EstablishedNotLanded_RestoresUnlandedSoLandingCanStamp()
    {
        // Mirrors the real failure window: aircraft established on final, not yet landed, when the
        // snapshot is taken. After restore, PhaseRunner only stamps the landing when
        // ActiveApproachScore is non-null with LandedAtSeconds == null.
        var ac = new AircraftState
        {
            Callsign = "DAL42",
            AircraftType = "B739",
            Position = new LatLon(37.72, -122.22),
        };
        ac.ActiveApproachScore = new ApproachScore
        {
            Callsign = "DAL42",
            AircraftType = "B739",
            ApproachId = "I30",
            RunwayId = "30",
            AirportCode = "OAK",
            EstablishedAtSeconds = 90.0,
            LandedAtSeconds = null,
        };

        var restored = AircraftState.FromSnapshot(ac.ToSnapshot(), groundLayout: null);

        Assert.NotNull(restored.ActiveApproachScore);
        Assert.Null(restored.ActiveApproachScore!.LandedAtSeconds);
    }
}
