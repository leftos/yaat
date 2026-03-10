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

        // Initially selects sig1 (bare, no args)
        Assert.Equal(0, state.SelectedOverloadIndex);

        state.NextOverload();
        Assert.Equal(1, state.SelectedOverloadIndex);

        state.NextOverload();
        Assert.Equal(0, state.SelectedOverloadIndex); // wraps

        state.PreviousOverload();
        Assert.Equal(1, state.SelectedOverloadIndex); // wraps back
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
}
