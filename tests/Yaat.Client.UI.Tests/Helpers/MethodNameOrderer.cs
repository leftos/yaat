using Xunit.Sdk;
using Xunit.v3;

namespace Yaat.Client.UI.Tests.Helpers;

/// <summary>Runs a class's tests in ordinal order of their method names, for a test that needs an earlier one to have run first.</summary>
public sealed class MethodNameOrderer : ITestCaseOrderer
{
    public IReadOnlyCollection<TTestCase> OrderTestCases<TTestCase>(IReadOnlyCollection<TTestCase> testCases)
        where TTestCase : notnull, ITestCase => [.. testCases.OrderBy(t => t.TestMethod?.MethodName, StringComparer.Ordinal)];
}
