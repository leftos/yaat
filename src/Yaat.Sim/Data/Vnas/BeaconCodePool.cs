namespace Yaat.Sim.Data.Vnas;

public sealed class BeaconCodePool
{
    private static readonly HashSet<uint> ReservedCodes = [7500, 7600, 7700, 7400];
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
    /// When no banks are configured, falls back to sequential 0001–7777 octal iteration.
    /// Returns 0 if the pool is exhausted.
    /// </summary>
    public uint AssignNextCode(bool isVfr)
    {
        if (!_hasBanks)
        {
            return AssignSequential();
        }

        var primary = isVfr ? _vfrBanks : _ifrBanks;
        return AssignFromBanks(primary) ?? AssignFromBanks(_anyBanks) ?? 0;
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

    // --- Private ---

    private uint AssignSequential()
    {
        for (uint attempt = 0; attempt < 4096; attempt++)
        {
            var code = _nextCandidate;
            _nextCandidate = NextOctalCode(_nextCandidate);

            if (ReservedCodes.Contains(code) || _assigned.Contains(code))
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

                if (ReservedCodes.Contains(code) || _assigned.Contains(code))
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
        // Stable key: combine start and end so distinct ranges don't collide.
        return HashCode.Combine(bank.Start, bank.End);
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
