namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// What kind of node serves as the origin or destination of a taxi-coverage
/// pair. <see cref="Parking"/> is a parking spot (looked up via
/// <c>FindParkingByName</c>). <see cref="RunwayExit"/> is a runway hold-short
/// node — the aircraft spawns at the hold-short as if it had just landed and
/// rolled off the runway, on the taxiway side of the line.
/// </summary>
public enum TaxiNodeKind
{
    Parking,
    RunwayExit,
}

/// <summary>
/// One taxi-coverage pair: spawn an aircraft of <paramref name="Category"/>
/// at <paramref name="OriginName"/> (interpreted per <paramref name="OriginKind"/>)
/// and send it to <paramref name="DestinationName"/>. The test compares
/// observed time and cumulative turn against the budget derived from the
/// optimal A* route by <see cref="Helpers.TaxiBudgetDeriver"/>.
///
/// <paramref name="PairId"/> is a short stable identifier used in the test
/// case display name and failure messages. Keep it human-readable
/// (e.g. <c>"SIG1-to-28R"</c>).
///
/// <paramref name="DestinationRunway"/> applies when
/// <paramref name="DestinationKind"/> is <see cref="TaxiNodeKind.RunwayExit"/>
/// and the controller wants a specific hold-short for a runway; for a parking
/// destination it stays null.
/// </summary>
public sealed record TaxiPair(
    string PairId,
    string AirportId,
    string OriginName,
    TaxiNodeKind OriginKind,
    string DestinationName,
    TaxiNodeKind DestinationKind,
    string? DestinationRunway,
    AircraftCategory Category,
    string AircraftType
);

/// <summary>
/// Curated taxi-coverage pairs. Two purposes:
/// <list type="bullet">
/// <item>Smoke sets (<see cref="OakSmoke"/>, <see cref="SfoSmoke"/>) run on
///   every PR. Hand-picked to cover ground-graph regions of each airport, not
///   every parking spot. The smoke sets are the regression net for the spin
///   / stall bug class.</item>
/// <item>Grid generators (separate test classes, <c>[Trait("Category", "Nightly")]</c>)
///   sweep every parking spot per airport.</item>
/// </list>
///
/// New pairs should specify <see cref="AircraftCategory.Piston"/> + a piston
/// type (C172) unless the origin is a part of the airport that primarily
/// hosts jets or turboprops — then upgrade the category and type accordingly.
/// </summary>
public static class TaxiCoverageData
{
    public const string DefaultPistonType = "C172";
    public const string DefaultJetType = "B738";

