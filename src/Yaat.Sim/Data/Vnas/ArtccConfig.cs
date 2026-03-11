using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yaat.Sim.Data.Vnas;

public class ArtccConfigRoot
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("lastUpdatedAt")]
    public string LastUpdatedAt { get; set; } = "";

    [JsonPropertyName("facility")]
    public FacilityConfig Facility { get; set; } = null!;

    [JsonPropertyName("videoMaps")]
    public List<VideoMapConfig> VideoMaps { get; set; } = [];
}

public class FacilityConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("positions")]
    public List<PositionConfig> Positions { get; set; } = [];

    [JsonPropertyName("childFacilities")]
    public List<FacilityConfig> ChildFacilities { get; set; } = [];

    [JsonPropertyName("starsConfiguration")]
    public StarsConfig? StarsConfiguration { get; set; }

    [JsonPropertyName("towerCabConfiguration")]
    public TowerCabConfig? TowerCabConfiguration { get; set; }

    [JsonPropertyName("asdexConfiguration")]
    public AsdexConfig? AsdexConfiguration { get; set; }
}

public class PositionConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("starred")]
    public bool Starred { get; set; }

    [JsonPropertyName("radioName")]
    public string RadioName { get; set; } = "";

    [JsonPropertyName("callsign")]
    public string Callsign { get; set; } = "";

    [JsonPropertyName("frequency")]
    public long Frequency { get; set; }

    [JsonPropertyName("eramConfiguration")]
    public EramPositionConfig? EramConfiguration { get; set; }

    [JsonPropertyName("starsConfiguration")]
    public StarsPositionConfig? StarsConfiguration { get; set; }
}

public class EramPositionConfig
{
    [JsonPropertyName("sectorId")]
    public string SectorId { get; set; } = "";
}

public class StarsPositionConfig
{
    [JsonPropertyName("areaId")]
    public string AreaId { get; set; } = "";

    [JsonPropertyName("colorSet")]
    public string ColorSet { get; set; } = "Tcw";

    [JsonPropertyName("tcpId")]
    public string TcpId { get; set; } = "";
}

public class StarsConfig
{
    [JsonPropertyName("automaticConsolidation")]
    public bool AutomaticConsolidation { get; set; }

    [JsonPropertyName("tcps")]
    public List<TcpConfig> Tcps { get; set; } = [];

    [JsonPropertyName("lists")]
    public List<StarsListConfig> Lists { get; set; } = [];

    [JsonPropertyName("areas")]
    public List<StarsAreaConfig> Areas { get; set; } = [];

    [JsonPropertyName("videoMapIds")]
    public List<string> VideoMapIds { get; set; } = [];

    [JsonPropertyName("mapGroups")]
    public List<StarsMapGroupConfig> MapGroups { get; set; } = [];

    [JsonPropertyName("atpaVolumes")]
    public List<AtpaVolumeConfig> AtpaVolumes { get; set; } = [];

    [JsonPropertyName("beaconCodeBanks")]
    public List<BeaconCodeBankConfig> BeaconCodeBanks { get; set; } = [];

    [JsonPropertyName("internalAirports")]
    public List<string> InternalAirports { get; set; } = [];

    [JsonPropertyName("primaryScratchpadRules")]
    public List<ScratchpadRuleConfig> PrimaryScratchpadRules { get; set; } = [];

    [JsonPropertyName("secondaryScratchpadRules")]
    public List<ScratchpadRuleConfig> SecondaryScratchpadRules { get; set; } = [];

    [JsonPropertyName("allow4CharacterScratchpad")]
    public bool Allow4CharacterScratchpad { get; set; }

    [JsonPropertyName("starsHandoffIds")]
    public List<StarsHandoffIdConfig> StarsHandoffIds { get; set; } = [];

    [JsonPropertyName("configurationPlans")]
    public List<StarsConfigurationPlanConfig> ConfigurationPlans { get; set; } = [];

    [JsonPropertyName("terminalSectors")]
    public List<string> TerminalSectors { get; set; } = [];

    [JsonPropertyName("impliedCompoundCommands")]
    public List<ImpliedCompoundCommandConfig> ImpliedCompoundCommands { get; set; } = [];

    [JsonPropertyName("rnavPatterns")]
    public List<string> RnavPatterns { get; set; } = [];

    [JsonPropertyName("rpcs")]
    public List<RpcConfig> Rpcs { get; set; } = [];

