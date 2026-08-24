namespace Yaat.Sim.Data.Airport;

/// <summary>
/// A controller-issued hold-short target. <see cref="Target"/> is the taxiway, runway, or taxi spot to
/// hold short of. <see cref="IsSpot"/> marks the spot form (<c>$17</c> — the <c>$</c> sigil shared with
/// taxi destinations): the aircraft holds at the named <see cref="GroundNodeType.Spot"/> node on its
/// route. <see cref="OnTaxiway"/> optionally names the taxiway the aircraft holds ON — the
/// <c>C@J</c> form ("hold short of C, on J"), which disambiguates which crossing of the target
/// binds when a route meets it more than once, and steers the route onto that taxiway toward the
/// crossing. Token order matches the <c>28R@E</c> runway-entry convention: the thing before
/// <c>@</c>, the location after. Null <see cref="OnTaxiway"/> is the bare form (<c>HS C</c>),
/// which binds the first crossing in route-walk order. A spot is a single node, so it has no located form.
/// </summary>
public readonly record struct HoldShortTarget(string Target, string? OnTaxiway, bool IsSpot)
{
    /// <summary>The command-text form: <c>C</c>, <c>C@J</c>, or <c>$17</c>. Round-trips through <see cref="TryParse"/>.</summary>
    public string ToCanonical()
    {
        if (IsSpot)
        {
            return $"${Target}";
        }

        return OnTaxiway is null ? Target : $"{Target}@{OnTaxiway}";
    }

    /// <summary>Human-readable prose form for controller-facing messages: <c>C</c>, <c>C at J</c>, or <c>spot 17</c>.</summary>
    public string ToNatural()
    {
        if (IsSpot)
        {
            return $"spot {Target}";
        }

        return OnTaxiway is null ? Target : $"{Target} at {OnTaxiway}";
    }

    /// <summary>
    /// The string a route's <see cref="HoldShortPoint.TargetName"/> carries for this target, and what
    /// name-matching compares against. A spot keeps its <c>$</c> sigil (<c>$17</c>) so it can never be
    /// mistaken for runway or taxiway <c>17</c> anywhere the name is displayed, matched, or restored.
    /// </summary>
    public string MatchKey => IsSpot ? ToCanonical() : Target;

    /// <summary>Whether a <see cref="HoldShortPoint.TargetName"/> denotes a spot hold-short (<c>$</c>-prefixed).</summary>
    public static bool IsSpotTargetName(string? targetName) => targetName is { Length: > 1 } && targetName[0] == '$';

    /// <summary>The spot name inside a spot <see cref="HoldShortPoint.TargetName"/> (<c>$17</c> → <c>17</c>).</summary>
    public static string SpotNameOf(string targetName) => targetName[1..];

    /// <summary>
    /// Controller-facing display form of a <see cref="HoldShortPoint.TargetName"/>: <c>spot 17</c> for the
    /// <c>$17</c> spot form, otherwise the de-padded runway/taxiway designator (<c>1L</c>, <c>C</c>).
    /// </summary>
    public static string Describe(string targetName) =>
        IsSpotTargetName(targetName) ? $"spot {SpotNameOf(targetName)}" : RunwayIdentifier.ToDisplayDesignator(targetName);

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
    /// Parses a hold-short token: <c>TARGET</c>, <c>TARGET@TAXIWAY</c>, or <c>$SPOT</c>, uppercased. Fails
    /// with an actionable <paramref name="error"/> on an empty half, more than one <c>@</c>, a located
    /// spot, or a parking token (<c>@A12</c> is a taxi destination, never a hold-short target).
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

        if (trimmed[0] == '$')
        {
            string spot = trimmed[1..];
            if (spot.Length == 0)
            {
                error = "hold-short spot is empty — expected $SPOT (e.g. $17)";
                return false;
            }

            if (spot.Contains('@'))
            {
                error = $"malformed hold-short target '{trimmed}' — a spot is a single point and takes no @TAXIWAY location";
                return false;
            }

            target = new HoldShortTarget(spot.ToUpperInvariant(), null, true);
            error = null;
            return true;
        }

        if (trimmed[0] == '@' && trimmed.Length > 1 && !trimmed[1..].Contains('@'))
        {
            error = $"parking '{trimmed}' cannot be a hold-short target — put it before HS as the taxi destination";
            return false;
        }

        int at = trimmed.IndexOf('@');
        if (at < 0)
        {
            target = new HoldShortTarget(trimmed.ToUpperInvariant(), null, false);
            error = null;
            return true;
        }

        string targetPart = trimmed[..at];
        string onPart = trimmed[(at + 1)..];
        if (targetPart.Length == 0 || onPart.Length == 0 || onPart.Contains('@'))
        {
            error = $"malformed hold-short target '{trimmed}' — expected TARGET, TARGET@TAXIWAY (e.g. C@J), or $SPOT";
            return false;
        }

        target = new HoldShortTarget(targetPart.ToUpperInvariant(), onPart.ToUpperInvariant(), false);
        error = null;
        return true;
    }
}
