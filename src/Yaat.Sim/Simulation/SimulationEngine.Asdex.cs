using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Actions;

namespace Yaat.Sim.Simulation;

// The ASDE-X and SAAB SAID half of the engine: the CRC-sourced mutations a controller's surface display sends, and the
// room-wide alert-inhibit sweep ASDXALERTS is. Everything they write is per-aircraft AircraftStarsState — snapshotted,
// so a replay, a rewind and a session restore all carry it — through the same TrackEngine bodies the typed ASDX verbs
// use. The one thing that leaves the simulation is a terminate: the live room turns the consumer call into its
// one-shot delete marker, which the broadcaster drains into DeleteAsdexTracks / DeleteSaabSaidTracks.
public sealed partial class SimulationEngine
{
    /// <summary>
    /// Applies one recorded CRC ASDE-X mutation. <c>EditDbFields</c> writes every display-field override the record
    /// carries (a null field is one the record does not touch); the per-aircraft verbs move the tag / terminate /
    /// suspend / alert-inhibit bits; <c>EnableAllAlerts</c> is the room-wide sweep. A mutation naming an aircraft the
    /// world no longer holds applies nothing, silently — the display it came from is one frame behind the world.
    /// <paramref name="host"/> is told about a terminate, and about nothing else.
    /// </summary>
    public void ApplyAsdexMutation(RecordedAsdexMutation mutation, IActionHost host)
    {
        switch (mutation.Kind)
        {
            case "EditDbFields":
                OnAsdexAircraft(mutation, ac => ApplyAsdexEdit(ac, mutation));
                break;
            case "Tag":
                OnAsdexAircraft(mutation, ac => TrackEngine.HandleAsdexVerb(ac, AsdexVerb.Tag));
                break;
            case "Terminate":
                OnAsdexAircraft(
                    mutation,
                    ac =>
                    {
                        TrackEngine.HandleAsdexVerb(ac, AsdexVerb.Terminate);
                        host.OnAsdexTrackTerminated(ac.Callsign);
                    }
                );
                break;
            case "Suspend":
                OnAsdexAircraft(mutation, ac => TrackEngine.HandleAsdexVerb(ac, AsdexVerb.Suspend));
                break;
            case "Unsuspend":
                OnAsdexAircraft(mutation, ac => TrackEngine.HandleAsdexVerb(ac, AsdexVerb.Unsuspend));
                break;
            case "InhibitAlerts":
                OnAsdexAircraft(mutation, ac => TrackEngine.HandleAsdexVerb(ac, AsdexVerb.InhibitAlerts));
                break;
            case "EnableAllAlerts":
                EnableAllAsdexAlerts();
                break;
        }
    }

    /// <summary>
    /// Applies one recorded CRC SAAB SAID mutation — the same shape as the ASDE-X one on the <c>Said*</c> fields,
    /// with no alerts to inhibit or enable. <paramref name="host"/> is told about a terminate.
    /// </summary>
    public void ApplySaidMutation(RecordedSaidMutation mutation, IActionHost host)
    {
        switch (mutation.Kind)
        {
            case "EditDbFields":
                OnSaidAircraft(mutation, ac => ApplySaidEdit(ac, mutation));
                break;
            case "Tag":
                OnSaidAircraft(mutation, ac => TrackEngine.HandleSaidVerb(ac, SaidVerb.Tag));
                break;
            case "Terminate":
                OnSaidAircraft(
                    mutation,
                    ac =>
                    {
                        TrackEngine.HandleSaidVerb(ac, SaidVerb.Terminate);
                        host.OnSaidTrackTerminated(ac.Callsign);
                    }
                );
                break;
            case "Suspend":
                OnSaidAircraft(mutation, ac => TrackEngine.HandleSaidVerb(ac, SaidVerb.Suspend));
                break;
            case "Unsuspend":
                OnSaidAircraft(mutation, ac => TrackEngine.HandleSaidVerb(ac, SaidVerb.Unsuspend));
                break;
        }
    }

    /// <summary>
    /// <c>ASDXALERTS</c> and a recorded <c>EnableAllAlerts</c>: clears every aircraft's ASDE-X alert inhibit. A pure
    /// per-aircraft sweep — there is no room-wide "alerts enabled" bit to hold.
    /// </summary>
    public CommandResult EnableAllAsdexAlerts()
    {
        foreach (var ac in World.GetSnapshot())
        {
            ac.Stars.AsdexAlertsInhibited = false;
        }

        return new CommandResult(true, "ASDX alerts enabled");
    }

    /// <summary>
    /// Resolves the mutation's aircraft by callsign alone. A surface mutation names a track, and a surface track id is
    /// the callsign: <c>AsdexTrackDto.Id</c> is <c>"CALLSIGN{callsign}"</c>, which the CRC handler strips before it
    /// builds the record (<c>CrcClientState.Asdex.cs</c>, <c>ReadTrackIdArg</c> / <c>ReadAircraftIdArg</c>), and an
    /// edit prefers the DTO's bare callsign field. Nothing on that wire carries a CID or a beacon code, so the
    /// room's FLID-aware resolver has nothing to add here.
    /// </summary>
    private void OnAsdexAircraft(RecordedAsdexMutation mutation, Action<AircraftState> apply)
    {
        if ((mutation.AircraftId is { Length: > 0 } id) && (FindAircraft(id) is { } aircraft))
        {
            apply(aircraft);
        }
    }

    private void OnSaidAircraft(RecordedSaidMutation mutation, Action<AircraftState> apply)
    {
        if ((mutation.AircraftId is { Length: > 0 } id) && (FindAircraft(id) is { } aircraft))
        {
            apply(aircraft);
        }
    }

    private static void ApplyAsdexEdit(AircraftState ac, RecordedAsdexMutation mutation)
    {
        Write(ac, AsdexEditField.Callsign, mutation.Callsign);
        Write(ac, AsdexEditField.BeaconCode, mutation.BeaconCode);
        Write(ac, AsdexEditField.Category, mutation.Category);
        Write(ac, AsdexEditField.AircraftType, mutation.AircraftType);
        Write(ac, AsdexEditField.Fix, mutation.Fix);
        Write(ac, AsdexEditField.Scratchpad1, mutation.Scratchpad1);
        Write(ac, AsdexEditField.Scratchpad2, mutation.Scratchpad2);
    }

    private static void ApplySaidEdit(AircraftState ac, RecordedSaidMutation mutation)
    {
        Write(ac, SaidEditField.Callsign, mutation.Callsign);
        Write(ac, SaidEditField.BeaconCode, mutation.BeaconCode);
        Write(ac, SaidEditField.Category, mutation.Category);
        Write(ac, SaidEditField.AircraftType, mutation.AircraftType);
        Write(ac, SaidEditField.Fix, mutation.Fix);
        Write(ac, SaidEditField.Scratchpad1, mutation.Scratchpad1);
        Write(ac, SaidEditField.Scratchpad2, mutation.Scratchpad2);
    }

    /// <summary>A field the record does not carry is left alone; one it carries is written as it stands, empty included.</summary>
    private static void Write(AircraftState ac, AsdexEditField field, string? value)
    {
        if (value is not null)
        {
            TrackEngine.SetAsdexField(ac, field, value);
        }
    }

    private static void Write(AircraftState ac, SaidEditField field, string? value)
    {
        if (value is not null)
        {
            TrackEngine.SetSaidField(ac, field, value);
        }
    }
}