    public static readonly IReadOnlyList<TaxiPair> OakSmoke =
    [
        // --- Piston: Parking → runway (north GA / commuter → north runway) ---
        // SIG1 is the canonical north FBO. GA3 and SIG4 are the parkings
        // surfaced by the OAK north-field spin bug bundle — a healthy
        // regression net keeps both as smoke pairs even though
        // OakNorthFieldTaxiSpinTests pins the original bundle.
        new TaxiPair(
            "OAK_SIG1-to-28R_piston",
            "OAK",
            "SIG1",
            TaxiNodeKind.Parking,
            "28R",
            TaxiNodeKind.RunwayExit,
            "28R",
            AircraftCategory.Piston,
            DefaultPistonType
        ),
        new TaxiPair(
            "OAK_GA3-to-28R_piston",
            "OAK",
            "GA3",
            TaxiNodeKind.Parking,
            "28R",
            TaxiNodeKind.RunwayExit,
            "28R",
            AircraftCategory.Piston,
            DefaultPistonType
        ),
        new TaxiPair(
            "OAK_SIG4-to-28R_piston",
            "OAK",
            "SIG4",
            TaxiNodeKind.Parking,
            "28R",
            TaxiNodeKind.RunwayExit,
            "28R",
            AircraftCategory.Piston,
            DefaultPistonType
        ),
        new TaxiPair(
            "OAK_KAI1-to-28R_piston",
            "OAK",
            "KAI1",
            TaxiNodeKind.Parking,
            "28R",
            TaxiNodeKind.RunwayExit,
            "28R",
            AircraftCategory.Piston,
            DefaultPistonType
        ),
        // --- Piston: Parking → runway (south commercial / cargo → south runway) ---
        new TaxiPair(
            "OAK_Gate4-to-30_piston",
            "OAK",
            "4",
            TaxiNodeKind.Parking,
            "30",
            TaxiNodeKind.RunwayExit,
            "30",
            AircraftCategory.Piston,
            DefaultPistonType
        ),
        new TaxiPair(
            "OAK_Gate22-to-30_piston",
            "OAK",
            "22",
            TaxiNodeKind.Parking,
            "30",
            TaxiNodeKind.RunwayExit,
            "30",
            AircraftCategory.Piston,
            DefaultPistonType
        ),
        // --- Jet: Parking → runway (south cargo / commercial → south runway) ---
        // OAK's commercial gates and FedEx cargo ramp host narrowbody jets.
        // A B738 spawn at these spots is realistic and stresses the same
        // taxi pipeline with a larger turning radius.
        new TaxiPair(
            "OAK_FDX5-to-30_jet",
            "OAK",
            "FDX5",
            TaxiNodeKind.Parking,
            "30",
            TaxiNodeKind.RunwayExit,
            "30",
            AircraftCategory.Jet,
            DefaultJetType
        ),
        new TaxiPair(
            "OAK_Gate22-to-30_jet",
            "OAK",
            "22",
            TaxiNodeKind.Parking,
            "30",
            TaxiNodeKind.RunwayExit,
            "30",
            AircraftCategory.Jet,
            DefaultJetType
        ),
        // --- Piston: Parking → parking (cross-field — longest routes) ---
        // Cross-field traversals stress the chain of taxiway transitions
        // and runway crossings, where chord-chain spin bugs typically
        // surface (the OAK J chord-chain spin lived on a similar route).
        new TaxiPair(
            "OAK_SIG1-to-Gate4_piston",
            "OAK",
            "SIG1",
            TaxiNodeKind.Parking,
            "4",
            TaxiNodeKind.Parking,
            null,
            AircraftCategory.Piston,
            DefaultPistonType
        ),
        new TaxiPair(
            "OAK_Gate22-to-SIG1_piston",
            "OAK",
            "22",
            TaxiNodeKind.Parking,
            "SIG1",
            TaxiNodeKind.Parking,
            null,
            AircraftCategory.Piston,
            DefaultPistonType
        ),
        // --- Jet: Parking → parking (south commercial ↔ south cargo) ---
        new TaxiPair(
            "OAK_FDX5-to-Gate22_jet",
            "OAK",
            "FDX5",
            TaxiNodeKind.Parking,
            "22",
            TaxiNodeKind.Parking,
            null,
            AircraftCategory.Jet,
            DefaultJetType
        ),
        // --- Runway-exit → parking (just landed, taxi to gate / FBO) ---
        // Origin "RWY/TWY" format names the runway and the exit taxiway;
        // the runner picks the hold-short whose A* route to destination is
        // shortest, which auto-selects the correct side of the runway.
        // 28R exit J (high-speed) → SIG1: just landed on 28R, exit to the
        // north FBO. Mirrors a Cessna landing 28R turning off at J.
        new TaxiPair(
            "OAK_28R-J-to-SIG1_piston",
            "OAK",
            "28R/J",
            TaxiNodeKind.RunwayExit,
            "SIG1",
            TaxiNodeKind.Parking,
            null,
            AircraftCategory.Piston,
            DefaultPistonType
        ),
        // 28R exit P → Gate22: jet just landed 28R, exit P to south
        // commercial. P is a 52°/127° turn (high-speed one side, 90° the
        // other) — exit-side picker should choose the south-facing one.
        new TaxiPair(
            "OAK_28R-P-to-Gate22_jet",
            "OAK",
            "28R/P",
            TaxiNodeKind.RunwayExit,
            "22",
            TaxiNodeKind.Parking,
            null,
            AircraftCategory.Jet,
            DefaultJetType
        ),
        // 28L exit G → Gate4: jet just landed 28L, 90° exit to G, taxi to
        // south terminal gate 4.
        new TaxiPair(
            "OAK_28L-G-to-Gate4_jet",
            "OAK",
            "28L/G",
            TaxiNodeKind.RunwayExit,
            "4",
            TaxiNodeKind.Parking,
            null,
            AircraftCategory.Jet,
            DefaultJetType
        ),
        // 30 exit W3 (high-speed, 27°) → FDX5: jet landed 30, takes the
        // high-speed exit W3 toward FedEx cargo.
        new TaxiPair(
            "OAK_30-W3-to-FDX5_jet",
            "OAK",
            "30/W3",
            TaxiNodeKind.RunwayExit,
            "FDX5",
            TaxiNodeKind.Parking,
            null,
            AircraftCategory.Jet,
            DefaultJetType
        ),
    ];

