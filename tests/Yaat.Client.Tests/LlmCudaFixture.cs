using LLama.Native;
using Xunit;

namespace Yaat.Client.Tests;

/// <summary>
/// One-time initializer for LLamaSharp's <see cref="NativeLibraryConfig"/>. Runs once before any
/// test in the <c>LLM</c> collection touches a <c>LLamaWeights</c>, which is the only safe moment
/// to configure the backend — <see cref="NativeLibraryConfig"/> throws
/// <see cref="InvalidOperationException"/> once the native library has loaded.
///
/// The test project pulls in both <c>LLamaSharp.Backend.Cpu</c> (transitively from Yaat.Client) and
/// <c>LLamaSharp.Backend.Cuda12</c> (direct PackageReference in the test csproj). At runtime:
/// - on a CUDA host (dev box, not CI), <c>WithCuda(true)</c> picks the CUDA native, enabling fast
///   GPU inference when the opt-in GGUF is present,
/// - on a CPU-only host, <c>WithAutoFallback(true)</c> transparently falls through to the CPU
///   backend and the same tests still run (just slower).
///
/// The fixture intentionally does NOT touch any weights itself — doing so would "lock" the native
/// library before individual tests get a chance to decide whether to skip.
/// </summary>
public sealed class LlmCudaFixture
{
    public LlmCudaFixture()
    {
        try
        {
            NativeLibraryConfig.All.WithCuda(true).WithAutoFallback(true);
        }
        catch (InvalidOperationException)
        {
            // Another test (or a previous run in the same host) already loaded the native library.
            // Swallow — that's fine, whichever backend was picked first is the one we use.
        }
    }
}

[CollectionDefinition("LLM")]
public sealed class LlmCollection : ICollectionFixture<LlmCudaFixture>
{
    // xUnit marker class — no code, just the type declaration.
}
