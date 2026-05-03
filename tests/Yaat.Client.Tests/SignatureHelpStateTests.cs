using Xunit;
using Yaat.Client.Services;
using Yaat.Sim.Commands;

namespace Yaat.Client.Tests;

public class SignatureHelpStateTests
{
    private static CommandSignatureSet MakeSet(params CommandSignature[] sigs)
    {
        return new CommandSignatureSet(sigs);
    }

    private static CommandParameter Lit(string name)
    {
        return new CommandParameter(name, "literal", false, IsLiteral: true);
    }

    private static CommandSignature MakeSig(string label, CommandParameter[] parameters, string? hint = null)
    {
        return new CommandSignature(CanonicalCommandType.FlyHeading, label, ["FH"], parameters, hint);
    }

    [Fact]
    public void Show_SetsSingleOverload()
    {
        var state = new SignatureHelpState();
        var sig = MakeSig("Fly Heading", [new CommandParameter("heading", "0-360", false)]);
        var set = MakeSet(sig);

        state.Show(set, 0, []);

        Assert.True(state.IsVisible);
        Assert.Equal(1, state.OverloadCount);
        Assert.Equal(0, state.SelectedOverloadIndex);
        Assert.Equal(0, state.ActiveParameterIndex);
        Assert.Equal(sig, state.CurrentSignature);
    }

    [Fact]
    public void BuildParts_SingleParam_MarksActive()
    {
        var sig = MakeSig("Fly Heading", [new CommandParameter("heading", "0-360", false)]);

        var parts = SignatureHelpState.BuildParts(sig, 0);

        Assert.Equal(3, parts.Count);
        Assert.Equal("FH", parts[0].Text);
        Assert.False(parts[0].IsParameter);
        Assert.Equal(" ", parts[1].Text);
        Assert.Equal("[heading]", parts[2].Text);
        Assert.True(parts[2].IsParameter);
        Assert.True(parts[2].IsActive);
    }

    [Fact]
    public void BuildParts_LiteralParam_NoSquareBrackets()
    {
        var sig = new CommandSignature(
            CanonicalCommandType.ClearedForTakeoff,
            "CTO RH",
            ["CTO"],
            [Lit("RH"), new CommandParameter("heading", "0-360", false)],
            "Fly runway heading until heading given"
        );

        var parts = SignatureHelpState.BuildParts(sig, 1);

        // CTO, " ", RH, " ", [heading] = 5 parts
        Assert.Equal(5, parts.Count);
        Assert.Equal("CTO", parts[0].Text);
        Assert.Equal("RH", parts[2].Text);
        Assert.False(parts[2].IsParameter); // literal is not a parameter
        Assert.Equal("[heading]", parts[4].Text);
        Assert.True(parts[4].IsParameter);
        Assert.True(parts[4].IsActive);
    }

    [Fact]
    public void BuildParts_MultipleParams_HighlightsCorrectOne()
    {
        var sig = MakeSig(
            "PTAC",
            [
                new CommandParameter("heading", "0-360", false),
                new CommandParameter("distance", "nm", false),
                new CommandParameter("approach", "approach ID", false),
            ]
        );

        var parts = SignatureHelpState.BuildParts(sig, 1);

        // FH, " ", {heading}, " ", {distance}, " ", {approach} = 7 parts
        Assert.Equal(7, parts.Count);
        Assert.False(parts[2].IsActive); // heading - not active
        Assert.True(parts[4].IsActive); // distance - active
        Assert.False(parts[6].IsActive); // approach - not active
    }

    [Fact]
    public void OverloadCycling_WrapsAround()
    {
        var state = new SignatureHelpState();
        var sig1 = MakeSig("Go Around", []);
        var sig2 = MakeSig("Go Around — Heading", [new CommandParameter("heading", "0-360", false)]);
        var set = MakeSet(sig1, sig2);

        state.Show(set, 0, []);

        // paramIndex=0 prefers the 1-arg overload (it still has a slot at the cursor) over
        // the bare overload, since signature help is only shown once the user has typed past
        // the verb (i.e. has indicated intent to type more).
        Assert.Equal(1, state.SelectedOverloadIndex);

        state.NextOverload();
        Assert.Equal(0, state.SelectedOverloadIndex); // wraps

        state.NextOverload();
        Assert.Equal(1, state.SelectedOverloadIndex);

        state.PreviousOverload();
        Assert.Equal(0, state.SelectedOverloadIndex);
    }

