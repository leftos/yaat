using static Yaat.Sim.Commands.CanonicalCommandType;

namespace Yaat.Sim.Speech;

/// <summary>
/// Static catalog of phraseology → canonical command rules. Organized by command category to
/// mirror <c>CommandRegistry</c>. The order within a category rarely matters because
/// <see cref="PhraseologyMapper"/> picks the longest match, with rules containing fewer
/// captures preferred on ties (so literal-only rules beat capture-group rules of the same
/// length).
/// </summary>
/// <remarks>
/// Pattern syntax:
/// <list type="bullet">
///   <item><description><c>literal</c> — case-insensitive exact token match.</description></item>
///   <item><description><c>literal?</c> — optional token.</description></item>
///   <item><description><c>{name}</c> — capture one token into the named group.</description></item>
/// </list>
/// Rules are written against the NORMALIZED transcript:
/// <list type="bullet">
///   <item><description><see cref="AtcNumberParser.NormalizeDigits"/> has replaced spoken numbers with digit strings.</description></item>
///   <item><description>Runway designators like "two eight right" have been collapsed to "28R".</description></item>
///   <item><description>Filler words ("uh", "please", "sir") have been stripped.</description></item>
///   <item><description>Tokens are lowercase.</description></item>
/// </list>
/// Commands intentionally NOT covered by these rules (out of pilot phraseology scope):
/// sim-control (Force*, Warp, Delete, Pause, Rate), track (TrackAircraft, Handoff, Accept),
/// data (Consolidate, CreateFlightPlan, ChangeDestination), coordination (RD/RDH/RDR/RDACK),
/// admin-only bulk ops (SquawkAll, TaxiAll, BreakConflict).
/// </remarks>
public static class PhraseologyRules
{
    public static IReadOnlyList<PhraseologyRule> All { get; } = Build();

    private static List<PhraseologyRule> Build()
    {
        var rules = new List<PhraseologyRule>();
        rules.AddRange(HeadingRules());
        rules.AddRange(AltitudeSpeedRules());
        rules.AddRange(NavigationRules());
        rules.AddRange(TowerRules());
        rules.AddRange(ApproachRules());
        rules.AddRange(PatternRules());
        rules.AddRange(HoldRules());
        rules.AddRange(HelicopterRules());
        rules.AddRange(TransponderRules());
        rules.AddRange(GroundRules());
        rules.AddRange(BroadcastRules());
        return rules;
    }

    // --- Heading (CommandRegistry.HeadingCommands) ---

    private static PhraseologyRule[] HeadingRules() =>
        [
            new(["turn", "left", "heading", "{hdg}"], "TL {hdg}", TurnLeft),
            new(["turn", "right", "heading", "{hdg}"], "TR {hdg}", TurnRight),
            // Relative turns — canonical 7110.65 5-6-2 form is "TURN (N) DEGREES LEFT/RIGHT".
            new(["turn", "{deg}", "degrees", "left"], "RELL {deg}", RelativeLeft),
            new(["turn", "{deg}", "degrees", "right"], "RELR {deg}", RelativeRight),
            new(["fly", "heading", "{hdg}"], "FH {hdg}", FlyHeading),
            new(["heading", "{hdg}"], "FH {hdg}", FlyHeading),
            new(["fly", "present", "heading"], "FPH", FlyPresentHeading),
            new(["maintain", "present", "heading"], "FPH", FlyPresentHeading),
        ];

    // --- Altitude / Speed (CommandRegistry.AltitudeSpeedCommands) ---

