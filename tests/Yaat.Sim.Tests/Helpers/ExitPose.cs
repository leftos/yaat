using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// A pose on a high-speed exit of a real runway: the main gear on the straight line from where the exit leaves the runway to
/// <paramref name="Bar"/>, its centre just off the pavement (<see cref="RunwayOccupancy.IsOnPavement"/>), facing the bar.
/// </summary>
/// <param name="Layout">The airport's ground layout.</param>
/// <param name="Runway">The runway.</param>
/// <param name="Bar">The exit's bar.</param>
/// <param name="Type">The aircraft type.</param>
/// <param name="Position">The main-gear position.</param>
/// <param name="Heading">The heading, toward the bar.</param>
/// <param name="CentreFt">The main gear's distance from the centreline (ft).</param>
/// <param name="TailFt">The tail's distance from the centreline (ft).</param>
/// <param name="TailAlongBarFt">How far along the runway the tail lies from the nearest bar on its side (ft).</param>
public sealed record ExitPose(
    AirportGroundLayout Layout,
    RunwayInfo Runway,
    GroundNode Bar,
    string Type,
    LatLon Position,
    TrueHeading Heading,
    double CentreFt,
    double TailFt,
    double TailAlongBarFt
)
{
    /// <summary>
    /// An aircraft of the pose's type stopped at the pose, past the exit's turn-off (<see cref="ExitPhaseFixture.ExitOffTheCentreline"/>).
    /// </summary>
    public AircraftState Stopped()
    {
        var ac = new AircraftState
        {
            Callsign = "EXIT1",
            AircraftType = Type,
            Position = Position,
            TrueHeading = Heading,
            TrueTrack = Heading,
            Altitude = Runway.ElevationFt,
            IsOnGround = true,
            AirportId = Layout.AirportId,
            Phases = new PhaseList { AssignedRunway = Runway },
        };
        ac.Phases.Add(ExitPhaseFixture.ExitOffTheCentreline(Runway));
        ac.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        return ac;
    }
}
