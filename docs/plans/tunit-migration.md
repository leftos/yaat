# TUnit Migration Plan

Migrate test projects from xUnit 2.9.3 to TUnit for source-generated, async-first testing.

## Motivation

- TUnit is source-generated (no reflection) — faster discovery and execution
- Async-native assertions with better error reporting
- Built for .NET 8+ (aligns with our .NET 10 stack)
- Single package dependency vs xUnit's three-package split

## Tradeoffs

- **Pro:** Faster test runs, modern API, single dependency
- **Con:** Smaller community, fewer Stack Overflow answers, every test signature changes
- **Risk:** Low — tests either compile or they don't; no behavioral ambiguity

## Scope

Three test projects:
- `tests/Yaat.Sim.Tests/` (~1015 tests, ~70 files)
- `tests/Yaat.Client.Tests/` (~125 tests, ~15 files)
- `tests/Yaat.Server.Tests/` (in yaat-server repo, ~113 tests)

## Steps

### Phase 1: Infrastructure

- [ ] Create a throwaway branch for the migration
- [ ] Update `.csproj` files: remove `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`; add `TUnit`
- [ ] Verify `dotnet test` discovers zero tests (confirms old runner is gone)

### Phase 2: Mechanical rewrites (per project)

- [ ] `using Xunit;` → `using TUnit;` (may need `using TUnit.Assertions;` etc.)
- [ ] `[Fact]` → `[Test]`
- [ ] `[Theory]` → `[Test]`
- [ ] `[InlineData(...)]` → `[Arguments(...)]`
- [ ] `[Skip(...)]` → `[Skip(...)]` (verify TUnit attribute name)
- [ ] All test methods: `void` → `async Task`
- [ ] All test methods: `public void MethodName` → `public async Task MethodName`

### Phase 3: Assertion rewrites

These require care — not pure find-and-replace:

- [ ] `Assert.Equal(expected, actual)` → `await Assert.That(actual).IsEqualTo(expected)` — note the argument order flip
- [ ] `Assert.Equal(expected, actual, precision: N)` → `await Assert.That(actual).IsEqualTo(expected).Within(tolerance)` — convert decimal-place precision to absolute tolerance (e.g., `precision: 1` ≈ `Within(0.05)`, `precision: 0` ≈ `Within(0.5)`)
- [ ] `Assert.True(x)` → `await Assert.That(x).IsTrue()`
- [ ] `Assert.True(x, message)` → `await Assert.That(x).IsTrue()` (TUnit shows expression on failure; custom messages may need `.Because(message)` or similar)
- [ ] `Assert.False(x)` → `await Assert.That(x).IsFalse()`
- [ ] `Assert.NotEqual(a, b)` → `await Assert.That(b).IsNotEqualTo(a)`
- [ ] `Assert.NotNull(x)` → `await Assert.That(x).IsNotNull()`
- [ ] `Assert.Null(x)` → `await Assert.That(x).IsNull()`
- [ ] `Assert.InRange(v, lo, hi)` → `await Assert.That(v).IsBetween(lo, hi)` or chained `.IsGreaterThanOrEqualTo(lo).And.IsLessThanOrEqualTo(hi)`
- [ ] `Assert.Contains(item, collection)` → `await Assert.That(collection).Contains(item)`
- [ ] `Assert.Empty(collection)` → `await Assert.That(collection).IsEmpty()`
- [ ] `Assert.Throws<T>(...)` → `await Assert.ThrowsAsync<T>(...)` or TUnit equivalent
- [ ] `Assert.Collection(...)` — rewrite to individual assertions

### Phase 4: Fixtures and lifecycle

- [ ] Audit `IClassFixture<T>` / `ICollectionFixture<T>` usages → convert to `[ClassDataSource]` or `[Before(Test)]`/`[After(Test)]`
- [ ] Constructor-based setup → `[Before(Test)]` methods
- [ ] `IDisposable` cleanup → `[After(Test)]` methods
- [ ] `IAsyncLifetime` → `[Before(Test)]`/`[After(Test)]` async methods

### Phase 5: Validation

- [ ] `dotnet build -p:TreatWarningsAsErrors=true` — zero warnings
- [ ] `dotnet test` — all tests pass
- [ ] Spot-check: break a few tests intentionally to verify failure messages are clear
- [ ] Compare test run time vs xUnit baseline

### Phase 6: yaat-server

- [ ] Repeat Phases 2-5 for `tests/Yaat.Server.Tests/`

## Assertion precision conversion reference

| xUnit `precision:` | Meaning (decimal places) | TUnit `Within()` |
|---------------------|--------------------------|-------------------|
| `0` | nearest integer | `0.5` |
| `1` | nearest tenth | `0.05` |
| `2` | nearest hundredth | `0.005` |

## Not in scope

- Changing test structure or adding new tests
- Adopting TUnit-specific features (DI, parallel config) — do that later if useful
- Migrating in phases (one project at a time) — all three YAAT projects share `Yaat.Sim.Tests` helpers, so migrate together