    private static PhraseologyRule[] AltitudeSpeedRules() =>
        [
            // Climb forms
            new(["climb", "and?", "maintain", "{alt}"], "CM {alt}", ClimbMaintain),
            new(["climb", "to?", "{alt}"], "CM {alt}", ClimbMaintain),
            new(["expedite", "climb", "to?", "{alt}"], "EXP {alt}", Expedite),
            new(["expedite", "climb"], "EXP", Expedite),
            // Descend forms
            new(["descend", "and?", "maintain", "{alt}"], "DM {alt}", DescendMaintain),
            new(["descend", "to?", "{alt}"], "DM {alt}", DescendMaintain),
            new(["expedite", "descent", "to?", "{alt}"], "EXP {alt}", Expedite),
            new(["expedite", "descent"], "EXP", Expedite),
            // Neutral "maintain {alt}" — caller may want CM vs DM based on current alt.
            // For now we emit CM; a future iteration can add context-sensitive dispatch.
            new(["maintain", "{alt}"], "CM {alt}", ClimbMaintain),
            // Speed — 7110.65 5-7-2 canonical form is "MAINTAIN (speed) KNOTS".
            new(["maintain", "{spd}", "knots"], "SPD {spd}", Speed),
            new(["reduce", "speed", "to?", "{spd}"], "SPD {spd}", Speed),
            new(["increase", "speed", "to?", "{spd}"], "SPD {spd}", Speed),
            new(["maintain", "speed", "{spd}"], "SPD {spd}", Speed),
            new(["speed", "{spd}"], "SPD {spd}", Speed),
            new(["slow", "to?", "{spd}"], "SPD {spd}", Speed),
            new(["resume", "normal", "speed"], "RNS", ResumeNormalSpeed),
            new(["no", "speed", "restrictions"], "DSR", DeleteSpeedRestrictions),
            new(["delete", "speed", "restrictions"], "DSR", DeleteSpeedRestrictions),
            // "Reduce to final approach speed" / "reduce to final" aren't in the 7110.65 phrase
            // boxes but are commonly spoken on frequency. Keep as recognition rules.
            new(["reduce", "to", "final", "approach", "speed"], "RFAS", ReduceToFinalApproachSpeed),
            new(["reduce", "to", "final"], "RFAS", ReduceToFinalApproachSpeed),
            new(["normal", "rate"], "NORM", NormalRate),
            new(["maintain", "mach", "{mach}"], "MACH {mach}", Mach),
            new(["mach", "{mach}"], "MACH {mach}", Mach),
        ];

    // --- Navigation (CommandRegistry.NavigationCommands) ---

    private static PhraseologyRule[] NavigationRules() =>
        [
            new(["proceed", "direct", "to?", "{fix}"], "DCT {fix}", DirectTo),
            new(["direct", "to?", "{fix}"], "DCT {fix}", DirectTo),
            new(["fly", "direct", "{fix}"], "DCT {fix}", DirectTo),
            new(["turn", "left", "direct", "to?", "{fix}"], "TLDCT {fix}", TurnLeftDirectTo),
            new(["turn", "right", "direct", "to?", "{fix}"], "TRDCT {fix}", TurnRightDirectTo),
            new(["when", "able", "direct", "to?", "{fix}"], "ADCT {fix}", AppendDirectTo),
            new(["after", "{current}", "direct", "to?", "{fix}"], "ADCT {fix}", AppendDirectTo),
        ];

    // --- Tower (CommandRegistry.TowerCommands) ---