    [Fact]
    public void AutoSelect_AdvancesToWiderOverload_WhenCursorMovesPastCurrent()
    {
        // Mirrors the ELB shape: bare / runway / runway+distance.
        // "ELB 28L"  (paramIndex=0) -> 1-arg overload, cursor on runway.
        // "ELB 28L " (paramIndex=1) -> 2-arg overload, cursor on distance.
        var bareSig = new CommandSignature(CanonicalCommandType.EnterLeftBase, "ELB", ["ELB"], [], "Bare");
        var rwySig = new CommandSignature(
            CanonicalCommandType.EnterLeftBase,
            "ELB Runway",
            ["ELB"],
            [new CommandParameter("runway", "rwy", false)],
            "Runway"
        );
        var rwyDistSig = new CommandSignature(
            CanonicalCommandType.EnterLeftBase,
            "ELB Runway+Distance",
            ["ELB"],
            [new CommandParameter("runway", "rwy", false), new CommandParameter("distance", "nm", false)],
            "Runway + Distance"
        );
        var set = MakeSet(bareSig, rwySig, rwyDistSig);
        var state = new SignatureHelpState();

        state.Show(set, 0, ["28L"]);
        Assert.Equal(1, state.SelectedOverloadIndex);

        state.Show(set, 1, ["28L"]);
        Assert.Equal(2, state.SelectedOverloadIndex);
    }

    [Fact]
    public void Dismiss_ClearsState()
    {
        var state = new SignatureHelpState();
        var sig = MakeSig("Fly Heading", [new CommandParameter("heading", "0-360", false)]);
        var set = MakeSet(sig);

        state.Show(set, 0, []);
        Assert.True(state.IsVisible);

        state.Dismiss();
        Assert.False(state.IsVisible);
        Assert.Null(state.CurrentSignature);
        Assert.Empty(state.SignatureParts);
    }

    [Fact]
    public void AutoSelect_PicksLiteralMatch()
    {
        var state = new SignatureHelpState();

        var bareSig = new CommandSignature(CanonicalCommandType.ClearedForTakeoff, "CTO", ["CTO"], [], "Bare takeoff");
        var rhSig = new CommandSignature(
            CanonicalCommandType.ClearedForTakeoff,
            "CTO RH",
            ["CTO"],
            [new CommandParameter("RH", "literal", false, IsLiteral: true)],
            "Runway heading"
        );
        var hdgSig = new CommandSignature(
            CanonicalCommandType.ClearedForTakeoff,
            "CTO Heading",
            ["CTO"],
            [new CommandParameter("heading", "0-360", false)],
            "Fly heading"
        );

        var set = MakeSet(bareSig, rhSig, hdgSig);

        // When typed "RH", should auto-select the RH overload
        state.Show(set, 0, ["RH"]);
        Assert.Equal(1, state.SelectedOverloadIndex);
    }

    private static CommandSignatureSet MakeCtoSet()
    {
        // Mirrors the registry overload shapes for CTO that participate in the bug:
        // 0 Bare, 1 RH+alt, 2 OC+alt, 3 Heading+alt, 4 LT+heading+alt
        var bareSig = new CommandSignature(CanonicalCommandType.ClearedForTakeoff, "CTO", ["CTO"], [], "Bare");
        var rhSig = new CommandSignature(
            CanonicalCommandType.ClearedForTakeoff,
            "CTO RH",
            ["CTO"],
            [Lit("RH"), new CommandParameter("altitude", "alt", true)],
            "Runway heading"
        );
        var ocSig = new CommandSignature(
            CanonicalCommandType.ClearedForTakeoff,
            "CTO OC",
            ["CTO"],
            [Lit("OC"), new CommandParameter("altitude", "alt", true)],
            "On course"
        );
        var hdgSig = new CommandSignature(
            CanonicalCommandType.ClearedForTakeoff,
            "CTO Heading",
            ["CTO"],
            [new CommandParameter("heading", "0-360", false), new CommandParameter("altitude", "alt", true)],
            "Fly heading"
        );
        var ltSig = new CommandSignature(
            CanonicalCommandType.ClearedForTakeoff,
            "CTO LT",
            ["CTO"],
            [Lit("LT"), new CommandParameter("heading", "0-360", false), new CommandParameter("altitude", "alt", true)],
            "Turn left to heading"
        );
        return MakeSet(bareSig, rhSig, ocSig, hdgSig, ltSig);
    }

    [Fact]
    public void AutoSelect_NumericArg_PrefersHeadingOverloadOverLiteralOverloads()
    {
        // Reproduces the CTO 020 150 bug: "020" is a heading, not a literal RH/OC/LT, so
        // signature help must show the Heading overload, not the first registered literal one.
        var state = new SignatureHelpState();
        var set = MakeCtoSet();

        // "CTO 020 150" with no trailing space → typedArgs=["020","150"], paramIndex=1
        state.Show(set, 1, ["020", "150"]);

        Assert.Equal("CTO Heading", state.CurrentSignature?.Label);
    }

