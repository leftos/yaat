using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Scope;
using static Yaat.Client.Views.Scope.EramTargetGlyph;
using static Yaat.Client.Views.Scope.EramTrackSymbol;
using static Yaat.Client.Views.Scope.ScopeBlockLevel;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// Pins <see cref="EramSymbolState.Of"/> against the YAAT-2688 case table: the target glyph an ERAM-template track draws by its
/// transponder, ident, code, coast or frozen state and correlation, and the track-position symbol under a full block.
/// </summary>
public class EramSymbolStateTests
{
    /// <summary>One case-table row: the aircraft's state, the block level, and the glyph and symbol it draws.</summary>
    internal sealed record Case
    {
        public string Mode { get; init; } = "C";
        public bool Ident { get; init; }
        public uint Code { get; init; } = 4521;

        /// <summary><c>coast</c>, <c>coverage</c> (coverage coasting), <c>frozen</c>, or empty for none.</summary>
        public string Status { get; init; } = "";
        public string Destination { get; init; } = "";
        public bool Correlated { get; init; }
        public ScopeBlockLevel Level { get; init; } = Full;
        public EramTargetGlyph? Target { get; init; }
        public EramTrackSymbol? Track { get; init; }
    }

    /// <summary>The case table by row number; <see cref="ScopeEramLookRenderTests"/> draws every row too.</summary>
    internal static readonly Dictionary<int, Case> Rows = new()
    {
        [1] = new()
        {
            Destination = "KORD",
            Correlated = true,
            Target = CorrelatedBeacon,
            Track = Flat,
        },
        [2] = new()
        {
            Correlated = true,
            Target = CorrelatedBeacon,
            Track = Free,
        },
        [3] = new()
        {
            Code = 4522,
            Destination = "KORD",
            Target = UncorrelatedBeacon,
            Track = Free,
        },
        [4] = new()
        {
            Code = 4523,
            Target = UncorrelatedBeacon,
            Track = Free,
        },
        [5] = new()
        {
            Destination = "KORD",
            Correlated = true,
            Level = Partial,
            Target = CorrelatedBeacon,
        },
        [6] = new()
        {
            Code = 4523,
            Level = Limited,
            Target = UncorrelatedBeacon,
        },
        [7] = new()
        {
            Ident = true,
            Destination = "KORD",
            Correlated = true,
            Target = Ident,
            Track = Flat,
        },
        [8] = new()
        {
            Ident = true,
            Code = 4523,
            Level = Limited,
            Target = Ident,
        },
        [9] = new()
        {
            Code = 1200,
            Level = Limited,
            Target = Vfr,
        },
        [10] = new()
        {
            Code = 1200,
            Destination = "KORD",
            Correlated = true,
            Target = Vfr,
            Track = Flat,
        },
        [11] = new()
        {
            Mode = "Standby",
            Destination = "KORD",
            Correlated = true,
            Target = CorrelatedPrimary,
            Track = Flat,
        },
        [12] = new()
        {
            Mode = "Standby",
            Code = 4523,
            Level = Limited,
            Target = UncorrelatedPrimary,
        },
        [13] = new()
        {
            Status = "coast",
            Destination = "KORD",
            Correlated = true,
            Track = Coast,
        },
        [14] = new()
        {
            Status = "coverage",
            Destination = "KORD",
            Correlated = true,
            Level = Partial,
        },
        [15] = new()
        {
            Status = "frozen",
            Destination = "KORD",
            Correlated = true,
            Target = CorrelatedBeacon,
            Track = Frozen,
        },
        // A weak primary has no source: a standby target always draws the strong primary.
        [16] = new()
        {
            Mode = "Standby",
            Code = 4523,
            Level = Limited,
            Target = UncorrelatedPrimary,
        },
        // MCI and reduced separation have no source: an uncorrelated Mode C target draws the uncorrelated beacon.
        [17] = new()
        {
            Code = 4523,
            Level = Limited,
            Target = UncorrelatedBeacon,
        },
        [18] = new()
        {
            Destination = "KORD",
            Correlated = true,
            Level = Dwell,
            Target = CorrelatedBeacon,
            Track = Flat,
        },
        [19] = new()
        {
            Destination = "KORD",
            Correlated = true,
            Level = Ground,
            Target = CorrelatedBeacon,
        },
    };

    internal static AircraftModel ModelOf(Case c)
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.TransponderMode = c.Mode;
        ac.IsIdenting = c.Ident;
        ac.BeaconCode = c.Code;
        ac.EramIsCoastTrack = c.Status == "coast";
        ac.EramIsCoverageCoasting = c.Status == "coverage";
        ac.EramIsFrozen = c.Status == "frozen";
        ac.Destination = c.Destination;
        ac.EramIsCorrelated = c.Correlated;
        return ac;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    public void CaseTableRow_DrawsItsGlyphAndTrackSymbol(int row)
    {
        Case c = Rows[row];

        var state = EramSymbolState.Of(ModelOf(c), c.Level);

        Assert.Equal(c.Target, state.Target);
        Assert.Equal(c.Track, state.Track);
    }

    [Theory]
    [InlineData(false, UncorrelatedPrimary)]
    [InlineData(true, CorrelatedPrimary)]
    public void OffTransponder_DrawsAsAPrimary(bool correlated, EramTargetGlyph expected)
    {
        Case c = new()
        {
            Mode = "Off",
            Correlated = correlated,
            Level = Limited,
        };

        Assert.Equal(expected, EramSymbolState.Of(ModelOf(c), c.Level).Target);
    }
}
