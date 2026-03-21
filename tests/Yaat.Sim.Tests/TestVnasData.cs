using Yaat.Sim.Data;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

/// <summary>
/// Thin wrapper around <see cref="Yaat.Sim.Testing.TestVnasData"/> for backward compatibility.
/// Existing tests reference this class; new tests should use the Sim version directly.
/// </summary>
internal static class TestVnasData
{
    internal static NavigationDatabase? NavigationDb => Yaat.Sim.Testing.TestVnasData.NavigationDb;

    internal static void EnsureInitialized() => Yaat.Sim.Testing.TestVnasData.EnsureInitialized();
}
