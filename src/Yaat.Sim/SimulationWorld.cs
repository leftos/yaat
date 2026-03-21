using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.Sim;

public sealed class SimulationWorld
{
    private readonly object _lock = new();
    private readonly List<AircraftState> _aircraft = [];

    public SerializableRandom Rng { get; set; } = new SerializableRandom(0);
    public WeatherProfile? Weather { get; set; }
    public AirportGroundLayout? GroundLayout { get; set; }

    /// <summary>
    /// Student TCP. Handoff accepts to this position do not trigger ONHO conditions.
    /// Set from the scenario's <c>studentPositionId</c> → resolved <see cref="Tcp"/>.
    /// </summary>
    public Tcp? StudentTcp { get; set; }

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
                    if (string.Equals(a.Callsign, callsign, StringComparison.OrdinalIgnoreCase))
                    {
                        return a;
                    }
                }

                return null;
            }

            GroundConflictDetector.ApplySpeedLimits(_aircraft, GroundLayout, deltaSeconds);

            var weather = Weather;
            var studentTcp = StudentTcp;
            foreach (var ac in _aircraft)
            {
                // Student TCP accepts don't trigger ONHO conditions
                if (ac.HandoffAccepted && studentTcp is not null && ac.Owner?.IsTcp(studentTcp) == true)
                {
                    ac.HandoffAccepted = false;
                }

                preTick?.Invoke(ac, deltaSeconds);
                FlightPhysics.Update(ac, deltaSeconds, Lookup, weather);
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

    public List<(string Callsign, string Notification)> DrainAllNotifications()
    {
        var result = new List<(string, string)>();
        lock (_lock)
        {
            foreach (var ac in _aircraft)
            {
                if (ac.PendingNotifications.Count > 0)
                {
                    foreach (var n in ac.PendingNotifications)
                    {
                        result.Add((ac.Callsign, n));
                    }

                    ac.PendingNotifications.Clear();
                }
            }
        }

        return result;
    }

    public List<ApproachScore> DrainAllApproachScores()
    {
        var result = new List<ApproachScore>();
        lock (_lock)
        {
            foreach (var ac in _aircraft)
            {
                if (ac.PendingApproachScores.Count > 0)
                {
                    result.AddRange(ac.PendingApproachScores);
                    ac.PendingApproachScores.Clear();
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
            GroundLayout = null;
            return count;
        }
    }

    public uint GenerateBeaconCode() => GenerateBeaconCode(Rng);

    public static uint GenerateBeaconCode(Random rng)
    {
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

        var rng = Rng;
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
