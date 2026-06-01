using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// Regression for the command-box parse of modifier-only verbs.
///
/// Bug (S2-OAK-5, 2026-05-30): typing <c>RES CROSS 28L</c> in the command box
/// failed with "N7LJ is not a recognized command", forcing the controller to the
/// <c>RES; CROSS 28L</c> workaround. Root cause: <see cref="CommandDefinition.DeriveArgMode"/>
/// only inspected <c>Overloads</c> and ignored <c>CompoundModifiers</c>, so
/// <c>RES</c> (whose only overload is no-arg, with CROSS/HS as modifiers) derived
/// <see cref="ArgMode.None"/>. The client <see cref="CommandSchemeParser"/> then
/// rejected any argument with "does not accept arguments", so the combined form
/// never parsed. The server-side path (<c>GroundCommandParser.ParseResume</c>)
/// always handled it — which is why existing engine.SendCommand tests passed.
/// </summary>
public class ResCrossInputParseTests
{
    private static readonly CommandScheme Scheme = CommandScheme.Default();

    [Fact]
    public void ResumeArgMode_IsOptional_BecauseItHasCompoundModifiers()
    {
        var def = CommandRegistry.Get(CanonicalCommandType.Resume);
        Assert.NotNull(def);
        Assert.Equal(ArgMode.Optional, def.ArgMode);
    }

    [Theory]
    [InlineData("RES CROSS 28L")]
    [InlineData("res cross 28l")]
    [InlineData("RES CROSS 28L 10R")]
    [InlineData("RES HS B")]
    public void ResWithModifier_ParsesFromCommandBox(string input)
    {
        var result = CommandSchemeParser.ParseCompound(input, Scheme, out var failure);
        Assert.Null(failure);
        Assert.NotNull(result);
    }

    [Fact]
    public void BareRes_StillParses()
    {
        var result = CommandSchemeParser.ParseCompound("RES", Scheme, out var failure);
        Assert.Null(failure);
        Assert.NotNull(result);
    }

    [Fact]
    public void ModifierOnlyVerb_Cland_ParsesFromCommandBox()
    {
        // Same DeriveArgMode gap affected every modifier-only command, e.g. CLAND NODEL.
        var result = CommandSchemeParser.ParseCompound("CLAND NODEL", Scheme, out var failure);
        Assert.Null(failure);
        Assert.NotNull(result);
    }
}
