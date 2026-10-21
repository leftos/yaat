using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;

namespace Yaat.Client.Tests;

/// <summary>
/// The traffic-advisory matcher's wire inputs on the client: <see cref="AircraftModel.FromDto"/> and
/// <see cref="AircraftModel.UpdateFromDto"/> carry <c>AircraftDto.TrueTrack</c> and <c>AircraftDto.IsManeuvering</c> across,
/// and fold the pattern triple into one <see cref="PatternPosition"/>, null when the aircraft is not in a pattern.
/// </summary>
public class AircraftModelTrafficAdvisoryTests
{
    [Fact]
    public void FromDto_WithAPatternTriple_AppliesIt()
    {
        var model = AircraftModel.FromDto(Dto(PatternEntryLeg.Base, PatternDirection.Right, 2.1));

        Assert.Equal(new PatternPosition(PatternEntryLeg.Base, PatternDirection.Right, 2.1), model.PatternPosition);
        Assert.Equal(95.0, model.TrueTrack);
        Assert.True(model.IsManeuvering);
    }

    [Fact]
    public void FromDto_OnFinal_HasNoSide()
    {
        var model = AircraftModel.FromDto(Dto(PatternEntryLeg.Final, null, 4.0));

        Assert.Equal(new PatternPosition(PatternEntryLeg.Final, null, 4.0), model.PatternPosition);
    }

    [Fact]
    public void FromDto_WithoutAPatternTriple_IsNull()
    {
        var model = AircraftModel.FromDto(Dto(null, null, null));

        Assert.Null(model.PatternPosition);
    }

    [Fact]
    public void UpdateFromDto_LeavingThePattern_ClearsIt()
    {
        var model = AircraftModel.FromDto(Dto(PatternEntryLeg.Downwind, PatternDirection.Left, 1.5));

        model.UpdateFromDto(Dto(null, null, null));

        Assert.Null(model.PatternPosition);
    }

    private static AircraftDto Dto(PatternEntryLeg? leg, PatternDirection? side, double? distanceNm) =>
        new(
            Callsign: "N436MS",
            AircraftType: "C172",
            Latitude: 37.72,
            Longitude: -122.22,
            Heading: 100,
            Altitude: 1500,
            GroundSpeed: 90,
            BeaconCode: 1200,
            TransponderMode: "C",
            IsIdenting: false,
            VerticalSpeed: 0,
            AssignedHeading: null,
            AssignedAltitude: null,
            AssignedSpeed: null,
            Departure: "KOAK",
            Destination: "KOAK",
            Route: "",
            FlightRules: "VFR",
            Status: "",
            TrueTrack: 95,
            IsManeuvering: true,
            PatternLeg: leg,
            PatternSide: side,
            PatternDistanceNm: distanceNm
        );
}
