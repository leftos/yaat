using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Speech.Context;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Speech.Context;

public class RoomContextFactoryTests
{
    private static NavigationDatabase Nav => NavigationDatabase.Instance;

    private static DecoderSettings Settings => DecoderSettings.Default;

    private static AirportGroundLayout OakLayout =>
        new TestAirportGroundData().GetLayout("OAK") ?? throw new InvalidOperationException("TestData has no OAK layout.");

    [Fact]
    public void TowerRoom_HasOwnRunwaysAndTaxiways_NoKsfoApproach()
    {
        TransmissionContext context = Build("KOAK", false, OakLayout, [], []);

        Assert.Contains(context.AirportEntities, entity => (entity.Kind == EntityKind.Runway) && (entity.Value == "KOAK/28R"));
        Assert.Contains(
            context.AirportEntities,
            entity => (entity.Kind == EntityKind.Taxiway) && entity.Value.StartsWith("KOAK/", StringComparison.Ordinal)
        );
        Assert.DoesNotContain(
            context.AirportEntities,
            entity => (entity.Kind == EntityKind.Procedure) && entity.Value.StartsWith("KSFO/", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void ApproachRoom_HasKsfoRunwaysAndApproaches()
    {
        TransmissionContext context = Build("KOAK", true, OakLayout, [], []);

        Assert.Contains(context.AirportEntities, entity => (entity.Kind == EntityKind.Runway) && (entity.Value == "KSFO/28L"));
        Assert.Contains(
            context.AirportEntities,
            entity => (entity.Kind == EntityKind.Procedure) && entity.Value.StartsWith("KSFO/", StringComparison.Ordinal)
        );
        Assert.Equal(context.AirportEntities.Count, context.AirportEntities.Select(entity => (entity.Kind, entity.Value)).Distinct().Count());
    }

    [Fact]
    public void KoakRoom_HasOwnLandmarkFile_NotKsfoOnes()
    {
        TransmissionContext context = Build("KOAK", false, null, [], []);

        Assert.Contains(context.AirportEntities, entity => (entity.Kind == EntityKind.Landmark) && (entity.Value == "KOAK/OAK30NUM"));
        Assert.Contains(context.AirportEntities, entity => (entity.Kind == EntityKind.Landmark) && (entity.Value == "KOAK/BAYBRIDGE"));
        // Hunters Point is 6.85 nm from KOAK but listed only in sfo-landmarks.json.
        Assert.DoesNotContain(context.AirportEntities, entity => entity.Value == "KOAK/HUNTERSPOINT");
    }

    [Fact]
    public void KsfoRoom_HasItsLandmarks()
    {
        TransmissionContext context = Build("KSFO", false, null, [], []);

        Assert.Contains(context.AirportEntities, entity => (entity.Kind == EntityKind.Landmark) && (entity.Value == "KSFO/HUNTERSPOINT"));
        Assert.Contains(context.AirportEntities, entity => (entity.Kind == EntityKind.Landmark) && (entity.Value == "KSFO/BAYBRIDGE"));
        Assert.DoesNotContain(context.AirportEntities, entity => entity.Value == "KSFO/OAK30NUM");
    }

    [Fact]
    public void Aircraft_KeepInputOrder_UnknownTelephonyLeftOut()
    {
        RoomAircraftInput[] aircraft = [new("SWA2495", "B737", [], null), new("QQQ123", "B738", [], null), new("N4926F", "C172", [], null)];

        TransmissionContext context = Build("KOAK", false, null, aircraft, []);

        Assert.Equal(["SWA2495", "N4926F"], context.Aircraft.Select(entry => entry.Callsign.Value));
        Assert.All(context.Aircraft, entry => Assert.Equal(EntityKind.Callsign, entry.Callsign.Kind));
    }

    [Fact]
    public void OwnEntities_ProgrammedFixAndAssignedRunway_OnlyOnThatAircraft()
    {
        RoomAircraftInput[] aircraft = [new("SWA2495", "B737", ["SUNOL", " ", "SUNOL"], "28R"), new("UAL1", "B772", [], null)];

        TransmissionContext context = Build("KOAK", false, null, aircraft, []);

        RoomAircraft swa = context.Aircraft.Single(entry => entry.Callsign.Value == "SWA2495");
        RoomAircraft ual = context.Aircraft.Single(entry => entry.Callsign.Value == "UAL1");
        Assert.Equal([(EntityKind.Fix, "SUNOL"), (EntityKind.Runway, "KOAK/28R")], swa.OwnEntities.Select(entity => (entity.Kind, entity.Value)));
        Assert.Empty(ual.OwnEntities);
    }

    [Theory]
    [InlineData("Oakland Tower", "oakland")]
    [InlineData("NorCal Approach", "norcal")]
    [InlineData("San Francisco Ground", "san francisco")]
    [InlineData("Oakland Clearance Delivery", "oakland")]
    [InlineData("Tower", null)]
    [InlineData("Big Bear City Tower", null)]
    public void FacilityName_StripsFunctionWords_KeepsOneOrTwoWords(string radioName, string? expected)
    {
        TransmissionContext context = Build("KOAK", false, null, [], [radioName]);

        Assert.Equal(expected is null ? [] : [expected], context.FacilityNames);
    }

    [Fact]
    public void FacilityNames_DedupedAndOrdinalSorted()
    {
        TransmissionContext context = Build("KOAK", false, null, [], ["Oakland Tower", "San Francisco Ground", "NorCal Approach", "Oakland Center"]);

        Assert.Equal(["norcal", "oakland", "san francisco"], context.FacilityNames);
    }

    [Fact]
    public void Cache_SameKeySameList_ApproachFlagDifferentList()
    {
        var cache = new RoomContextCache();
        AirportGroundLayout layout = OakLayout;

        IReadOnlyList<ContextEntity> first = cache.AirportEntities(new AirportContextKey("KOAK", false, layout), Nav, Settings);
        IReadOnlyList<ContextEntity> again = cache.AirportEntities(new AirportContextKey("KOAK", false, layout), Nav, Settings);
        IReadOnlyList<ContextEntity> approach = cache.AirportEntities(new AirportContextKey("KOAK", true, layout), Nav, Settings);

        Assert.Same(first, again);
        Assert.NotSame(first, approach);
    }

    [Fact]
    public void EndToEnd_TowerRoom_DecodesSouthwestLanding()
    {
        TransmissionContext context = Build("KOAK", false, OakLayout, [new("SWA2495", "B737", [], null), new("UAL1", "B772", [], null)], []);
        var input = new DecoderInput(
            [new DecoderHypothesis("southwest twenty four ninety five runway two eight right cleared to land", null, null)],
            InputSource.Spoken,
            MatchMode.Fuzzy
        );

        DecodeResult result = ContextDecoder.Decode(input, context, Settings);

        Assert.Equal(DecodeState.Resolved, result.State);
        Assert.Equal("SWA2495", result.Callsign?.Entity.Value);
    }

    private static TransmissionContext Build(
        string airport,
        bool approach,
        AirportGroundLayout? layout,
        IReadOnlyList<RoomAircraftInput> aircraft,
        IReadOnlyList<string> facilities
    ) => RoomContextFactory.Build(new RoomContextCache(), new RoomContextRequest(airport, approach, layout, aircraft, facilities), Nav, Settings);
}