    [Fact]
    public void AutoSelect_CommittedLiteralMismatch_EliminatesOverload()
    {
        // With "020" committed as the first arg, all literal-prefixed overloads must be
        // eliminated regardless of how they would otherwise score.
        var state = new SignatureHelpState();
        var set = MakeCtoSet();

        state.Show(set, 1, ["020", "150"]);

        // Should not pick RH (idx 1), OC (idx 2), or LT (idx 4) — only Heading (idx 3) survives.
        Assert.Equal(3, state.SelectedOverloadIndex);
    }

    [Fact]
    public void AutoSelect_InProgressLiteralPrefix_KeepsMatchingLiterals()
    {
        // Regression guard: while typing "R", the user might be heading toward "RH"; signature
        // help must keep the RH overload eligible (prefix match), not eliminate it for not
        // exactly equaling "RH".
        var state = new SignatureHelpState();
        var set = MakeCtoSet();

        // "CTO R" with no trailing space → typedArgs=["R"], paramIndex=0
        state.Show(set, 0, ["R"]);

        Assert.Equal("CTO RH", state.CurrentSignature?.Label);
    }

    [Fact]
    public void AutoSelect_InProgressLiteralPrefixOnNumeric_StillPicksHeading()
    {
        // While typing the heading "020" (no trailing space), Heading overload should be
        // picked because "RH"/"OC"/"LT" don't start with "0".
        var state = new SignatureHelpState();
        var set = MakeCtoSet();

        // "CTO 020" with no trailing space → typedArgs=["020"], paramIndex=0
        state.Show(set, 0, ["020"]);

        Assert.Equal("CTO Heading", state.CurrentSignature?.Label);
    }

    [Fact]
    public void ActiveParameterDescription_ShowsTypeHint()
    {
        var state = new SignatureHelpState();
        var sig = MakeSig("Fly Heading", [new CommandParameter("heading", "0-360", false)], "Fly assigned heading");
        var set = MakeSet(sig);

        state.Show(set, 0, []);

        Assert.Equal("heading: 0-360", state.ActiveParameterDescription);
    }

    [Fact]
    public void UpdateParameterIndex_ChangesHighlighting()
    {
        var state = new SignatureHelpState();
        var sig = MakeSig("Hold", [new CommandParameter("fix", "fix name", false), new CommandParameter("inbound", "0-360", false)]);
        var set = MakeSet(sig);

        state.Show(set, 0, []);
        Assert.True(state.SignatureParts[2].IsActive); // fix
        Assert.False(state.SignatureParts[4].IsActive); // inbound

        state.UpdateParameterIndex(1);
        Assert.False(state.SignatureParts[2].IsActive); // fix
        Assert.True(state.SignatureParts[4].IsActive); // inbound
    }

    [Fact]
    public void BuildParts_OptionalParam_RendersWithQuestionMark()
    {
        var sig = new CommandSignature(
            CanonicalCommandType.ClearedForTakeoff,
            "CTO MRC",
            ["CTO"],
            [Lit("MRC"), new CommandParameter("altitude", "alt", true)],
            "Turn right crosswind on departure"
        );

        var parts = SignatureHelpState.BuildParts(sig, 1);

        // CTO, " ", MRC, " ", [altitude?] = 5 parts
        Assert.Equal(5, parts.Count);
        Assert.Equal("MRC", parts[2].Text);
        Assert.False(parts[2].IsParameter);
        Assert.Equal("[altitude?]", parts[4].Text);
        Assert.True(parts[4].IsParameter);
        Assert.True(parts[4].IsActive);
    }

    [Fact]
    public void BuildParts_RequiredAndOptionalParams_RenderDifferently()
    {
        var sig = new CommandSignature(
            CanonicalCommandType.ClearedForTakeoff,
            "CTO LT",
            ["CTO"],
            [Lit("LT"), new CommandParameter("heading", "0-360", false), new CommandParameter("altitude", "alt", true)],
            "Turn left to heading on departure"
        );

        var parts = SignatureHelpState.BuildParts(sig, 0);

        // CTO, " ", LT, " ", [heading], " ", [altitude?] = 7 parts
        Assert.Equal(7, parts.Count);
        Assert.Equal("[heading]", parts[4].Text);
        Assert.Equal("[altitude?]", parts[6].Text);
    }
}