    private static PhraseologyRule[] TowerRules() =>
        [
            // Line up and wait
            new(["line", "up", "and", "wait", "runway", "{rwy}"], "LUAW", LineUpAndWait),
            new(["line", "up", "and", "wait"], "LUAW", LineUpAndWait),
            new(["position", "and", "hold"], "LUAW", LineUpAndWait), // legacy US phraseology
            // Cleared for takeoff
            new(["cleared", "for", "takeoff", "runway", "{rwy}"], "CTO", ClearedForTakeoff),
            new(["cleared", "for", "takeoff"], "CTO", ClearedForTakeoff),
            new(["clear", "for", "takeoff"], "CTO", ClearedForTakeoff),
            new(["cancel", "takeoff", "clearance"], "CTOC", CancelTakeoffClearance),
            // Cleared to land
            new(["cleared", "to", "land", "runway", "{rwy}"], "CLAND", ClearedToLand),
            new(["cleared", "to", "land"], "CLAND", ClearedToLand),
            new(["clear", "to", "land"], "CLAND", ClearedToLand),
            new(["cancel", "landing", "clearance"], "CLC", CancelLandingClearance),
            // Land and hold short
            new(["cleared", "to", "land", "runway", "{rwy}", "hold", "short", "of?", "runway", "{holdrwy}"], "LAHSO {holdrwy}", LandAndHoldShort),
            new(["lahso", "runway", "{rwy}"], "LAHSO {rwy}", LandAndHoldShort),
            // Go around
            new(["go", "around", "fly", "heading", "{hdg}"], "GA {hdg}", GoAround),
            new(["go", "around", "make", "left", "traffic"], "GA MLT", GoAround),
            new(["go", "around", "make", "right", "traffic"], "GA MRT", GoAround),
            new(["go", "around"], "GA", GoAround),
            // Option/touch-and-go
            new(["cleared", "touch", "and", "go", "runway", "{rwy}"], "TG", TouchAndGo),
            new(["cleared", "for", "touch", "and", "go"], "TG", TouchAndGo),
            new(["cleared", "touch", "and", "go"], "TG", TouchAndGo),
            new(["touch", "and", "go"], "TG", TouchAndGo),
            new(["cleared", "for", "stop", "and", "go"], "SG", StopAndGo),
            new(["cleared", "stop", "and", "go"], "SG", StopAndGo),
            new(["stop", "and", "go"], "SG", StopAndGo),
            new(["cleared", "for", "low", "approach"], "LA", LowApproach),
            new(["cleared", "low", "approach"], "LA", LowApproach),
            new(["cleared", "for", "the", "option"], "COPT", ClearedForOption),
            new(["cleared", "for", "option"], "COPT", ClearedForOption),
        ];

    // --- Approach (CommandRegistry.ApproachCommands) ---

    private static PhraseologyRule[] ApproachRules() =>
        [
            // Cleared — ILS/RNAV use CAPP with type-prefixed runway (ILS28R, RNAV28R).
            new(["cleared", "ils", "runway", "{rwy}", "approach"], "CAPP ILS{rwy}", ClearedApproach),
            new(["cleared", "ils", "{rwy}", "approach"], "CAPP ILS{rwy}", ClearedApproach),
            new(["cleared", "rnav", "runway", "{rwy}", "approach"], "CAPP RNAV{rwy}", ClearedApproach),
            new(["cleared", "rnav", "{rwy}", "approach"], "CAPP RNAV{rwy}", ClearedApproach),
            new(["cleared", "straight", "in", "ils", "runway", "{rwy}", "approach"], "CAPPSI ILS{rwy}", ClearedApproachStraightIn),
            new(["cleared", "straight", "in", "rnav", "runway", "{rwy}", "approach"], "CAPPSI RNAV{rwy}", ClearedApproachStraightIn),
            // Visual approach — distinct canonical (CVA, not CAPP). 7110.65 7-4-3.b:
            // "CLEARED VISUAL APPROACH RUNWAY (number)".
            new(["cleared", "visual", "approach", "runway", "{rwy}"], "CVA {rwy}", ClearedVisualApproach),
            new(["cleared", "visual", "approach", "{rwy}"], "CVA {rwy}", ClearedVisualApproach),
            // Generic auto-resolve form
            new(["cleared", "approach"], "CAPP", ClearedApproach),
            new(["cleared", "for", "the?", "approach"], "CAPP", ClearedApproach),
            // Join approach
            new(["join", "the?", "ils", "runway", "{rwy}", "approach"], "JAPP ILS{rwy}", JoinApproach),
            new(["join", "the?", "rnav", "runway", "{rwy}", "approach"], "JAPP RNAV{rwy}", JoinApproach),
            // Expect approach — 7110.65 form is "EXPECT (type) APPROACH RUNWAY (number)".
            new(["expect", "ils", "runway", "{rwy}", "approach"], "EAPP ILS{rwy}", ExpectApproach),
            new(["expect", "ils", "approach", "runway", "{rwy}"], "EAPP ILS{rwy}", ExpectApproach),
            new(["expect", "rnav", "runway", "{rwy}", "approach"], "EAPP RNAV{rwy}", ExpectApproach),
            new(["expect", "rnav", "approach", "runway", "{rwy}"], "EAPP RNAV{rwy}", ExpectApproach),
            new(["expect", "visual", "approach", "runway", "{rwy}"], "EAPP VIS{rwy}", ExpectApproach),
            // Traffic following
            new(["follow", "traffic", "{callsign}"], "FOLLOW {callsign}", Follow),
            new(["follow", "the?", "{callsign}"], "FOLLOW {callsign}", Follow),
            // Report-in-sight — 7110.65 5-11-1 uses "REPORT (airport) IN SIGHT".
            // "Field in sight" is colloquial; we accept both.
            new(["report", "airport", "in", "sight"], "RFIS", ReportFieldInSight),
            new(["report", "field", "in", "sight"], "RFIS", ReportFieldInSight),
            new(["report", "traffic", "in", "sight"], "RTIS", ReportTrafficInSight),
        ];

