using Yaat.Client.ContextMenus;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Situation;

namespace Yaat.Client.UI.Tests.Fakes;

/// <summary>
/// A minimal <see cref="IMenuAircraft"/>: never live traffic, IFR unless told otherwise, with the warp seed values, the
/// assignments the flight group headers show, the type the speed items read and the route fixes the navigation pickers offer
/// settable. A record, so a test varies one with <c>with</c>.
/// </summary>
internal sealed record FakeMenuAircraft : IMenuAircraft
{
    public string Callsign { get; init; } = "N123AB";

    public bool HasActiveTaxiRoute { get; init; }

    public bool HasStoredTaxiRoute { get; init; }

    public bool HasTaxiwayHoldShortAhead { get; init; }

    public bool HasCfrWindow { get; init; }

    public bool IsHeldForRelease { get; init; }

    public bool IsLiveTraffic => false;

    public bool AssumedFromLiveTraffic => false;

    public bool IsOnGround { get; init; }

    public string CurrentTaxiway { get; init; } = "";

    public string ParkingSpot { get; init; } = "";

    public string? GroundAirportId { get; init; }

    public LatLon Position { get; init; }

    public string? LastReportedTrafficCallsign { get; init; }

    public bool IsHeld => false;

    public string? HoldKind => null;

    public string? HoldYieldTarget => null;

    public string? AutoYieldTarget => null;

    public bool AutoYieldIsFollowing => false;

    public bool IsDelayed => false;

    public bool HasQueuedPatternEntry => false;

    public string FlightRules { get; init; } = "IFR";

    public string CurrentPhase { get; init; } = "";

    public string AssignedRunway { get; init; } = "";

    public string PhaseSequence => "";

    public string LandingClearance => "";

    public string PendingLandingClearance => "";

    public double HeadingDegrees { get; init; }

    /// <summary>The fake has no declination: its magnetic heading is its <see cref="HeadingDegrees"/>.</summary>
    public double MagneticHeadingDegrees => HeadingDegrees;

    public double AltitudeFeet { get; init; }

    public double IndicatedAirspeedKnots { get; init; }

    public double GroundSpeedKnots { get; init; }

    public string NavigatingTo { get; init; } = "";

    public IReadOnlyList<string> NavigationRoute { get; init; } = [];

    public MagneticHeading? AssignedHeading { get; init; }

    public double? AssignedAltitude { get; init; }

    public double? AssignedSpeed { get; init; }

    public string FiledAircraftType { get; init; } = "";

    public string DisplayAircraftType => FiledAircraftType;

    public string BaseAircraftType { get; init; } = "";

    public AircraftCategory Category { get; init; } = AircraftCategory.Jet;

    public int? FiledVfrCruiseFeet => null;

    public string Note => "";

    public string Destination { get; init; } = "";

    public string Departure { get; init; } = "";

    public string Route { get; init; } = "";

    public string ActiveSidId { get; init; } = "";

    public string ActiveStarId { get; init; } = "";

    public string? ActiveApproachId { get; init; }

    public string? ExpectedApproach { get; init; }

    public string? Owner { get; init; }

    public string? OwnerSectorCode { get; init; }

    public string? HandoffPeer { get; init; }

    public string? HandoffPeerSectorCode { get; init; }

    public string? PointoutStatus { get; init; }

    public AircraftSituation Situation { get; init; }

    public SituationFlags SituationFlags { get; init; }

    public string? NextCrossingRunway { get; init; }

    public IReadOnlyList<ExitAheadDto>? ExitsAhead { get; init; }

    public IReadOnlyList<GroundMeetPointDto>? MeetPoints { get; init; }

    public StandDeparture? StandDeparture { get; init; }

    public IReadOnlyList<string> RouteFixes { get; init; } = [];

    public IReadOnlyList<string> RouteFixNames() => RouteFixes;
}
