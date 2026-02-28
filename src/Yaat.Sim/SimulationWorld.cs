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
            _aircraft.RemoveAll(a => a.Callsign == callsign);
        }
    }

    public List<AircraftState> GetSnapshot()
    {
        lock (_lock)
        {
            return [.. _aircraft];
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

    public List<AircraftState> GetSnapshotByScenario(string scenarioId)
    {
        lock (_lock)
        {
            return [.. _aircraft.Where(a => a.ScenarioId == scenarioId)];
        }
    }

    public int RemoveByScenario(string scenarioId)
    {
        lock (_lock)
        {
            return _aircraft.RemoveAll(a => a.ScenarioId == scenarioId);
        }
    }

    public void TickScenario(string scenarioId, double deltaSeconds)
    {
        TickScenario(scenarioId, deltaSeconds, preTick: null);
    }

    public void TickScenario(
        string scenarioId,
        double deltaSeconds,
        Action<AircraftState, double>? preTick)
    {
        lock (_lock)
        {
            foreach (var ac in _aircraft)
            {
                if (ac.ScenarioId == scenarioId)
                {
                    preTick?.Invoke(ac, deltaSeconds);
                    FlightPhysics.Update(ac, deltaSeconds);
                }
            }
        }
    }

    public List<(string Callsign, string Warning)> DrainWarnings(string scenarioId)
    {
        var result = new List<(string, string)>();
        lock (_lock)
        {
            foreach (var ac in _aircraft)
            {
                if (ac.ScenarioId == scenarioId && ac.PendingWarnings.Count > 0)
                {
                    foreach (var w in ac.PendingWarnings)
                    {
                        result.Add((ac.Callsign, w));
                    }
                    ac.PendingWarnings.Clear();
                }
            }
        }
        return result;
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
