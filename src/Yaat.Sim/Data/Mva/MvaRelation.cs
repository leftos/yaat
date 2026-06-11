namespace Yaat.Sim.Data.Mva;

/// <summary>An aircraft's altitude relative to the controlling MVA floor at its position.</summary>
public enum MvaRelation
{
    /// <summary>No MVA sector covers the position.</summary>
    NoData,

    /// <summary>More than the tolerance band below the floor.</summary>
    Below,

    /// <summary>Within the tolerance band of the floor.</summary>
    At,

    /// <summary>More than the tolerance band above the floor.</summary>
    Above,
}
