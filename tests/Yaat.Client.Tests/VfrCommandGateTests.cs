using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim.Commands;

namespace Yaat.Client.Tests;

/// <summary>
/// The client-side VFR gate (issue #317). The simulation applies whatever it is given, so this is
/// what actually stops a VFR-only command reaching an IFR aircraft when the controller has not
/// opted in — and what surfaces the advisory when they have.
/// </summary>
public class VfrCommandGateTests
{
    private static AircraftModel Ac(string rules) => new() { Callsign = "UAL123", FlightRules = rules };

    private static AircraftModel Ifr() => Ac("IFR");

    private static AircraftModel Vfr() => Ac("VFR");

    [Theory]
    [InlineData(VfrCommandsForIfr.None, false)]
    [InlineData(VfrCommandsForIfr.EnterFinalOnly, true)]
    [InlineData(VfrCommandsForIfr.All, true)]
    public void EnterFinal_OnIfr_FollowsTheMode(VfrCommandsForIfr mode, bool allowed)
    {
        var result = VfrCommandGate.Evaluate(Ifr(), "EF 28L", mode);

        Assert.Equal(allowed, result.Allowed);
        Assert.Equal(allowed, result.BypassedForIfr);
        Assert.Equal(allowed, result.RejectionMessage is null);
    }

    [Theory]
    [InlineData(VfrCommandsForIfr.None, false)]
    [InlineData(VfrCommandsForIfr.EnterFinalOnly, false)]
    [InlineData(VfrCommandsForIfr.All, true)]
    public void EnterLeftDownwind_OnIfr_NeedsTheFullMode(VfrCommandsForIfr mode, bool allowed)
    {
        var result = VfrCommandGate.Evaluate(Ifr(), "ELD 28L", mode);

        Assert.Equal(allowed, result.Allowed);
    }

    [Theory]
    [InlineData("EF 28L")]
    [InlineData("ELD 28L")]
    [InlineData("MLT")]
    [InlineData("TG")]
    [InlineData("FOLLOW AAL55")]
    [InlineData("CM A025")]
    [InlineData("CTO MRC")]
    public void VfrAircraft_IsNeverGated(string canonical)
    {
        foreach (var mode in Enum.GetValues<VfrCommandsForIfr>())
        {
            var result = VfrCommandGate.Evaluate(Vfr(), canonical, mode);

            Assert.True(result.Allowed, $"'{canonical}' must pass for a VFR aircraft under {mode}");
            Assert.False(result.BypassedForIfr, "A VFR aircraft is not a bypass — no advisory should fire.");
        }
    }

    [Theory]
    [InlineData("H 270")]
    [InlineData("CM 050")]
    [InlineData("CVA 28L")]
    [InlineData("CLAND")]
    [InlineData("CIFR")]
    public void NonVfrOnlyCommands_PassEvenInStrictMode(string canonical)
    {
        var result = VfrCommandGate.Evaluate(Ifr(), canonical, VfrCommandsForIfr.None);

        Assert.True(result.Allowed);
        Assert.False(result.BypassedForIfr);
    }

    [Fact]
    public void NullTarget_Passes()
    {
        Assert.True(VfrCommandGate.Evaluate(null, "ELD 28L", VfrCommandsForIfr.None).Allowed);
    }

    /// <summary>
    /// The server owns the grammar and produces the better message, so an unparseable command is
    /// forwarded rather than swallowed by the gate.
    /// </summary>
    [Theory]
    [InlineData("NOTACOMMAND")]
    [InlineData("")]
    [InlineData("@@@ ###")]
    public void UnparseableInput_Passes(string canonical)
    {
        Assert.True(VfrCommandGate.Evaluate(Ifr(), canonical, VfrCommandsForIfr.None).Allowed);
    }

    /// <summary>A trailing block separator still parses, so the gate must not treat it as an escape hatch.</summary>
    [Fact]
    public void TrailingSeparator_DoesNotBypassTheGate()
    {
        Assert.False(VfrCommandGate.Evaluate(Ifr(), "ELD 28L ;", VfrCommandsForIfr.EnterFinalOnly).Allowed);
    }

    /// <summary>The RPO force-override prefix must not hide the command from the gate.</summary>
    [Fact]
    public void ForceOverridePrefix_IsStrippedBeforeChecking()
    {
        Assert.False(VfrCommandGate.Evaluate(Ifr(), "** ELD 28L", VfrCommandsForIfr.EnterFinalOnly).Allowed);
        Assert.True(VfrCommandGate.Evaluate(Ifr(), "** EF 28L", VfrCommandsForIfr.EnterFinalOnly).Allowed);
    }

    /// <summary>A compound is rejected whole if any member is blocked, in either block separator.</summary>
    [Theory]
    [InlineData("EF 28L; ELD 28L")]
    [InlineData("H 270, MLT")]
    [InlineData("ELD 28L; EF 28L")]
    public void Compound_RejectsWhenAnyMemberIsBlocked(string canonical)
    {
        var result = VfrCommandGate.Evaluate(Ifr(), canonical, VfrCommandsForIfr.EnterFinalOnly);

        Assert.False(result.Allowed);
        Assert.NotNull(result.RejectionMessage);
    }

    [Fact]
    public void Compound_AllowedWhenEveryMemberPasses()
    {
        var result = VfrCommandGate.Evaluate(Ifr(), "H 270; EF 28L", VfrCommandsForIfr.EnterFinalOnly);

        Assert.True(result.Allowed);
        Assert.True(result.BypassedForIfr);
    }

    [Fact]
    public void RejectionMessage_NamesTheVerbTheCallsignAndTheSetting()
    {
        var message = VfrCommandGate.Evaluate(Ifr(), "ELD 28L", VfrCommandsForIfr.None).RejectionMessage;

        Assert.NotNull(message);
        Assert.Contains("ELD", message);
        Assert.Contains("UAL123", message);
        Assert.Contains("CIFR", message);
        Assert.Contains("VFR commands for IFR aircraft", message);
    }

    [Fact]
    public void BypassAdvisory_NamesTheAircraftAndSaysRulesAreUnchanged()
    {
        var advisory = VfrCommandGate.BuildBypassAdvisory("UAL123");

        Assert.Contains("UAL123", advisory);
        Assert.Contains("IFR", advisory);
        Assert.Contains("unchanged", advisory, StringComparison.OrdinalIgnoreCase);
    }
}
