namespace Yaat.Sim;

/// <summary>
/// Which initial call a ground-spawned aircraft makes in solo training, decided once at scenario load from where it
/// spawned and its timed presets (<see cref="Scenarios.InitialCallupClassifier"/>). Every aircraft the loader did not
/// arm — an arrival that taxied to its stand, a warp, a generated aircraft — keeps <see cref="None"/>.
/// </summary>
public enum InitialCallupPlan
{
    /// <summary>No initial call.</summary>
    None = 0,

    /// <summary>A ground spawn with no TAXI or push preset: "ready to taxi" from where it sits, after the stand delay and a pacing slot.</summary>
    StandCall = 1,

    /// <summary>A ground spawn whose only ground preset is a push: it calls after the push completes.</summary>
    AfterPush = 2,

    /// <summary>A ground spawn whose timed TAXI ends at a spot, a taxiway hold short, or nowhere named: it calls once it gets there.</summary>
    AfterTaxiArrival = 3,

    /// <summary>A runway spawn with a SAY preset and no takeoff preset: the SAY is what it says; it makes no lined-up call.</summary>
    RunwaySayOnly = 4,

    /// <summary>
    /// A runway spawn with neither a takeoff nor a SAY preset: at the lined-up call point a radar student gets an automatic
    /// takeoff (towered field) or a release request (untowered field); any other student gets the lined-up call.
    /// </summary>
    RunwayNoPreset = 5,
}
