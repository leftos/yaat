namespace Yaat.Sim.Data.Vnas;

public sealed class BeaconCodePool
{
    private readonly HashSet<uint> _assigned = [];

    // Fallback state used when no banks are configured.
    private uint _nextCandidate = 0001;

    // Per-bank cursor, keyed by bank index.
    private readonly Dictionary<int, uint> _bankCursors = [];

    // Configured banks, grouped by type. Empty means fall back to sequential 0001-7777.
    private List<BeaconCodeBankConfig> _ifrBanks = [];
    private List<BeaconCodeBankConfig> _vfrBanks = [];
    private List<BeaconCodeBankConfig> _anyBanks = [];
    private bool _hasBanks;

    public BeaconCodePool() { }

    public BeaconCodePool(List<BeaconCodeBankConfig> banks)
    {
        ConfigureBanks(banks);
    }

    /// <summary>
    /// Whether a beacon code may be auto-assigned to an individual aircraft. This is the single source of
    /// truth for reserved-code exclusion across every code-assigning path (bank draw and sequential
    /// fallback). Codes are stored as decimal digits representing octal, so <c>1200u</c> means squawk 1200.
    ///
    /// "Ends in 00" alone is not the definition of reserved: it captures the non-discrete codes, but the
    /// reserved *discrete* special-purpose codes slip through it. Assigning one of those to ordinary
    /// traffic raises a false indicator on a controller's scope, so each family is excluded explicitly:
    ///
    /// <list type="bullet">
    /// <item>Non-discrete codes (last two octal digits zero): VFR conspicuity 1200, the SPCs 7500/7600/7700,
    /// UAS lost-link 7400, military 4000, 0000, and every block code. FAA 7110.65 §5-2-3 … §5-2-7.</item>
    /// <item>The whole 7500-7777 octal block. Every discrete code in the 7600 (radio failure) and 7700
    /// (emergency) series triggers the RF / EMRG indicator in STARS and ERAM — not just the two members
    /// ending in 00 — and 7777 is the military interceptor code. AIM §4-1-20 "Code Changes"; PCG.</item>
    /// <item>The VFR conspicuity codes ATC monitors besides 1200: 1202 (gliders), 1203 (formation lead),
    /// 1255 (firefighting), 1276 and 1277 (SAR). FAA 7110.65 §5-2-11.</item>
    /// <item>The DoD-allocated 5000-5062 block. NBCAP (FAA Order JO 7110.66).</item>
    /// </list>
    /// </summary>
    public static bool IsAssignableCode(uint code)
    {
        if (code.ToString("D4").EndsWith("00", StringComparison.Ordinal))
        {
            return false;
        }

        if (code is >= 7500 and <= 7777)
        {
            return false;
        }

        if (code is >= 5000 and <= 5062)
        {
            return false;
        }

        return code is not (1202 or 1203 or 1255 or 1276 or 1277);
    }

    /// <summary>
    /// Replaces the bank configuration. Clears per-bank cursors; does not release already-assigned codes.
    /// </summary>
    public void ConfigureBanks(List<BeaconCodeBankConfig> banks)
    {
        _ifrBanks = banks.Where(b => b.Type.Equals("Ifr", StringComparison.OrdinalIgnoreCase)).ToList();
        _vfrBanks = banks.Where(b => b.Type.Equals("Vfr", StringComparison.OrdinalIgnoreCase)).ToList();
        _anyBanks = banks.Where(b => b.Type.Equals("Any", StringComparison.OrdinalIgnoreCase)).ToList();
        _hasBanks = banks.Count > 0;
        _bankCursors.Clear();
    }

    /// <summary>
    /// Assigns the next available beacon code.
    /// When banks are configured, IFR traffic draws from "Ifr" banks then "Any" banks;
    /// VFR traffic draws from "Vfr" banks then "Any" banks.
    ///
    /// Every miss falls through to sequential 0001–7777 octal iteration: a facility config may define banks
    /// for one flight-rules type and not the other, and a bank can be exhausted. Without the fallback those
    /// cases yield 0, which callers would write straight onto a transponder as the illegal all-zeros squawk.
    /// Returns 0 only when all 4096 codes are in use.
    /// </summary>
    public uint AssignNextCode(bool isVfr)
    {
        if (!_hasBanks)
        {
            return AssignSequential();
        }

        var primary = isVfr ? _vfrBanks : _ifrBanks;
        return AssignFromBanks(primary) ?? AssignFromBanks(_anyBanks) ?? AssignSequential();
    }

