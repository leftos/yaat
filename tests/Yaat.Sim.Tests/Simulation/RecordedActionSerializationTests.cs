using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Guards the polymorphic action log a recording is written from and read back through. Every
/// <see cref="RecordedAction"/> subtype has to be registered as a <see cref="JsonDerivedTypeAttribute"/>
/// on the base record and has to survive a write/read cycle through
/// <see cref="RecordingJsonOptions.Default"/> — the options
/// <see cref="RecordingArchiveWriter.WriteActions"/> writes <c>actions.json.br</c> with. A subtype added
/// without a registration serializes without a discriminator and fails only when a recording is loaded;
/// these tests turn that into a compile-time-adjacent failure instead.
/// </summary>
public class RecordedActionSerializationTests
{
    private const string DiscriminatorProperty = "$type";

    [Fact]
    public void EverySubtypeIsRegisteredAsDerivedType()
    {
        var registrations = DerivedTypeRegistrations();
        var registeredTypes = registrations.Select(r => r.DerivedType).ToHashSet();

        var unregistered = ConcreteSubtypes()
            .Where(t => !registeredTypes.Contains(t))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        Assert.True(
            unregistered.Count == 0,
            $"RecordedAction subtypes missing a [JsonDerivedType] registration on RecordedAction: {string.Join(", ", unregistered)}. "
                + "An unregistered subtype serializes without a discriminator and breaks recording load."
        );

        var stale = registeredTypes.Where(t => !ConcreteSubtypes().Contains(t)).Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.True(
            stale.Count == 0,
            $"[JsonDerivedType] registrations naming types that are not concrete RecordedAction subtypes: {string.Join(", ", stale)}."
        );

        var missingDiscriminator = registrations.Where(r => r.TypeDiscriminator is null).Select(r => r.DerivedType.Name).ToList();
        Assert.True(
            missingDiscriminator.Count == 0,
            $"[JsonDerivedType] registrations without an explicit discriminator: {string.Join(", ", missingDiscriminator)}."
        );

        var duplicates = registrations
            .GroupBy(r => r.TypeDiscriminator!.ToString(), StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} -> [{string.Join(", ", g.Select(r => r.DerivedType.Name))}]")
            .ToList();
        Assert.True(duplicates.Count == 0, $"Duplicate [JsonDerivedType] discriminators on RecordedAction: {string.Join("; ", duplicates)}.");
    }

    [Fact]
    public void EverySubtypeRoundTripsThroughTheActionLogSerializer()
    {
        var samples = BuildSamples();

        var uncovered = ConcreteSubtypes().Where(t => !samples.ContainsKey(t)).Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.True(
            uncovered.Count == 0,
            $"RecordedAction subtypes without a sample in BuildSamples(): {string.Join(", ", uncovered)}. Add one so the round trip covers the new subtype."
        );

        var discriminators = DerivedTypeRegistrations().ToDictionary(r => r.DerivedType, r => r.TypeDiscriminator!.ToString()!);

        foreach (var (type, sample) in samples)
        {
            Assert.Equal(type, sample.GetType());

            // Serialized as the base type, exactly as RecordingArchiveWriter.WriteActions does with its List<RecordedAction>,
            // so System.Text.Json emits the discriminator.
            var json = JsonSerializer.Serialize<RecordedAction>(sample, RecordingJsonOptions.Default);
            Assert.Contains($"\"{DiscriminatorProperty}\":\"{discriminators[type]}\"", json);

            var roundTripped = JsonSerializer.Deserialize<RecordedAction>(json, RecordingJsonOptions.Default);
            Assert.NotNull(roundTripped);
            Assert.Equal(type, roundTripped.GetType());
            Assert.Equal(sample.ElapsedSeconds, roundTripped.ElapsedSeconds);
        }
    }