    // --- Pattern (CommandRegistry.PatternCommands) ---

    private static PhraseologyRule[] PatternRules() =>
        [
            new(["enter", "left", "downwind", "runway", "{rwy}"], "ELD {rwy}", EnterLeftDownwind),
            new(["enter", "right", "downwind", "runway", "{rwy}"], "ERD {rwy}", EnterRightDownwind),
            new(["enter", "left", "downwind"], "ELD", EnterLeftDownwind),
            new(["enter", "right", "downwind"], "ERD", EnterRightDownwind),
            new(["enter", "left", "base", "runway", "{rwy}"], "ELB {rwy}", EnterLeftBase),
            new(["enter", "right", "base", "runway", "{rwy}"], "ERB {rwy}", EnterRightBase),
            new(["enter", "left", "base"], "ELB", EnterLeftBase),
            new(["enter", "right", "base"], "ERB", EnterRightBase),
            new(["enter", "left", "crosswind"], "ELC", EnterLeftCrosswind),
            new(["enter", "right", "crosswind"], "ERC", EnterRightCrosswind),
            new(["enter", "final"], "EF", EnterFinal),
            new(["make", "left", "traffic", "runway", "{rwy}"], "MLT {rwy}", MakeLeftTraffic),
            new(["make", "right", "traffic", "runway", "{rwy}"], "MRT {rwy}", MakeRightTraffic),
            new(["make", "left", "traffic"], "MLT", MakeLeftTraffic),
            new(["make", "right", "traffic"], "MRT", MakeRightTraffic),
            new(["turn", "crosswind"], "TC", TurnCrosswind),
            new(["turn", "downwind"], "TD", TurnDownwind),
            new(["turn", "base"], "TB", TurnBase),
            new(["turn", "final"], "EF", EnterFinal),
            new(["extend", "downwind"], "EXT", ExtendDownwind),
            new(["make", "short", "approach"], "SA", MakeShortApproach),
            new(["short", "approach"], "SA", MakeShortApproach),
            new(["make", "normal", "approach"], "MNA", MakeNormalApproach),
            new(["make", "left", "three", "sixty"], "L360", MakeLeft360),
            new(["make", "right", "three", "sixty"], "R360", MakeRight360),
            new(["left", "three", "sixty"], "L360", MakeLeft360),
            new(["right", "three", "sixty"], "R360", MakeRight360),
            new(["make", "left", "two", "seventy"], "L270", MakeLeft270),
            new(["make", "right", "two", "seventy"], "R270", MakeRight270),
            new(["cancel", "the?", "two", "seventy"], "NO270", Cancel270),
            new(["circle", "the?", "airport"], "CIRCLE", CircleAirport),
        ];

    // --- Hold (CommandRegistry.HoldCommands) ---

    private static PhraseologyRule[] HoldRules() =>
        [
            new(["hold", "present", "position", "left", "turns"], "HPPL", HoldPresentPosition360Left),
            new(["hold", "present", "position", "right", "turns"], "HPPR", HoldPresentPosition360Right),
            new(["hold", "present", "position"], "HPP", HoldPresentPositionHover),
            new(["hold", "at", "{fix}", "left", "turns"], "HFIXL {fix}", HoldAtFixLeft),
            new(["hold", "at", "{fix}", "right", "turns"], "HFIXR {fix}", HoldAtFixRight),
            new(["hold", "at", "{fix}"], "HFIX {fix}", HoldAtFixHover),
        ];

