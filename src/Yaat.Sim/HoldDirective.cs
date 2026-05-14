namespace Yaat.Sim;

public enum HoldKind
{
    HoldPosition,
    GiveWay,
}

/// <summary>
/// Structured ground-hold directive. Replaces the historical pair of
/// <c>IsHeld</c>/<c>GiveWayTarget</c> on <see cref="AircraftGroundOps"/> so the
/// discriminator between an unconditional HOLDPOSITION and a conditional GIVEWAY
/// is explicit and self-validating. Construct via <see cref="HoldPosition"/> or
/// <see cref="GiveWay(string)"/>.
/// </summary>
public sealed record HoldDirective
{
    public HoldKind Kind { get; }
    public string? YieldTarget { get; }

    private HoldDirective(HoldKind kind, string? yieldTarget)
    {
        Kind = kind;
        YieldTarget = yieldTarget;
    }

    public static HoldDirective HoldPosition { get; } = new(HoldKind.HoldPosition, null);

    public static HoldDirective GiveWay(string yieldTarget)
    {
        if (string.IsNullOrWhiteSpace(yieldTarget))
        {
            throw new System.ArgumentException("yieldTarget is required for a GiveWay directive", nameof(yieldTarget));
        }
        return new HoldDirective(HoldKind.GiveWay, yieldTarget);
    }

    public bool IsGiveWayFor(string callsign) =>
        Kind == HoldKind.GiveWay
        && YieldTarget is not null
        && !string.IsNullOrEmpty(callsign)
        && string.Equals(YieldTarget, callsign, System.StringComparison.OrdinalIgnoreCase);
}
