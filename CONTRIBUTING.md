# Contributing to YAAT

If you just want to run YAAT, grab a prebuilt installer from the [Releases page](https://github.com/leftos/yaat/releases/latest) — the contributor path below is only for people modifying the code. The [Installation Guide](INSTALL.md#building-from-source) has the full source-build walkthrough including the .NET SDK install.

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

Formatting and style are build gates. The rules live in `.editorconfig`; `Directory.Build.props` turns on
`EnforceCodeStyleInBuild` and `TreatWarningsAsErrors`, so a rule at `warning` fails the build, and CI also fails on any
info-level style finding and on any file CSharpier would change (C# and `.axaml`).

The `prek` pre-commit hooks apply the fixers to staged files and re-stage them. To do the same by hand, in this order:

```bash
dotnet tool restore                                  # CSharpier is a pinned local tool
dotnet format style --severity info                  # Fix code style issues (run twice: one pass does not always converge)
dotnet format analyzers                              # Fix analyzer warnings
dotnet csharpier format .                            # Whitespace and line breaks
dotnet build -p:TreatWarningsAsErrors=true           # Verify the build still passes
```

What CI checks: `dotnet csharpier check .` and `dotnet format style yaat.slnx --verify-no-changes --severity info`.

The conventions the rules encode: braces on every control-flow body; file-scoped namespaces that match the folder path;
`var` only when the right-hand side names the type (`new T()`, a cast) and the explicit type everywhere else; expression
bodies for single-line methods; primary constructors; collection expressions; a named tuple local keeps its name rather
than being deconstructed.

Do **not** run bare `dotnet format` — its whitespace rules conflict with CSharpier.

`git config blame.ignoreRevsFile .git-blame-ignore-revs` hides the mechanical formatting commits from `git blame`.

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
