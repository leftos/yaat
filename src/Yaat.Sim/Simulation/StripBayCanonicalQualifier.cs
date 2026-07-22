using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Simulation;

/// <summary>
/// Rewrites a recorded strip canonical whose bay token predates the
/// facility-qualified form (<c>STRIP Local 1/2/1</c>) into the current one
/// (<c>STRIP OAK/Local 1/2/1</c>). Used by <see cref="RecordingSchemaUpgrader"/>
/// to migrate archived action logs in place — bay names are only unique within a
/// facility, so the canonical grammar now requires the owner.
///
/// <para>Idempotent: a canonical that already names a facility is returned
/// unchanged, so the upgrade can be re-run safely and needs no schema-version
/// gate. A bay token that resolves to no accessible bay is also left alone —
/// the caller surfaces those rather than guessing.</para>
/// </summary>
public static class StripBayCanonicalQualifier
{
    private static readonly string[] IdPrefixes = ["STRIP_", "HSTRIP_", "SEP_", "BLANK_", "ARRIVAL_"];

    /// <summary>
    /// Qualifies every unit of a possibly-compound canonical. Splitting on
    /// <c>;</c> / <c>,</c> is safe because the compound parser owns those
    /// separators — no strip payload can contain one.
    /// </summary>
    public static string QualifyCompound(string canonical, IReadOnlyList<AccessibleBay> bays)
    {
        if (canonical.IndexOfAny([';', ',']) < 0)
        {
            return Qualify(canonical, bays);
        }

        var result = new System.Text.StringBuilder(canonical.Length + 16);
        var unitStart = 0;
        for (var i = 0; i <= canonical.Length; i++)
        {
            if (i < canonical.Length && canonical[i] is not (';' or ','))
            {
                continue;
            }
            var unit = canonical[unitStart..i];
            result.Append(QualifyPreservingPadding(unit, bays));
            if (i < canonical.Length)
            {
                result.Append(canonical[i]);
            }
            unitStart = i + 1;
        }
        return result.ToString();
    }

    private static string QualifyPreservingPadding(string unit, IReadOnlyList<AccessibleBay> bays)
    {
        var trimmed = unit.Trim();
        if (trimmed.Length == 0)
        {
            return unit;
        }
        var lead = unit[..unit.IndexOf(trimmed[0])];
        var trail = unit[(lead.Length + trimmed.Length)..];
        return lead + Qualify(trimmed, bays) + trail;
    }

    /// <summary>
    /// Qualifies the bay token in <paramref name="canonical"/> against
    /// <paramref name="bays"/>. Returns the input unchanged when the verb takes
    /// no bay, the bay is already qualified, or no bay name matches.
    /// </summary>
    public static string Qualify(string canonical, IReadOnlyList<AccessibleBay> bays)
    {
        if (bays.Count == 0 || string.IsNullOrWhiteSpace(canonical))
        {
            return canonical;
        }

        var tokens = canonical.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            return canonical;
        }

        if (ResolveDestStart(tokens) is not int start || start >= tokens.Length)
        {
            return canonical;
        }

        if (IsAlreadyQualified(tokens, start, bays))
        {
            return canonical;
        }

        if (!TryResolveOwner(tokens, start, bays, out var facilityId))
        {
            return canonical;
        }

