namespace Yaat.Sim.ControllerAi;

/// <summary>
/// The position family an AI controller brain plays. Clearance delivery is folded into <see cref="Ground"/>; radar
/// roles are split by facility type. Ordered so brains tick upstream-first (Ground before Local before Approach before
/// Center) — a crossing Local approves this tick is visible to Ground next tick, the one-tick coordination latency the
/// design accepts as realistic.
/// </summary>
public enum ControlRole
{
    Ground = 0,
    Local = 1,
    Approach = 2,
    Center = 3,
}

public static class ControlRoles
{
    /// <summary>Deterministic tick order: upstream positions act first.</summary>
    public static int Rank(ControlRole role) => (int)role;

    /// <summary>The <see cref="AtcPositionTypeClassifier"/> code the role answers pilots as (GND / TWR / APP / CTR).</summary>
    public static string PositionType(ControlRole role) =>
        role switch
        {
            ControlRole.Ground => "GND",
            ControlRole.Local => "TWR",
            ControlRole.Approach => "APP",
            ControlRole.Center => "CTR",
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown control role"),
        };

    /// <summary>Parses the aliases the soak runner and room commands accept: GC/GND, LC/TWR, APP/DEP, CTR.</summary>
    public static bool TryParseAlias(string? alias, out ControlRole role)
    {
        switch (alias?.Trim().ToUpperInvariant())
        {
            case "GC":
            case "GND":
            case "GROUND":
                role = ControlRole.Ground;
                return true;
            case "LC":
            case "TWR":
            case "LOCAL":
            case "TOWER":
                role = ControlRole.Local;
                return true;
            case "APP":
            case "DEP":
            case "APPROACH":
                role = ControlRole.Approach;
                return true;
            case "CTR":
            case "CENTER":
                role = ControlRole.Center;
                return true;
            default:
                role = default;
                return false;
        }
    }
}