    /// <summary>
    /// Resets all assignment state (assigned codes, cursors) without clearing bank configuration.
    /// Used during snapshot restore to rebuild assignments from the snapshot.
    /// </summary>
    public void Clear()
    {
        _assigned.Clear();
        _nextCandidate = 0001;
        _bankCursors.Clear();
    }

    public void MarkUsed(uint code)
    {
        _assigned.Add(code);
    }

    public void Release(uint code)
    {
        _assigned.Remove(code);
    }

    /// <summary>Sequential-fallback cursor, for snapshot serialization.</summary>
    public uint NextCandidate => _nextCandidate;

    /// <summary>Per-bank draw cursors keyed by the deterministic bank key, for snapshot serialization.</summary>
    public IReadOnlyDictionary<int, uint> BankCursors => _bankCursors;

    /// <summary>
    /// Restores the draw cursors captured in a snapshot so post-restore assignments continue from the
    /// same point as the live run. Assigned codes are restored separately via <see cref="MarkUsed"/>.
    /// </summary>
    public void RestoreCursors(uint nextCandidate, IReadOnlyDictionary<int, uint>? bankCursors)
    {
        _nextCandidate = nextCandidate == 0 ? 0001 : nextCandidate;
        _bankCursors.Clear();
        if (bankCursors is not null)
        {
            foreach (var (key, cursor) in bankCursors)
            {
                _bankCursors[key] = cursor;
            }
        }
    }

    // --- Private ---

    private uint AssignSequential()
    {
        for (uint attempt = 0; attempt < 4096; attempt++)
        {
            var code = _nextCandidate;
            _nextCandidate = NextOctalCode(_nextCandidate);

            if (!IsAssignableCode(code) || _assigned.Contains(code))
            {
                continue;
            }

            _assigned.Add(code);
            return code;
        }

        return 0;
    }

    private uint? AssignFromBanks(List<BeaconCodeBankConfig> banks)
    {
        for (var i = 0; i < banks.Count; i++)
        {
            var bank = banks[i];
            var start = (uint)bank.Start;
            var end = (uint)bank.End;

            if (!_bankCursors.TryGetValue(GetBankKey(bank), out var cursor))
            {
                cursor = start;
            }

            // Try every code in the bank before giving up.
            var bankSize = CountOctalRange(start, end);
            for (var attempt = 0; attempt < bankSize; attempt++)
            {
                var code = cursor;
                cursor = NextOctalCodeInRange(cursor, start, end);

                if (!IsAssignableCode(code) || _assigned.Contains(code))
                {
                    continue;
                }

                _bankCursors[GetBankKey(bank)] = cursor;
                _assigned.Add(code);
                return code;
            }

            _bankCursors[GetBankKey(bank)] = start;
        }

        return null;
    }

    private static int GetBankKey(BeaconCodeBankConfig bank)
    {
        // Deterministic, collision-free key for distinct ranges (octal codes ≤ 7777). Stable across
        // processes — unlike HashCode.Combine — so per-bank cursors round-trip through snapshots.
        return (bank.Start * 10000) + bank.End;
    }

    private static int CountOctalRange(uint start, uint end)
    {
        // Count how many valid octal codes exist from start to end inclusive.
        var count = 0;
        var code = start;
        while (true)
        {
            count++;
            if (code == end)
            {
                break;
            }

            code = NextOctalCode(code);
            if (code == start)
            {
                break;
            }
        }

        return count;
    }

    private static uint NextOctalCodeInRange(uint code, uint start, uint end)
    {
        var next = NextOctalCode(code);
        if (next > end || next < start)
        {
            return start;
        }

        return next;
    }

    private static uint NextOctalCode(uint code)
    {
        // Increment as if octal: each digit 0-7, 4 digits (0001-7777)
        var d0 = code % 10;
        var d1 = (code / 10) % 10;
        var d2 = (code / 100) % 10;
        var d3 = (code / 1000) % 10;

        d0++;
        if (d0 > 7)
        {
            d0 = 0;
            d1++;
        }

        if (d1 > 7)
        {
            d1 = 0;
            d2++;
        }

        if (d2 > 7)
        {
            d2 = 0;
            d3++;
        }

        if (d3 > 7)
        {
            // Wrap around
            return 0001;
        }

        return d3 * 1000 + d2 * 100 + d1 * 10 + d0;
    }
}