    [JsonPropertyName("recatEnabled")]
    public bool RecatEnabled { get; set; }
}

public class StarsMapGroupConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("mapIds")]
    public List<int?> MapIds { get; set; } = [];

    [JsonPropertyName("tcps")]
    public List<string> Tcps { get; set; } = [];
}

public class StarsListConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("coordinationChannel")]
    public CoordinationChannelConfig? CoordinationChannel { get; set; }

    [JsonPropertyName("showTitle")]
    public string ShowTitle { get; set; } = "DisplayIfEntries";

    [JsonPropertyName("numberOfEntries")]
    public int NumberOfEntries { get; set; }

    [JsonPropertyName("persistentEntries")]
    public bool PersistentEntries { get; set; }

    [JsonPropertyName("showMore")]
    public bool ShowMore { get; set; }

    [JsonPropertyName("showLineNumbers")]
    public bool ShowLineNumbers { get; set; }

    [JsonPropertyName("sortField")]
    public string SortField { get; set; } = "";

    [JsonPropertyName("sortIsAscending")]
    public bool SortIsAscending { get; set; } = true;

    [JsonPropertyName("secondarySortField")]
    public string? SecondarySortField { get; set; }

    [JsonPropertyName("secondarySortIsAscending")]
    public bool? SecondarySortIsAscending { get; set; }
}

public class CoordinationChannelConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("flightType")]
    public string FlightType { get; set; } = "";

    [JsonPropertyName("sendingTcpIds")]
    public List<string> SendingTcpIds { get; set; } = [];

    [JsonPropertyName("receivers")]
    public List<CoordinationReceiverConfig> Receivers { get; set; } = [];
}

public class CoordinationReceiverConfig
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("receivingTcpId")]
    public string ReceivingTcpId { get; set; } = "";

    [JsonPropertyName("autoAcknowledge")]
    public bool AutoAcknowledge { get; set; }
}

public class TcpConfig
{
    [JsonPropertyName("subset")]
    public int Subset { get; set; }

    [JsonPropertyName("sectorId")]
    public string SectorId { get; set; } = "";

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("parentTcpId")]
    public string? ParentTcpId { get; set; }

    [JsonPropertyName("terminalSector")]
    public string? TerminalSector { get; set; }
}

public class TowerCabConfig
{
    [JsonPropertyName("aircraftVisibilityCeiling")]
    public double AircraftVisibilityCeiling { get; set; } = 6000;

    [JsonPropertyName("towerLocation")]
    public TowerLocationConfig? TowerLocation { get; set; }
}

public class TowerLocationConfig
{
    [JsonPropertyName("lat")]
    public double Lat { get; set; }

    [JsonPropertyName("lon")]
    public double Lon { get; set; }
}

public class AsdexConfig
{
    [JsonPropertyName("targetVisibilityRange")]
    public double TargetVisibilityRange { get; set; } = 15;

    [JsonPropertyName("targetVisibilityCeiling")]
    public double TargetVisibilityCeiling { get; set; } = 1500;

    [JsonPropertyName("videoMapId")]
    public string? VideoMapId { get; set; }

    [JsonPropertyName("defaultRotation")]
    public int DefaultRotation { get; set; }

    [JsonPropertyName("defaultZoomRange")]
    public int DefaultZoomRange { get; set; }

    [JsonPropertyName("fixRules")]
    public List<AsdexFixRuleConfig> FixRules { get; set; } = [];

    [JsonPropertyName("useDestinationIdAsFix")]
    public bool UseDestinationIdAsFix { get; set; }

    [JsonPropertyName("runwayConfigurations")]
    public List<AsdexRunwayConfigurationConfig> RunwayConfigurations { get; set; } = [];

    [JsonPropertyName("positions")]
    public List<AsdexPositionConfig> Positions { get; set; } = [];

    [JsonPropertyName("defaultPositionId")]
    public string? DefaultPositionId { get; set; }

    [JsonPropertyName("towerLocation")]
    public TowerLocationConfig? TowerLocation { get; set; }
}