    public static readonly IReadOnlyList<TaxiPair> SfoSmoke =
    [
        // --- Jet: Parking → runway (terminal gate → departure runway) ---
        new TaxiPair(
            "SFO_A12-to-1L_jet",
            "SFO",
            "A12",
            TaxiNodeKind.Parking,
            "1L",
            TaxiNodeKind.RunwayExit,
            "1L",
            AircraftCategory.Jet,
            DefaultJetType
        ),
        new TaxiPair(
            "SFO_B5-to-1L_jet",
            "SFO",
            "B5",
            TaxiNodeKind.Parking,
            "1L",
            TaxiNodeKind.RunwayExit,
            "1L",
            AircraftCategory.Jet,
            DefaultJetType
        ),
        new TaxiPair(
            "SFO_F5-to-28R_jet",
            "SFO",
            "F5",
            TaxiNodeKind.Parking,
            "28R",
            TaxiNodeKind.RunwayExit,
            "28R",
            AircraftCategory.Jet,
            DefaultJetType
        ),
        // --- Piston: Parking → runway. SFO has no dedicated GA parking,
        // but a C172 can taxi from any parking node; the sim doesn't enforce
        // parking-type fit. Mixed-category coverage matters more than realism
        // here — different turn rates and taxi speeds exercise different
        // navigator paths.
        new TaxiPair(
            "SFO_A12-to-1L_piston",
            "SFO",
            "A12",
            TaxiNodeKind.Parking,
            "1L",
            TaxiNodeKind.RunwayExit,
            "1L",
            AircraftCategory.Piston,
            DefaultPistonType
        ),
        new TaxiPair(
            "SFO_F5-to-28R_piston",
            "SFO",
            "F5",
            TaxiNodeKind.Parking,
            "28R",
            TaxiNodeKind.RunwayExit,
            "28R",
            AircraftCategory.Piston,
            DefaultPistonType
        ),
        // --- Cross-terminal Parking → parking ---
        new TaxiPair("SFO_A12-to-F5_jet", "SFO", "A12", TaxiNodeKind.Parking, "F5", TaxiNodeKind.Parking, null, AircraftCategory.Jet, DefaultJetType),
        new TaxiPair(
            "SFO_A12-to-F5_piston",
            "SFO",
            "A12",
            TaxiNodeKind.Parking,
            "F5",
            TaxiNodeKind.Parking,
            null,
            AircraftCategory.Piston,
            DefaultPistonType
        ),
        // --- Runway-exit → parking ---
        // 28R is SFO's primary west-flow arrival runway. D and E are
        // 90°/70° exits in the middle of the runway, well-placed for
        // taxi-in to any of the terminals. Mixed jet + piston categories.
        new TaxiPair(
            "SFO_28R-D-to-A12_jet",
            "SFO",
            "28R/D",
            TaxiNodeKind.RunwayExit,
            "A12",
            TaxiNodeKind.Parking,
            null,
            AircraftCategory.Jet,
            DefaultJetType
        ),
        new TaxiPair(
            "SFO_28R-E-to-F5_jet",
            "SFO",
            "28R/E",
            TaxiNodeKind.RunwayExit,
            "F5",
            TaxiNodeKind.Parking,
            null,
            AircraftCategory.Jet,
            DefaultJetType
        ),
        new TaxiPair(
            "SFO_28R-L-to-A12_piston",
            "SFO",
            "28R/L",
            TaxiNodeKind.RunwayExit,
            "A12",
            TaxiNodeKind.Parking,
            null,
            AircraftCategory.Piston,
            DefaultPistonType
        ),
    ];

    /// <summary>
    /// FLL is the third fillet-comparison-gate airport. Two parallel runways:
    /// 10L/28R (long, north — exits A/B/Q/A2 plus high-speed A5/B7/B8) and
    /// 10R/28L (south — J-series exits). Terminal cores B/C/D/E/F sit between
    /// them; A-row parking flanks 10L. Pairs are jets (FLL is commercial) with
    /// one piston for mixed turn dynamics. The D-row → 10L route mirrors the
    /// DAL880 backtrack bundle (<see cref="IssueFllDal880TaxiBacktrackBTests"/>).
    /// </summary>
    public static readonly IReadOnlyList<TaxiPair> FllSmoke =
    [
        // --- Parking → runway (terminal core → long runway, both ends) ---
        new TaxiPair(
            "FLL_D8-to-10L_jet",
            "FLL",
            "D8",
            TaxiNodeKind.Parking,
            "10L",
            TaxiNodeKind.RunwayExit,
            "10L",
            AircraftCategory.Jet,
            DefaultJetType
        ),
        new TaxiPair(
            "FLL_C9-to-28R_jet",
            "FLL",
            "C9",
            TaxiNodeKind.Parking,
            "28R",
            TaxiNodeKind.RunwayExit,
            "28R",
            AircraftCategory.Jet,
            DefaultJetType
        ),
        new TaxiPair(
            "FLL_A9-to-10L_jet",
            "FLL",
            "A9",
            TaxiNodeKind.Parking,
            "10L",
            TaxiNodeKind.RunwayExit,
            "10L",
            AircraftCategory.Jet,
            DefaultJetType
        ),
        // Same D-row → 10L route under piston turn dynamics.
        new TaxiPair(
            "FLL_D8-to-10L_piston",
            "FLL",
            "D8",
            TaxiNodeKind.Parking,
            "10L",
            TaxiNodeKind.RunwayExit,
            "10L",
            AircraftCategory.Piston,
            DefaultPistonType
        ),
        // --- Runway-exit → parking (just landed, taxi to terminal) ---
        // 28R high-speed exit B8 → terminal D; 10R 90° exit J → terminal C.
        new TaxiPair(
            "FLL_28R-B8-to-D8_jet",
            "FLL",
            "28R/B8",
            TaxiNodeKind.RunwayExit,
            "D8",
            TaxiNodeKind.Parking,
            null,
            AircraftCategory.Jet,
            DefaultJetType
        ),
        new TaxiPair(
            "FLL_10R-J-to-C9_jet",
            "FLL",
            "10R/J",
            TaxiNodeKind.RunwayExit,
            "C9",
            TaxiNodeKind.Parking,
            null,
            AircraftCategory.Jet,
            DefaultJetType
        ),
    ];
}
