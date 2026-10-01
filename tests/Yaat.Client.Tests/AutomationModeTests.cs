using Xunit;
using Yaat.Client.Automation;

namespace Yaat.Client.Tests;

public class AutomationModeTests
{
    [Theory]
    [InlineData("1", true)]
    [InlineData(null, false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    public void ReadFromEnvironment_OnlyOneMeansOn(string? value, bool expected)
    {
        string? previous = Environment.GetEnvironmentVariable(AutomationMode.EnvironmentVariable);
        Environment.SetEnvironmentVariable(AutomationMode.EnvironmentVariable, value);
        try
        {
            Assert.Equal(expected, AutomationMode.ReadFromEnvironment());
        }
        finally
        {
            Environment.SetEnvironmentVariable(AutomationMode.EnvironmentVariable, previous);
        }
    }
}
