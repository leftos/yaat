# Contributing to YAAT

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [CSharpier](https://csharpier.com/) — `dotnet tool install -g csharpier`
- [yaat-server](https://github.com/leftos/yaat-server) cloned as a sibling directory

## Repository Layout

```
src/Yaat.Client/    Avalonia desktop app (instructor/RPO UI)
src/Yaat.Sim/       Shared simulation library (no UI dependencies)
tests/              Unit tests for both projects
```

`Yaat.Sim` is shared between this repo and yaat-server. Changes to simulation logic here are picked up by yaat-server on its next build (sibling directory reference). If you're also working in yaat-server, make sure to [set up its git hooks](https://github.com/leftos/yaat-server/blob/master/CONTRIBUTING.md#git-hooks) so the submodule pin stays in sync.

## Building

```bash
dotnet build           # Build the solution
dotnet test            # Run all tests
```

## Code Style

- **Line width**: 150 characters (configured in `.csharpierrc`)
- **Nullable reference types**: enabled project-wide
- **Implicit usings**: enabled
- C# 13 / .NET 10

### Formatting

Run these commands before every commit, in this order:

```bash
dotnet format style           # Fix code style issues
dotnet format analyzers       # Fix analyzer warnings
dotnet csharpier format .     # Apply CSharpier formatting
dotnet build                  # Verify the build still passes
```

Do **not** run bare `dotnet format` — its whitespace rules conflict with CSharpier.

### Conventions

- MVVM with `[ObservableProperty]` / `[RelayCommand]` from CommunityToolkit.Mvvm
- `_camelCase` backing fields generate `PascalCase` properties
- SignalR callbacks run on background threads — marshal to UI via `Dispatcher.UIThread.Post()`
- Never swallow exceptions silently; log with `AppLog`

## Commits

- Prefix with a type tag: `fix:`, `feat:`, `add:`, `docs:`, `ref:`, `test:`, `ci:`, `dep:`, `chore:`
- Imperative mood, 72-character subject line limit
- One logical change per commit

## Tests

Tests live in `tests/` mirroring the source project structure. Run the relevant subset before committing:

```bash
dotnet test tests/Yaat.Sim.Tests
dotnet test tests/Yaat.Client.Tests
```

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