    // --- Helicopter (CommandRegistry.HelicopterCommands) ---

    private static PhraseologyRule[] HelicopterRules() =>
        [
            new(["cleared", "for", "air", "taxi"], "ATXI", AirTaxi),
            new(["cleared", "air", "taxi"], "ATXI", AirTaxi),
            new(["air", "taxi", "to", "{helipad}"], "ATXI {helipad}", AirTaxi),
            new(["cleared", "takeoff", "present", "position"], "CTOPP", ClearedTakeoffPresent),
            new(["cleared", "for", "takeoff", "present", "position"], "CTOPP", ClearedTakeoffPresent),
        ];

    // --- Transponder (CommandRegistry.TransponderCommands) ---

    private static PhraseologyRule[] TransponderRules() =>
        [
            new(["squawk", "{code}"], "SQ {code}", Squawk),
            new(["squawk", "vfr"], "SQVFR", SquawkVfr),
            new(["squawk", "normal"], "SQNORM", SquawkNormal),
            new(["squawk", "standby"], "SQSBY", SquawkStandby),
            new(["squawk", "ident"], "IDENT", Ident),
            new(["ident"], "IDENT", Ident),
        ];

    // --- Ground (CommandRegistry.GroundCommands) ---

    private static PhraseologyRule[] GroundRules() =>
        [
            // Taxi — note: route capture is a simple single-token match. Multi-token
            // taxiway routes ("taxi via alpha bravo charlie") need a multi-token capture we
            // routes aren't yet supported and can be added later.
            new(["taxi", "to", "runway", "{rwy}"], "TAXI RWY {rwy}", Taxi),
            new(["taxi", "runway", "{rwy}"], "TAXI RWY {rwy}", Taxi),
            new(["pushback", "approved"], "PUSH", Pushback),
            new(["push", "back", "approved"], "PUSH", Pushback),
            new(["hold", "position"], "HOLD", HoldPosition),
            new(["resume", "taxi"], "RES", Resume),
            new(["continue", "taxi"], "RES", Resume),
            new(["cross", "runway", "{rwy}"], "CROSS {rwy}", CrossRunway),
            new(["hold", "short", "of?", "runway", "{rwy}"], "HS {rwy}", HoldShort),
            new(["hold", "short", "of?", "{taxiway}"], "HS {taxiway}", HoldShort),
            new(["follow", "the?", "{callsign}", "on", "ground"], "FOLLOWG {callsign}", FollowGround),
            new(["give", "way", "to", "{callsign}"], "GIVEWAY {callsign}", GiveWay),
            new(["exit", "left"], "EL", ExitLeft),
            new(["exit", "right"], "ER", ExitRight),
            new(["exit", "left", "at?", "{taxiway}"], "EL {taxiway}", ExitLeft),
            new(["exit", "right", "at?", "{taxiway}"], "ER {taxiway}", ExitRight),
            new(["exit", "at?", "{taxiway}"], "EXIT {taxiway}", ExitTaxiway),
        ];

    // --- Broadcast requests (CommandRegistry.BroadcastCommands) ---

    private static PhraseologyRule[] BroadcastRules() =>
        [
            new(["say", "speed"], "SSPD", SaySpeed),
            new(["say", "your?", "speed"], "SSPD", SaySpeed),
            new(["say", "mach"], "SMACH", SayMach),
            new(["say", "altitude"], "SALT", SayAltitude),
            new(["say", "your?", "altitude"], "SALT", SayAltitude),
            new(["say", "heading"], "SHDG", SayHeading),
            new(["say", "your?", "heading"], "SHDG", SayHeading),
            new(["say", "position"], "SPOS", SayPosition),
            new(["report", "position"], "SPOS", SayPosition),
            new(["say", "expected", "approach"], "SEAPP", SayExpectedApproach),
        ];
}
