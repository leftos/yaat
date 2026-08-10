namespace Yaat.Sim;

/// <summary>
/// The air↔ground frame conversions for <see cref="AircraftState.IndicatedAirspeed"/>.
/// On the ground that field carries groundspeed (the aircraft is wheel-constrained: taxi
/// speeds, rollout coast/exit speeds, and braking rates are all ground-frame quantities,
/// and track = heading is always correct because the gear resists lateral wind). Airborne
/// it carries indicated airspeed. Wind and air density therefore enter the ground frame at
/// exactly the transitions: rotation happens at Vr INDICATED — a headwind shortens the
/// ground roll by the v² law and lowers the surface-radar groundspeed at rotation — and
/// touchdown groundspeed is touchdown TAS minus the headwind component. Every phase that
/// flips <c>IsOnGround</c> with meaningful speed must route through these helpers so the
/// conversion has exactly one implementation.
/// </summary>
public static class GroundFrame
{
    /// <summary>
    /// The indicated airspeed corresponding to a ground-frame speed at the aircraft's
    /// current altitude and cached headwind: TAS = GS + headwind, then density-corrected
    /// to IAS. Used to gate rotation during the takeoff roll.
    /// </summary>
    public static double IasForGroundSpeed(AircraftState aircraft, double groundSpeedKts)
    {
        return WindInterpolator.TasToIas(groundSpeedKts + aircraft.HeadwindKts, aircraft.Altitude);
    }

    /// <summary>
    /// Air → ground at touchdown: converts the airborne IAS to wheel speed
    /// (TAS − headwind, floored at 0) and flips <see cref="AircraftState.IsOnGround"/>.
    /// </summary>
    public static void EnterGround(AircraftState aircraft, double iasKts)
    {
        double tas = WindInterpolator.IasToTas(iasKts, aircraft.Altitude);
        aircraft.IsOnGround = true;
        aircraft.IndicatedAirspeed = Math.Max(0, tas - aircraft.HeadwindKts);
    }

    /// <summary>
    /// Ground → air at liftoff: the field becomes indicated airspeed at the given value
    /// (rotation happens AT Vr indicated, so no conversion is needed — the ground-frame
    /// speed it implies was what the roll built up to).
    /// </summary>
    public static void LeaveGround(AircraftState aircraft, double iasKts)
    {
        aircraft.IsOnGround = false;
        aircraft.IndicatedAirspeed = iasKts;
    }
}