    private static IReadOnlyList<JsonDerivedTypeAttribute> DerivedTypeRegistrations() =>
        typeof(RecordedAction).GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false).ToList();

    private static IReadOnlyList<Type> ConcreteSubtypes() =>
        typeof(RecordedAction)
            .Assembly.GetTypes()
            .Where(t => !t.IsAbstract && t.IsAssignableTo(typeof(RecordedAction)))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// One realistic sample per concrete subtype. Hand-written rather than reflected so the round trip
    /// exercises populated payloads (nested DTOs, collections, nullable extras) instead of default-valued shells.
    /// </summary>
    private static Dictionary<Type, RecordedAction> BuildSamples()
    {
        var samples = new List<RecordedAction>
        {
            new RecordedCommand(12.5, "AAL100", "D SUNOL", "LA", "conn-a1b2")
            {
                ReactionDelaySeconds = 1.75,
                SpawnJitterSeconds = 4,
                SpawnedAircraft = SampleAircraft("AAL100", "B738"),
                IssuedAtUtc = new DateTime(2026, 5, 1, 17, 42, 13, DateTimeKind.Utc),
                StripId = "SEP_3",
                Accepted = true,
            },
            new RecordedChat(18.0, "LA", "SWA22 will be number two behind the RJ"),
            new RecordedAmendFlightPlan(
                24.0,
                "SWA22",
                new FlightPlanAmendment(
                    AircraftType: "B737",
                    EquipmentSuffix: "L",
                    Departure: "KOAK",
                    Destination: "KSJC",
                    CruiseSpeed: 280,
                    Route: "OAK6 SUNOL",
                    Remarks: "PDC RECEIVED",
                    Scratchpad1: "SJC",
                    BeaconCode: 4321,
                    BeaconAssignedByFacilityId: "NCT",
                    BeaconAssignedBySectorId: "OAK"
                ),
                "STRIP_7"
            ),
            new RecordedRequestNewBeaconCode(31.0, "N123AB", "ZOA", "40"),
            new RecordedWeatherChange(40.0, "{\"metars\":[\"KOAK 011253Z 29012KT 10SM FEW015 18/11 A2992\"]}", ReconstructMetars: true),
            new RecordedSettingChange(45.0, "AutoCrossRunway", "true"),
            new RecordedArrivalGeneratorsChange(50.0, "[{\"Fix\":\"SUNOL\",\"IntervalSeconds\":180,\"Enabled\":true}]"),
            new RecordedAircraftSpawn(55.0, SampleAircraft("SKW5555", "CRJ2")) { IsSynthetic = true },
            new RecordedLiveTrafficSample(
                60.0,
                "UAL1234",
                new LiveTrafficSample(
                    ObservedAtSimSeconds: 60.0,
                    Lat: 37.7213,
                    Lon: -122.2208,
                    AltitudeFt: 3500,
                    GroundSpeedKts: 210,
                    TrueTrackDeg: 118,
                    VerticalSpeedFpm: -700,
                    Source: LiveTrafficSource.Stars,
                    BeaconCode: 4321
                )
                {
                    Instance = "NCT",
                    ObservedAtUtc = new DateTimeOffset(2026, 5, 1, 17, 43, 0, TimeSpan.Zero),
                    AssignedAltitudeFt = 4000,
                    ClearedHeadingDeg = 280,
                    ClearedSpeedKts = 210,
                },
                SampleAircraft("UAL1234", "B739")
            ),
            new RecordedLiveTrafficRemoval(65.0, "UAL1234", LiveTrafficRemovalReason.OutOfScope),
            new RecordedLiveTrafficStatus(
                70.0,
                new DateTimeOffset(2026, 5, 1, 17, 43, 10, TimeSpan.Zero),
                FeedConfigured: true,
                Connected: true,
                LastMessageAgeSeconds: 1.25,
                TracksInScope: 14
            ),
            new RecordedAsdexMutation(75.0, "EditDbFields", "ac-0042", "SKW5555", "1234", "L", "CRJ2", "SUNOL", "SJC", "27"),
            new RecordedSaidMutation(80.0, "Tag", "ac-0043", "N123AB", "0431", "S", "C172", "OAK", "VFR", null),
            new RecordedStarsSharedStateChange(
                85.0,
                "AAL100",
                "OAK_TWR",
                new SharedStateDto
                {
                    ForceFdb = true,
                    IsHighlighted = false,
                    LeaderDirection = 90,
                    QueriedUntilElapsedSeconds = 92.0,
                    WasPreviouslyOwned = true,
                    TpaType = 1,
                    TpaSize = 3.0,
                    IsRecentlyAcceptedIncomingPointout = true,
                }
            ),
            new RecordedClearanceChange(
                90.0,
                "SWA22",
                new AircraftClearanceDto
                {
                    Expect = "AS FILED",
                    Sid = "OAK6",
                    Transition = "SUNOL",
                    Climbout = "RV",
                    Climbvia = "N",
                    InitialAlt = "5000",
                    ContactInfo = "NORCAL DEPARTURE 135.65",
                    LocalInfo = "RWY 30",
                    DepFreq = "135.65",
                }
            ),
            new RecordedHoldAnnotationChange(
                95.0,
                "AAL100",
                new AircraftHoldAnnotationDto
                {
                    Fix = "SUNOL",
                    Direction = 1,
                    Turns = 1,
                    LegLength = 5,
                    LegLengthInNm = true,
                    Efc = 1815,
                }
            ),
            new RecordedEramEntry(100.0, "AAL100", "QR 350", "07"),
            new RecordedEramCrrGroup(105.0, "ALPHA", "Green", 37.7213, -122.2208),
            new RecordedStripRequest(110.0, "AAL100", "OAK", "STRIP_11"),
            new RecordedAsdexSafetyLogicChange(115.0, "OAK", "{\"runwayAreas\":[{\"runwayId\":\"30\"}],\"activeConfigurationId\":\"cfg-1\"}"),
            new RecordedAttendanceChange(120.0, ["OAK_TWR", "OAK_GND", "NCT_W_APP"]),
            new RecordedAutoTrackChange(125.0, "OAK_TWR", ["OAK", "-HWD"]),
        };

        return samples.ToDictionary(s => s.GetType());
    }

    private static AircraftSnapshotDto SampleAircraft(string callsign, string aircraftType) =>
        new AircraftState
        {
            Callsign = callsign,
            AircraftType = aircraftType,
            Cid = "1234567",
            AirportId = "OAK",
            Position = new LatLon(37.7213, -122.2208),
            Altitude = 3000,
            IndicatedAirspeed = 210,
        }.ToSnapshot();
}
