using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// DA takes exactly the abbreviated flight plan fields of docs/crc/stars.md Table 12, in any order, and refuses the whole
/// entry naming a token that is none of them (YAAT-2944: "DA KOAK*KAPC C172 045" filed KOAK*KAPC as the type and lost it).
/// </summary>
public class AbbreviatedFlightPlanFormsTests
{
    public AbbreviatedFlightPlanFormsTests() => TestVnasData.EnsureInitialized();

    private static CreateAbbreviatedFlightPlanCommand ParseDa(string text)
    {
        ParseResult<CompoundCommand> result = CommandParser.ParseCompound(text);
        Assert.True(result.IsSuccess, result.Reason);
        return Assert.IsType<CreateAbbreviatedFlightPlanCommand>(result.Value!.Blocks[0].Commands[0]);
    }

    private static string RefuseDa(string fields)
    {
        ParseResult<CreateAbbreviatedFlightPlanCommand> result = CommandParser.ReadAbbreviatedFlightPlanFields(fields);
        Assert.False(result.IsSuccess, $"'{fields}' read as {result.Value}");
        return result.Reason!;
    }

    [Fact]
    public void DepartureDestination_RefusedAsNotADaField()
    {
        string reason = RefuseDa("KOAK*KAPC C172 045");

        Assert.Equal("KOAK*KAPC: departure*destination is not a DA field", reason);
    }

    [Fact]
    public void TypeAndAltitude_FileVfrPlan()
    {
        CreateAbbreviatedFlightPlanCommand cmd = ParseDa("DA C172 045");

        Assert.Equal(new CreateAbbreviatedFlightPlanCommand(null, null, null, "C172", 4500, "VFR"), cmd);
    }

    [Fact]
    public void DotP_StaysVfrOnTop()
    {
        CreateAbbreviatedFlightPlanCommand cmd = ParseDa("DA C172 055 .P");

        Assert.Equal(new CreateAbbreviatedFlightPlanCommand(null, null, null, "C172", 5500, "OTP"), cmd);
    }

    [Fact]
    public void CrcExample_BeaconScratchpad2TypeAltitude()
    {
        CreateAbbreviatedFlightPlanCommand cmd = ParseDa("DA 4304 +VFF C182 065");

        Assert.Equal(new CreateAbbreviatedFlightPlanCommand(4304, null, "VFF", "C182", 6500, "VFR"), cmd);
    }

    [Fact]
    public void FieldsInAnotherOrder_ParseTheSame()
    {
        CreateAbbreviatedFlightPlanCommand cmd = ParseDa("DA 065 C182 .V +VFF 4304");

        Assert.Equal(new CreateAbbreviatedFlightPlanCommand(4304, null, "VFF", "C182", 6500, "VFR"), cmd);
    }

    [Theory]
    [InlineData("DA `ABC C172")]
    [InlineData("DA \u0394ABC C172")]
    public void Scratchpad1_BacktickOrDeltaPrefix(string text)
    {
        CreateAbbreviatedFlightPlanCommand cmd = ParseDa(text);

        Assert.Equal(new CreateAbbreviatedFlightPlanCommand(null, "ABC", null, "C172", null, "VFR"), cmd);
    }

    [Fact]
    public void Caret_IsNotAScratchpadPrefix()
    {
        string reason = RefuseDa("^ABC C172");

        Assert.Equal("^ABC is not a DA field (beacon, `SP1, +SP2, type, altitude, .V/.P/.E)", reason);
    }

    [Fact]
    public void Scratchpad2_TakesAllowedSymbols()
    {
        CreateAbbreviatedFlightPlanCommand cmd = ParseDa("DA +A.B C172");

        Assert.Equal(new CreateAbbreviatedFlightPlanCommand(null, null, "A.B", "C172", null, "VFR"), cmd);
    }

    [Fact]
    public void ThreeCharacterType_Accepted()
    {
        CreateAbbreviatedFlightPlanCommand cmd = ParseDa("DA C17 100");

        Assert.Equal(new CreateAbbreviatedFlightPlanCommand(null, null, null, "C17", 10000, "VFR"), cmd);
    }

    [Theory]
    [InlineData("1C72", "1C72")]
    [InlineData("C1725", "C1725")]
    [InlineData("FOO!BAR", "FOO!BAR")]
    public void TokenOfNoField_RefusedNamingIt(string text, string token)
    {
        string reason = RefuseDa(text);

        Assert.Equal($"{token} is not a DA field (beacon, `SP1, +SP2, type, altitude, .V/.P/.E)", reason);
    }

    [Fact]
    public void TypeWithEquipment_AltitudeAndIfr()
    {
        CreateAbbreviatedFlightPlanCommand cmd = ParseDa("DA B738/L 350 .E");

        Assert.Equal(new CreateAbbreviatedFlightPlanCommand(null, null, null, "B738/L", 35000, "IFR"), cmd);
    }
}
