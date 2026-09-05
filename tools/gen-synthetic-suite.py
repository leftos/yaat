"""Emit a synthetic test project matching Yaat.Sim.Tests' SHAPE in either xunit.v3 or TUnit.

The question this exists to answer: does TUnit's source generator regress incremental build time
at ~9,000 tests? Generator work scales with test/class count, not with what the tests do, so a
synthetic project of the same shape measures it without converting 840 real files first.

Shape taken from the real suite (TRX, 2026-09-03): 9,363 tests across 846 classes (~11 cases per
class), 3,139 [InlineData] rows, and ~400 of ~846 classes taking an ITestOutputHelper. Test bodies
are deliberately trivial - we are measuring COMPILE cost, not run cost.
"""

import argparse
import pathlib

AP = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
AP.add_argument("--framework", choices=["tunit", "xunit"], required=True)
AP.add_argument("--out", required=True)
AP.add_argument("--classes", type=int, default=846)
AP.add_argument("--plain-per-class", type=int, default=8)
AP.add_argument("--arg-rows", type=int, default=3, help="parameterized rows per class")
AP.add_argument("--output-helper-fraction", type=float, default=0.47)
AP.add_argument("--tunit-version", default="1.65.68")
A = AP.parse_args()

out = pathlib.Path(A.out)
(out / "Tests").mkdir(parents=True, exist_ok=True)

TUNIT_CSPROJ = f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="TUnit" Version="{A.tunit_version}" />
  </ItemGroup>
</Project>
"""

XUNIT_CSPROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.8.1" />
    <PackageReference Include="xunit.v3" Version="3.2.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
</Project>
"""

TUNIT_GLOBALS = """global using ITestOutputHelper = SynthSuite.TestOutputHelper;
"""

TUNIT_HELPERS = """namespace SynthSuite;

public sealed class TestOutputHelper
{
    public void WriteLine(string message) => TestContext.Current?.OutputWriter.WriteLine(message);
}
"""

XUNIT_GLOBALS = """global using Xunit;
"""


def tunit_class(i: int, with_output: bool) -> str:
    attr = "[ClassDataSource<TestOutputHelper>(Shared = SharedType.None)]\n" if with_output else ""
    decl = f"public sealed class Synth{i}Tests(ITestOutputHelper output)" if with_output else f"public sealed class Synth{i}Tests"
    use = '        output.WriteLine($"n={n}");\n' if with_output else ""
    body = [f"namespace SynthSuite.Tests;\n\n{attr}{decl}\n{{"]
    for t in range(A.plain_per_class):
        body.append(f"""    [Test]
    public async Task Plain_{t}()
    {{
        var n = {t} + {i};
{use}        await Assert.That(n).IsEqualTo({t + i});
    }}
""")
    rows = "\n".join(f"    [Arguments({r}, {r * 2})]" for r in range(A.arg_rows))
    body.append(f"""    [Test]
{rows}
    public async Task Parameterised(int a, int expected)
    {{
        await Assert.That(a * 2).IsEqualTo(expected);
    }}
}}
""")
    return "\n".join(body)


def xunit_class(i: int, with_output: bool) -> str:
    ctor = (
        f"""    private readonly ITestOutputHelper _output;

    public Synth{i}Tests(ITestOutputHelper output) => _output = output;
"""
        if with_output
        else ""
    )
    use = '        _output.WriteLine($"n={n}");\n' if with_output else ""
    body = [f"namespace SynthSuite.Tests;\n\npublic sealed class Synth{i}Tests\n{{\n{ctor}"]
    for t in range(A.plain_per_class):
        body.append(f"""    [Fact]
    public void Plain_{t}()
    {{
        var n = {t} + {i};
{use}        Assert.Equal({t + i}, n);
    }}
""")
    rows = "\n".join(f"    [InlineData({r}, {r * 2})]" for r in range(A.arg_rows))
    body.append(f"""    [Theory]
{rows}
    public void Parameterised(int a, int expected)
    {{
        Assert.Equal(expected, a * 2);
    }}
}}
""")
    return "\n".join(body)


if A.framework == "tunit":
    (out / "SynthSuite.csproj").write_text(TUNIT_CSPROJ, encoding="utf-8")
    (out / "GlobalUsings.cs").write_text(TUNIT_GLOBALS, encoding="utf-8")
    (out / "Helpers.cs").write_text(TUNIT_HELPERS, encoding="utf-8")
    render = tunit_class
else:
    (out / "SynthSuite.csproj").write_text(XUNIT_CSPROJ, encoding="utf-8")
    (out / "GlobalUsings.cs").write_text(XUNIT_GLOBALS, encoding="utf-8")
    render = xunit_class

cutoff = int(A.classes * A.output_helper_fraction)
for i in range(A.classes):
    (out / "Tests" / f"Synth{i}Tests.cs").write_text(render(i, i < cutoff), encoding="utf-8")

cases = A.classes * (A.plain_per_class + A.arg_rows)
print(f"{A.framework}: {A.classes} classes, {cases} test cases, {cutoff} with an output helper -> {out}")
