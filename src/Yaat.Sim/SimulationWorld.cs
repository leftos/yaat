namespace Yaat.Sim;

public sealed class SimulationWorld
{
    private readonly object _lock = new();
    private readonly List<AircraftState> _aircraft = [];

    public void AddAircraft(AircraftState aircraft)
    {
        lock (_lock)
        {
            _aircraft.Add(aircraft);
        }
    }

    public void RemoveAircraft(string callsign)
    {
        lock (_lock)
        {
            _aircraft.RemoveAll(
                a => a.Callsign == callsign);
        }
    }

    public List<AircraftState> GetSnapshot()
    {
        lock (_lock)
        {
            return new List<AircraftState>(_aircraft);
        }
    }

    public void Tick(double deltaSeconds)
    {
        lock (_lock)
        {
            foreach (var ac in _aircraft)
            {
                FlightPhysics.Update(ac, deltaSeconds);
            }
        }
    }

    public static uint GenerateBeaconCode()
    {
        var rng = Random.Shared;
        uint code = 0;
        for (int i = 0; i < 4; i++)
        {
            code = (code * 10) + (uint)rng.Next(0, 8);
        }
        return code;
    }
}
