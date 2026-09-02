#!/usr/bin/env dotnet run
#:package Microsoft.Diagnostics.Tracing.TraceEvent@3.1.16
// Attribute GC allocation ticks in a `--profile gc-verbose` nettrace to the
// nearest project frame.
//
// Every GCAllocationTick event carries a call stack whose leaf is almost always
// a BCL or runtime method (string.Concat, List.Grow, the JIT helper). Charging
// the leaf tells you nothing actionable, so this walks each stack outward from
// the leaf until it hits the first frame whose module matches --prefix and
// charges the bytes there — the project method that caused the allocation.
//
// Usage:
//   dotnet run .claude/skills/dotnet-test-suite-profiling/scripts/alloc_ticks.cs \
//       -- .tmp/prof/gc.nettrace [--prefix Yaat.] [--top 25]
//
// Note: GCAllocationTick fires roughly once per 100 KB allocated, so the byte
// totals are a sampled estimate. Rank by them; do not quote them as exact.

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

string? tracePath = null;
string prefix = "Yaat.";
int top = 25;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--prefix" when i + 1 < args.Length:
            prefix = args[++i];
            break;
        case "--top" when i + 1 < args.Length:
            top = int.Parse(args[++i]);
            break;
        case "-h":
        case "--help":
            Console.WriteLine("usage: alloc_ticks.cs <trace.nettrace> [--prefix Yaat.] [--top 25]");
            return 0;
        default:
            tracePath = args[i];
            break;
    }
}

if (tracePath is null)
{
    Console.Error.WriteLine("usage: alloc_ticks.cs <trace.nettrace> [--prefix Yaat.] [--top 25]");
    return 2;
}

string etlx = TraceLog.CreateFromEventPipeDataFile(tracePath);
using var log = new TraceLog(etlx);
var source = log.Events.GetSource();

var bytesByFrame = new Dictionary<string, (long Bytes, long Ticks)>();
long totalBytes = 0;
long totalTicks = 0;
long unattributed = 0;

source.Clr.GCAllocationTick += data =>
{
    long amount = data.AllocationAmount64 != 0 ? data.AllocationAmount64 : data.AllocationAmount;
    totalBytes += amount;
    totalTicks++;

    string? attributed = null;
    for (var frame = data.CallStack(); frame is not null; frame = frame.Caller)
    {
        string name = frame.CodeAddress.FullMethodName;
        string module = frame.CodeAddress.ModuleName ?? string.Empty;
        if (name.StartsWith(prefix, StringComparison.Ordinal) || module.StartsWith(prefix.TrimEnd('.'), StringComparison.Ordinal))
        {
            attributed = string.IsNullOrEmpty(name) ? module : name;
            break;
        }
    }

    if (attributed is null)
    {
        unattributed += amount;
        return;
    }

    var prior = bytesByFrame.TryGetValue(attributed, out var v) ? v : (0L, 0L);
    bytesByFrame[attributed] = (prior.Item1 + amount, prior.Item2 + 1);
};

source.Process();

Console.WriteLine($"{totalTicks:N0} allocation ticks, {totalBytes / 1024.0 / 1024.0:N1} MB sampled");
Console.WriteLine($"{unattributed / 1024.0 / 1024.0:N1} MB had no '{prefix}' frame on the stack (runtime/test-host allocations)");
Console.WriteLine($"\ntop {top} '{prefix}' frames by sampled bytes:");
foreach (var (frame, v) in bytesByFrame.OrderByDescending(kv => kv.Value.Bytes).Take(top))
{
    Console.WriteLine($"{v.Bytes / 1024.0 / 1024.0, 10:N1} MB {v.Ticks, 7:N0} ticks  {frame}");
}

return 0;
