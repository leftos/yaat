namespace Yaat.Sim;

public sealed class SimulationWorld
{
    private readonly object _lock = new();
    private readonly List<AircraftState> _aircraft = [];

    public void AddAircraft(AircraftState aircraft)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(aircraft.Cid))
            {
                aircraft.Cid = GenerateUniqueCid();
            }

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
        Tick(deltaSeconds, preTick: null);
    }

    public void Tick(double deltaSeconds, Action<AircraftState, double>? preTick)
    {
        lock (_lock)
        {
            AircraftState? Lookup(string callsign)
            {
                foreach (var a in _aircraft)
                {
                    if (string.Equals(
                        a.Callsign, callsign,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return a;
                    }
                }

                return null;
            }

            var speedOverrides = GroundConflictDetector
                .ComputeSpeedOverrides(_aircraft);

            foreach (var ac in _aircraft)
            {
                preTick?.Invoke(ac, deltaSeconds);
                FlightPhysics.Update(ac, deltaSeconds, Lookup);

                if (speedOverrides.TryGetValue(ac.Callsign, out double maxSpeed)
                    && ac.IsOnGround
                    && ac.GroundSpeed > maxSpeed)
                {
                    ac.GroundSpeed = maxSpeed;
                }
            }
        }
    }

    public List<(string Callsign, string Warning)> DrainAllWarnings()
    {
        var result = new List<(string, string)>();
        lock (_lock)
        {
            foreach (var ac in _aircraft)
            {
                if (ac.PendingWarnings.Count > 0)
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

    public int Clear()
    {
        lock (_lock)
        {
            int count = _aircraft.Count;
            _aircraft.Clear();
            return count;
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

    /// <summary>
    /// Must be called under _lock.
    /// </summary>
    private string GenerateUniqueCid()
    {
        var usedCids = new HashSet<string>();
        foreach (var ac in _aircraft)
        {
            if (!string.IsNullOrEmpty(ac.Cid))
            {
                usedCids.Add(ac.Cid);
            }
        }

        var rng = Random.Shared;
        for (int attempt = 0; attempt < 1000; attempt++)
        {
            var cid = rng.Next(100, 1000).ToString();
            if (!usedCids.Contains(cid))
            {
                return cid;
            }
        }

        for (int i = 100; i < 1000; i++)
        {
            var cid = i.ToString();
            if (!usedCids.Contains(cid))
            {
                return cid;
            }
        }

        return "000";
    }
}
