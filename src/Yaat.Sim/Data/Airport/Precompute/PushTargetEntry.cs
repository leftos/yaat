namespace Yaat.Sim.Data.Airport.Precompute;

/// <summary>One push target precomputed for an airport: the stand it pushes from and the design group it serves.</summary>
public sealed record PushTargetEntry(string StandName, string DesignGroup);