        tokens[start] = $"{facilityId}/{tokens[start]}";
        return string.Join(' ', tokens);
    }

    /// <summary>
    /// Index of the token where the bay reference begins, or null when this verb
    /// carries no bay (a strip-id form, an annotation, a bare aircraft-scoped
    /// verb). Mirrors the argument layout each verb's parser expects.
    /// </summary>
    private static int? ResolveDestStart(string[] tokens)
    {
        var verb = tokens[0].ToUpperInvariant();
        var headIsId = IsStripId(tokens[1]);
        return verb switch
        {
            "STRIP" => headIsId ? 2 : 1,
            "SCAN" or "HSC" or "BLANK" => 1,
            // SEPM's first argument is always the separator's strip id; SEP's is the style char.
            "SEPM" or "SEP" or "SEPARATOR" => 2,
            // Id form addresses the strip directly and carries no bay.
            "SEPE" or "SEPD" or "SEPARATORDEL" or "BLANKD" => headIsId ? null : 1,
            // The UI always emits HSM by strip id; the terminal's optional source-bay
            // form is not produced by any recorded session.
            "HSM" => headIsId ? 2 : null,
            // Bay scope is optional on these: present only when a further token follows
            // and the head is neither a strip id nor a backslash-joined payload.
            "HSA" or "HALFSTRIPAMEND" or "HSD" or "HALFSTRIPDEL" or "HSO" or "HSS" => (
                !headIsId && tokens.Length > 2 && !tokens[1].Contains('\\', StringComparison.Ordinal)
            )
                ? 1
                : null,
            "HALFSTRIPCREATE" => 1,
            _ => null,
        };
    }

    private static bool IsStripId(string token)
    {
        foreach (var prefix in IdPrefixes)
        {
            if (token.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True when the token run already reads as <c>FACILITY/BAY…</c>: the leading
    /// segment names an accessible facility and the remainder resolves to one of
    /// its bays. Needed because a facility id can equal a bay name (NCT owns a bay
    /// called NCT), so "does the first segment match a bay?" is not enough.
    /// </summary>
    private static bool IsAlreadyQualified(string[] tokens, int start, IReadOnlyList<AccessibleBay> bays)
    {
        var slash = tokens[start].IndexOf('/');
        if (slash <= 0)
        {
            return false;
        }

        var candidateFacility = tokens[start][..slash];
        if (!bays.Any(b => b.Owner.Id.Equals(candidateFacility, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var remainder = tokens[start][(slash + 1)..];
        var inner = remainder.Length == 0 ? tokens.Skip(start + 1).ToArray() : [remainder, .. tokens.Skip(start + 1)];
        var scoped = bays.Where(b => b.Owner.Id.Equals(candidateFacility, StringComparison.OrdinalIgnoreCase)).ToList();
        return TryMatchBayName(inner, scoped, out _);
    }

    /// <summary>
    /// Resolves the owning facility of the bay named by the token run starting at
    /// <paramref name="start"/>. Own-facility bays win over linked external ones
    /// when a name appears in both — a recorded command could only ever have meant
    /// the bay the position owns.
    /// </summary>
    private static bool TryResolveOwner(string[] tokens, int start, IReadOnlyList<AccessibleBay> bays, out string facilityId)
    {
        var ordered = bays.OrderBy(b => b.IsExternal ? 1 : 0).ToList();
        if (TryMatchBayName(tokens.Skip(start).ToArray(), ordered, out var match))
        {
            facilityId = match!.Owner.Id;
            return true;
        }

        facilityId = "";
        return false;
    }

    /// <summary>
    /// Matches the leading bay name in <paramref name="tokens"/> against
    /// <paramref name="bays"/>. The name runs up to the first token containing a
    /// <c>/</c> (whose text before the slash is its last word) — the same span rule
    /// the server's dest-spec resolver uses. With no slash anywhere, progressively
    /// shorter joins are tried so a trailing label or index token is not swallowed.
    /// </summary>
    private static bool TryMatchBayName(string[] tokens, IReadOnlyList<AccessibleBay> bays, out AccessibleBay? match)
    {
        match = null;
        if (tokens.Length == 0)
        {
            return false;
        }

        var slashTokIdx = -1;
        for (var i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Contains('/', StringComparison.Ordinal))
            {
                slashTokIdx = i;
                break;
            }
        }

        if (slashTokIdx >= 0)
        {
            var words = tokens.Take(slashTokIdx).ToList();
            var head = tokens[slashTokIdx][..tokens[slashTokIdx].IndexOf('/')];
            if (head.Length > 0)
            {
                words.Add(head);
            }
            return TryMatchExact(string.Join(' ', words), bays, out match);
        }

        for (var take = tokens.Length; take >= 1; take--)
        {
            if (TryMatchExact(string.Join(' ', tokens.Take(take)), bays, out match))
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryMatchExact(string bayName, IReadOnlyList<AccessibleBay> bays, out AccessibleBay? match)
    {
        var normalized = Normalize(bayName);
        if (normalized.Length == 0)
        {
            match = null;
            return false;
        }

        foreach (var bay in bays)
        {
            if (Normalize(bay.Bay.Name) == normalized)
            {
                match = bay;
                return true;
            }
        }

        match = null;
        return false;
    }

    private static string Normalize(string s) => s.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
}