public class StarsAreaConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("visibilityCenter")]
    public TowerLocationConfig? VisibilityCenter { get; set; }

    [JsonPropertyName("surveillanceRange")]
    public double SurveillanceRange { get; set; } = 150;

    [JsonPropertyName("underlyingAirports")]
    public List<string> UnderlyingAirports { get; set; } = [];

    [JsonPropertyName("videoMapIds")]
    public List<string> VideoMapIds { get; set; } = [];

    [JsonPropertyName("ssaAirports")]
    public List<string> SsaAirports { get; set; } = [];

    [JsonPropertyName("towerListConfigurations")]
    public List<TowerListConfig> TowerListConfigurations { get; set; } = [];

    [JsonPropertyName("ldbBeaconCodesInhibited")]
    public bool LdbBeaconCodesInhibited { get; set; }

    [JsonPropertyName("pdbGroundSpeedInhibited")]
    public bool PdbGroundSpeedInhibited { get; set; }

    [JsonPropertyName("displayRequestedAltInFdb")]
    public bool DisplayRequestedAltInFdb { get; set; }

    [JsonPropertyName("useVfrPositionSymbol")]
    public bool UseVfrPositionSymbol { get; set; }

    [JsonPropertyName("showDestinationDepartures")]
    public bool ShowDestinationDepartures { get; set; }

    [JsonPropertyName("showDestinationSatelliteArrivals")]
    public bool ShowDestinationSatelliteArrivals { get; set; }

    [JsonPropertyName("showDestinationPrimaryArrivals")]
    public bool ShowDestinationPrimaryArrivals { get; set; }

    [JsonPropertyName("recatEnabled")]
    public bool RecatEnabled { get; set; }
}

public class VideoMapConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("shortName")]
    public string ShortName { get; set; } = "";

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    [JsonPropertyName("sourceFileName")]
    public string SourceFileName { get; set; } = "";

    [JsonPropertyName("starsBrightnessCategory")]
    public string StarsBrightnessCategory { get; set; } = "A";

    [JsonPropertyName("starsId")]
    public int StarsId { get; set; }

    [JsonPropertyName("starsAlwaysVisible")]
    public bool StarsAlwaysVisible { get; set; }

    [JsonPropertyName("tdmOnly")]
    public bool TdmOnly { get; set; }
}

public class AtpaVolumeConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("airportId")]
    public string AirportId { get; set; } = "";

    [JsonPropertyName("volumeId")]
    public string VolumeId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("runwayThreshold")]
    public TowerLocationConfig RunwayThreshold { get; set; } = new();

    [JsonPropertyName("ceiling")]
    public int Ceiling { get; set; }

    [JsonPropertyName("floor")]
    public int Floor { get; set; }

    [JsonPropertyName("magneticHeading")]
    public int MagneticHeading { get; set; }

    [JsonPropertyName("maximumHeadingDeviation")]
    public int MaximumHeadingDeviation { get; set; }

    [JsonPropertyName("length")]
    public double Length { get; set; }

    [JsonPropertyName("widthLeft")]
    public double WidthLeft { get; set; }

    [JsonPropertyName("widthRight")]
    public double WidthRight { get; set; }

    [JsonPropertyName("twoPointFiveApproachEnabled")]
    public bool TwoPointFiveApproachEnabled { get; set; }

    [JsonPropertyName("scratchpads")]
    public List<AtpaScratchpadConfig> Scratchpads { get; set; } = [];

    [JsonPropertyName("tcps")]
    public List<AtpaVolumeTcpConfig> Tcps { get; set; } = [];

    [JsonPropertyName("excludedTcpIds")]
    public List<string> ExcludedTcpIds { get; set; } = [];

    [JsonPropertyName("leaderDirections")]
    public List<string> LeaderDirections { get; set; } = [];
}

public class AtpaVolumeTcpConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("tcpId")]
    public string TcpId { get; set; } = "";

    [JsonPropertyName("coneType")]
    public string ConeType { get; set; } = "";
}

public class AtpaScratchpadConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("entry")]
    public string Entry { get; set; } = "";

    /// <summary>"One" or "Two" — which scratchpad slot this applies to.</summary>
    [JsonPropertyName("scratchPadNumber")]
    public string ScratchPadNumber { get; set; } = "";

    /// <summary>"Exclude" (remove from sequence) or "Ineligible" (not eligible for ATPA).</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";
}

public class BeaconCodeBankConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("start")]
    public int Start { get; set; }

    [JsonPropertyName("end")]
    public int End { get; set; }
}

