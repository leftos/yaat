using System.Reflection;
using Xunit.v3;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Sim;

[assembly: PreferencesFilePerTest]

namespace Yaat.Client.UI.Tests.Helpers;

/// <summary>
/// Starts every test in this assembly on its own fresh preferences file under the per-process root, so nothing a test or a
/// window it leaves behind saves reaches a later test. A process-wide switch is safe because the assembly runs one test at a
/// time; there is no After, since the next test's Before switches again. The folder is created up front, so a test can write
/// the file itself before any save has.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class PreferencesFilePerTestAttribute : BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        string folder = Directory.CreateDirectory(YaatPaths.Combine("tests", Guid.NewGuid().ToString("N"))).FullName;
        UserPreferences.UseFile(Path.Combine(folder, "preferences.json"));
    }
}
