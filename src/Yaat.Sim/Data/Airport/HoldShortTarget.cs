namespace Yaat.Sim.Data.Airport;

/// <summary>
/// A controller-issued hold-short target. <see cref="Target"/> is the taxiway or runway to hold
/// short of. <see cref="OnTaxiway"/> optionally names the taxiway the aircraft holds ON — the
/// <c>C@J</c> form ("hold short of C, on J"), which disambiguates which crossing of the target
/// binds when a route meets it more than once, and steers the route onto that taxiway toward the
/// crossing. Token order matches the <c>28R@E</c> runway-entry convention: the thing before
/// <c>@</c>, the location after. Null <see cref="OnTaxiway"/> is the bare form (<c>HS C</c>),
/// which binds the first crossing in route-walk order.
/// </summary>
public readonly record struct HoldShortTarget(string Target, string? OnTaxiway)
{
    /// <summary>The command-text form: <c>C</c> or <c>C@J</c>. Round-trips through <see cref="TryParse"/>.</summary>
    public string ToCanonical() => OnTaxiway is null ? Target : $"{Target}@{OnTaxiway}";

    /// <summary>Human-readable prose form for controller-facing messages: <c>C</c> or <c>C at J</c>.</summary>
    public string ToNatural() => OnTaxiway is null ? Target : $"{Target} at {OnTaxiway}";

    public override string ToString() => ToCanonical();

    /// <summary>Throwing form of <see cref="TryParse"/> for callers whose input is already validated (tools, tests).</summary>
    public static HoldShortTarget Parse(string token)
    {
        if (!TryParse(token, out var target, out string? error))
        {
            throw new ArgumentException(error, nameof(token));
        }

        return target;
    }

    /// <summary>
    /// Parses a hold-short token: <c>TARGET</c> or <c>TARGET@TAXIWAY</c>, uppercased. Fails with an
    /// actionable <paramref name="error"/> on an empty half or more than one <c>@</c>.
    /// </summary>
    public static bool TryParse(string token, out HoldShortTarget target, out string? error)
    {
        target = default;
        string trimmed = token.Trim();
        if (trimmed.Length == 0)
        {
            error = "hold-short target is empty";
            return false;
        }

        int at = trimmed.IndexOf('@');
        if (at < 0)
        {
            target = new HoldShortTarget(trimmed.ToUpperInvariant(), null);
            error = null;
            return true;
        }

        string targetPart = trimmed[..at];
        string onPart = trimmed[(at + 1)..];
        if (targetPart.Length == 0 || onPart.Length == 0 || onPart.Contains('@'))
        {
            error = $"malformed hold-short target '{trimmed}' — expected TARGET or TARGET@TAXIWAY (e.g. C@J)";
            return false;
        }

        target = new HoldShortTarget(targetPart.ToUpperInvariant(), onPart.ToUpperInvariant());
        error = null;
        return true;
    }
}