public class ScratchpadRuleConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("airportIds")]
    public List<string> AirportIds { get; set; } = [];

    [JsonPropertyName("searchPattern")]
    public string SearchPattern { get; set; } = "";

    [JsonPropertyName("minAltitude")]
    public int? MinAltitude { get; set; }

    [JsonPropertyName("maxAltitude")]
    public int? MaxAltitude { get; set; }

    [JsonPropertyName("template")]
    public string Template { get; set; } = "";
}

public class StarsHandoffIdConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("facilityId")]
    public string FacilityId { get; set; } = "";

    [JsonPropertyName("handoffNumber")]
    public int HandoffNumber { get; set; }
}

public class StarsConfigurationPlanConfig
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

public class ImpliedCompoundCommandConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
}

public class TowerListConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("airportId")]
    public string AirportId { get; set; } = "";

    [JsonPropertyName("range")]
    public double Range { get; set; }
}

public class AsdexFixRuleConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("searchPattern")]
    public string SearchPattern { get; set; } = "";

    [JsonPropertyName("fixId")]
    public string FixId { get; set; } = "";
}

public class AsdexPositionConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("runwayIds")]
    public List<string> RunwayIds { get; set; } = [];
}

public class AsdexRunwayConfigurationConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("arrivalRunwayIds")]
    public List<string> ArrivalRunwayIds { get; set; } = [];

    [JsonPropertyName("departureRunwayIds")]
    public List<string> DepartureRunwayIds { get; set; } = [];

    [JsonPropertyName("holdShortRunwayPairs")]
    public List<JsonElement> HoldShortRunwayPairs { get; set; } = [];
}

public class RpcConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("airportId")]
    public string AirportId { get; set; } = "";

    [JsonPropertyName("positionSymbolTie")]
    public string PositionSymbolTie { get; set; } = "";

    [JsonPropertyName("positionSymbolStagger")]
    public string PositionSymbolStagger { get; set; } = "";

    [JsonPropertyName("masterRunway")]
    public RpcRunwayConfig? MasterRunway { get; set; }

    [JsonPropertyName("slaveRunway")]
    public RpcRunwayConfig? SlaveRunway { get; set; }
}

public class RpcRunwayConfig
{
    [JsonPropertyName("runwayId")]
    public string RunwayId { get; set; } = "";

    [JsonPropertyName("headingTolerance")]
    public double HeadingTolerance { get; set; }

    [JsonPropertyName("nearSideHalfWidth")]
    public double NearSideHalfWidth { get; set; }

    [JsonPropertyName("farSideHalfWidth")]
    public double FarSideHalfWidth { get; set; }

    [JsonPropertyName("nearSideDistance")]
    public double NearSideDistance { get; set; }

    [JsonPropertyName("regionLength")]
    public double RegionLength { get; set; }

    [JsonPropertyName("targetReferencePoint")]
    public TowerLocationConfig? TargetReferencePoint { get; set; }

    [JsonPropertyName("targetReferenceLineHeading")]
    public double TargetReferenceLineHeading { get; set; }

    [JsonPropertyName("targetReferenceLineLength")]
    public double TargetReferenceLineLength { get; set; }

    [JsonPropertyName("targetReferencePointAltitude")]
    public double TargetReferencePointAltitude { get; set; }

    [JsonPropertyName("imageReferencePoint")]
    public TowerLocationConfig? ImageReferencePoint { get; set; }

    [JsonPropertyName("imageReferenceLineHeading")]
    public double ImageReferenceLineHeading { get; set; }

    [JsonPropertyName("imageReferenceLineLength")]
    public double ImageReferenceLineLength { get; set; }

    [JsonPropertyName("tieModeOffset")]
    public double TieModeOffset { get; set; }

    [JsonPropertyName("descentPointDistance")]
    public double DescentPointDistance { get; set; }

    [JsonPropertyName("descentPointAltitude")]
    public double DescentPointAltitude { get; set; }

    [JsonPropertyName("abovePathTolerance")]
    public double AbovePathTolerance { get; set; }

    [JsonPropertyName("belowPathTolerance")]
    public double BelowPathTolerance { get; set; }

    [JsonPropertyName("defaultLeaderDirection")]
    public string DefaultLeaderDirection { get; set; } = "";

    [JsonPropertyName("scratchpadPatterns")]
    public List<string> ScratchpadPatterns { get; set; } = [];

    [JsonPropertyName("secondaryScratchpadPatterns")]
    public List<string> SecondaryScratchpadPatterns { get; set; } = [];
}
