using System.Text.RegularExpressions;
using Xunit;

namespace Yaat.Client.UI.Tests;

/// <summary>
/// Automation mode must never activate a window or pin one on top, so every client
/// <c>Activate()</c> call (or <c>Activate</c> method group) and every <c>Topmost</c> assignment
/// has to sit in an allow-listed member, under a gate: either its nearest enclosing <c>if</c>
/// tests <c>!AutomationGate.SuppressActivation</c> / <c>!AutomationMode.IsEnabled</c>, or the
/// member returns early on <c>if (AutomationGate.SuppressActivation)</c> /
/// <c>if (AutomationMode.IsEnabled)</c> before the call. A raw <c>ShowDialog</c> call activates the
/// dialog and re-activates its owner on close, so it may only appear in <c>DialogPresenter</c>,
/// under the same gate; every other dialog opens through <c>DialogPresenter.ShowModalAsync</c>.
/// A dialog that returns a value closes through <c>DialogPresenter.Close</c>, never a bare
/// <c>Close(result)</c>, whose value would reach only a <c>ShowDialog</c> caller. Every
/// <c>WindowState</c> assignment sits in an allow-listed member under the same gate, because
/// Avalonia's Win32 backend activates a visible window on every state change except to Minimized.
/// MessageBox.Avalonia's own show methods build an activated window, so its <c>MessageBoxManager</c>,
/// show methods and <c>MsBoxWindow</c> appear only in <c>MessageBoxPresenter</c>, which opens the box
/// through <c>DialogPresenter</c>.
/// </summary>
public partial class AutomationModeSourceTests
{
    // "relative/path.cs::Member" for each member allowed to call Activate() behind the gate.
    private static readonly HashSet<string> GatedActivateMembers =
    [
        "src/Yaat.Client.Core/Views/WindowActivationExtensions.cs::RestoreAndActivate",
        "src/Yaat.Client.Core/Views/WindowGeometryHelper.cs::ApplyGeometryToWindow",
        "src/Yaat.Client/Views/MainWindow.axaml.cs::ReclaimFocusAfterLayoutApply",
        "src/Yaat.Client/Views/MainWindow.axaml.cs::FocusActiveCommandInput",
        "src/Yaat.Client/Views/TerminalWindow.axaml.cs::FocusCommandInput",
    ];

    // "relative/path.cs::Member" for each member allowed to assign Window.Topmost behind the gate.
    private static readonly HashSet<string> GatedTopmostMembers =
    [
        "src/Yaat.Client.Core/Views/WindowGeometryHelper.cs::SetTopmost",
        "src/Yaat.Client.Core/Views/WindowGroupRaiser.cs::RaiseAll",
    ];

    // "relative/path.cs::Member" for each member allowed to call Window.ShowDialog behind the gate.
    private static readonly HashSet<string> GatedShowDialogMembers = ["src/Yaat.Client.Core/Views/DialogPresenter.cs::ShowModalAsync"];

    // "relative/path.cs::Member" for each member allowed to assign Window.WindowState behind the gate.
    private static readonly HashSet<string> GatedWindowStateMembers =
    [
        "src/Yaat.Client.Core/Views/WindowActivationExtensions.cs::RestoreAndActivate",
        "src/Yaat.Client.Core/Views/WindowGeometryHelper.cs::LeaveMinimizedOrMaximizedFrame",
        "src/Yaat.Client.Core/Views/WindowGeometryHelper.cs::ApplySavedWindowState",
    ];

    // The one file allowed to build a MessageBox.Avalonia window; it opens it through DialogPresenter.
    private const string MessageBoxSeam = "src/Yaat.Client/Views/MessageBoxPresenter.cs";

    private static readonly string[] NegatedGateTokens = ["!AutomationGate.SuppressActivation", "!AutomationMode.IsEnabled"];

    private static readonly string[] BlockKeywords =
    [
        "if",
        "else",
        "for",
        "foreach",
        "while",
        "do",
        "try",
        "catch",
        "finally",
        "using",
        "lock",
        "switch",
    ];

    [GeneratedRegex(@"(?<![\w])Activate\s*(?:\(\s*\)|(?=\s*[),;]))")]
    private static partial Regex ActivateUse();

    [GeneratedRegex(@"(?<![\w])Topmost\s*=(?![=>])")]
    private static partial Regex TopmostAssignment();

    [GeneratedRegex(@"(?<![\w])ShowDialog\s*[<(]")]
    private static partial Regex ShowDialogCall();

    // Avalonia's Win32 backend activates a visible window on every WindowState change except to Minimized.
    [GeneratedRegex(@"(?<![\w])WindowState\s*=(?![=>])|(?<![\w])(?:SetValue|SetCurrentValue)\s*\(\s*(?:Window\.)?WindowStateProperty(?![\w])")]
    private static partial Regex WindowStateAssignment();

    // A window declared non-Normal in XAML is shown with SW_SHOWMAXIMIZED / SW_MINIMIZE, ignoring ShowActivated.
    [GeneratedRegex(@"(?<![\w])WindowState\s*=\s*""(?<state>Maximized|Minimized|FullScreen)""")]
    private static partial Regex AxamlNonNormalWindowState();

    // MessageBox.Avalonia's own show methods build an activated (and, for the dialog form, modal) window;
    // an IMsBox / MsBox<...> box is shown only through those methods (ShowAsync among them).
    [GeneratedRegex(
        @"(?<![\w])(?:MessageBoxManager|ShowWindowDialogAsync|ShowWindowAsync|ShowAsPopupAsync|MsBoxWindow|IMsBox)(?![\w])|(?<![\w])MsBox\s*<"
    )]
    private static partial Regex MessageBoxUse();

    // A receiver-less Close with a result: the value would reach only a ShowDialog caller.
    [GeneratedRegex(@"(?<![\w.])(?:this\.)?Close\s*\(\s*(?!\)|null\s*\))")]
    private static partial Regex ResultClose();

    // Methods (generic or not) and constructors: modifiers, an optional return type, the name, then "(".
    // No "new" modifier: a statement line such as "new WindowGeometryHelper(...)" would read as a member.
    [GeneratedRegex(
        @"^\s*(?:(?:public|private|internal|protected|static|override|async|sealed|virtual|partial|unsafe|extern|abstract)\s+)+(?:[\w<>\[\]?,. ]+?\s+)?(\w+)\s*(?:<[^>()]*>)?\s*\("
    )]
    private static partial Regex MemberSignature();

    [GeneratedRegex(@"^if\s*\(\s*(?:AutomationGate\.SuppressActivation|AutomationMode\.IsEnabled)\s*\)")]
    private static partial Regex EarlyReturnGuard();

    [Fact]
    public void NoUngatedActivateCalls()
    {
        string root = FindRepoRoot();
        List<string> violations = [];

        foreach (string dir in Directory.GetDirectories(Path.Combine(root, "src"), "Yaat.Client*"))
        {
            IEnumerable<string> files = Directory
                .EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal)) || (f.EndsWith(".axaml", StringComparison.Ordinal)));
            foreach (string file in files)
            {
                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains("/obj/") || relative.Contains("/bin/"))
                {
                    continue;
                }

                violations.AddRange(Scan(relative, File.ReadAllText(file)));
            }
        }

        Assert.True(
            violations.Count == 0,
            "Ungated activation, Topmost, ShowDialog or WindowState change, a bare result Close, a non-Normal XAML WindowState, "
                + "or a MessageBox.Avalonia use outside MessageBoxPresenter:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, violations)
        );
    }

    public static TheoryData<string, string, string, string?> ScannerCases() =>
        new()
        {
            {
                "gated if passes",
                "src/Yaat.Client/Views/MainWindow.axaml.cs",
                """
                        private void FocusActiveCommandInput()
                        {
                            if (!AutomationGate.SuppressActivation && (WindowState != WindowState.Minimized))
                            {
                                Activate();
                            }
                        }
                    """,
                null
            },
            {
                "early return passes",
                "src/Yaat.Client.Core/Views/WindowGroupRaiser.cs",
                """
                        private static void RaiseAll(Window activatedWindow)
                        {
                            if (AutomationGate.SuppressActivation)
                            {
                                return;
                            }

                            foreach (Window window in order)
                            {
                                window.Topmost = true;
                                window.Topmost = false;
                            }
                        }
                    """,
                null
            },
            {
                "else branch is flagged",
                "src/Yaat.Client/Views/MainWindow.axaml.cs",
                """
                        private void FocusActiveCommandInput()
                        {
                            if (!AutomationGate.SuppressActivation)
                            {
                                Focus();
                            }
                            else
                            {
                                Activate();
                            }
                        }
                    """,
                "else branch"
            },
            {
                "gate named only in a comment is flagged",
                "src/Yaat.Client/Views/MainWindow.axaml.cs",
                """
                        private void FocusActiveCommandInput()
                        {
                            // !AutomationGate.SuppressActivation is checked by the caller
                            Activate();
                        }
                    """,
                "no automation gate"
            },
            {
                "or-ed gate is flagged",
                "src/Yaat.Client/Views/MainWindow.axaml.cs",
                """
                        private void FocusActiveCommandInput()
                        {
                            if (!AutomationGate.SuppressActivation || force)
                            {
                                Activate();
                            }
                        }
                    """,
                "does not test"
            },
            {
                "constructor call is flagged",
                "src/Yaat.Client/Views/SomeWindow.axaml.cs",
                """
                        public SomeWindow()
                        {
                            InitializeComponent();
                            Activate();
                        }
                    """,
                "in SomeWindow is not in the allow-list"
            },
            {
                "generic method call is flagged",
                "src/Yaat.Client/Views/SomeWindow.axaml.cs",
                """
                        private static void Raise<T>(T window)
                            where T : Window
                        {
                            window.Activate();
                        }
                    """,
                "in Raise is not in the allow-list"
            },
            {
                "method group is flagged",
                "src/Yaat.Client/Views/SomeWindow.axaml.cs",
                """
                        private void Wire()
                        {
                            Opened += (_, _) => Dispatcher.UIThread.Post(Activate);
                        }
                    """,
                "Activate in Wire"
            },
            {
                "Topmost assignment is flagged",
                "src/Yaat.Client/Views/SomeWindow.axaml.cs",
                """
                        private void Pin()
                        {
                            Topmost = true;
                        }
                    """,
                "Topmost assignment in Pin"
            },
            {
                "raw generic ShowDialog is flagged",
                "src/Yaat.Client/Views/SomeWindow.axaml.cs",
                """
                        private async void OnPickClick(object? sender, RoutedEventArgs e)
                        {
                            string? result = await dialog.ShowDialog<string?>(this);
                        }
                    """,
                "ShowDialog in OnPickClick is not in the allow-list"
            },
            {
                "raw ShowDialog is flagged even when gated",
                "src/Yaat.Client/Views/SomeWindow.axaml.cs",
                """
                        private async void OnAboutClick(object? sender, RoutedEventArgs e)
                        {
                            if (!AutomationGate.SuppressActivation)
                            {
                                await about.ShowDialog(this);
                            }
                        }
                    """,
                "ShowDialog in OnAboutClick is not in the allow-list"
            },
            {
                "raw Close with a result is flagged",
                "src/Yaat.Client/Views/SomeWindow.axaml.cs",
                """
                        private void OnOkClick(object? sender, RoutedEventArgs e)
                        {
                            Close(BuildResult());
                        }
                    """,
                "Close(<result>) in OnOkClick: result dialogs must close through DialogPresenter.Close"
            },
            {
                "WindowState assignment outside the allow-list is flagged",
                "src/Yaat.Client/Views/SomeWindow.axaml.cs",
                """
                        private void OnMaximizeClick(object? sender, RoutedEventArgs e)
                        {
                            if (!AutomationGate.SuppressActivation)
                            {
                                WindowState = WindowState.Maximized;
                            }
                        }
                    """,
                "WindowState assignment in OnMaximizeClick is not in the allow-list"
            },
            {
                "ungated WindowState assignment in an allow-listed member is flagged",
                "src/Yaat.Client.Core/Views/WindowActivationExtensions.cs",
                """
                        public static void RestoreAndActivate(this Window window)
                        {
                            if (window.WindowState == WindowState.Minimized)
                            {
                                window.WindowState = WindowState.Normal;
                            }
                        }
                    """,
                "WindowState assignment in RestoreAndActivate: its nearest enclosing if does not test"
            },
            {
                "early-returned WindowState assignment and WindowState comparisons pass",
                "src/Yaat.Client.Core/Views/WindowGeometryHelper.cs",
                """
                        private void ApplySavedWindowState(SavedWindowGeometry geo, bool isStartupRestore)
                        {
                            if (AutomationGate.SuppressActivation)
                            {
                                return;
                            }

                            WindowState state = _window.WindowState;
                            _window.WindowState = geo.IsMaximized ? WindowState.Maximized : WindowState.Normal;
                        }
                    """,
                null
            },
            {
                "raw MessageBoxManager call is flagged",
                "src/Yaat.Client/Views/SomeWindow.axaml.cs",
                """
                        private async void OnDeleteClick(object? sender, RoutedEventArgs e)
                        {
                            IMsBox<ButtonResult> box = MessageBoxManager.GetMessageBoxStandard("Delete?", "Delete it?", ButtonEnum.YesNo);
                        }
                    """,
                "MessageBox.Avalonia use in OnDeleteClick: boxes must open through MessageBoxPresenter"
            },
            {
                "raw ShowWindowDialogAsync is flagged",
                "src/Yaat.Client/Views/SomeWindow.axaml.cs",
                """
                        private async Task AskAsync(IMsBox<string> box)
                        {
                            string choice = await box.ShowWindowDialogAsync(this);
                        }
                    """,
                "MessageBox.Avalonia use in AskAsync"
            },
            {
                "the message box seam may build an MsBoxWindow",
                "src/Yaat.Client/Views/MessageBoxPresenter.cs",
                """
                        private static async Task<T> ShowAsync<TView, TViewModel, T>(Window owner, TView view, TViewModel viewModel)
                        {
                            MsBoxWindow window = new() { Content = view, DataContext = viewModel };
                        }
                    """,
                null
            },
            {
                "IMsBox use and its ShowAsync are flagged",
                "src/Yaat.Client/Views/SomeWindow.axaml.cs",
                """
                        private async Task<ButtonResult> AskAsync(IMsBox<ButtonResult> box)
                        {
                            return await box.ShowAsync();
                        }
                    """,
                "MessageBox.Avalonia use in AskAsync"
            },
            {
                "a constructed MsBox is flagged",
                "src/Yaat.Client/Views/SomeWindow.axaml.cs",
                """
                        private Task<ButtonResult> ShowBox(MsBoxStandardView view, MsBoxStandardViewModel viewModel)
                        {
                            var box = new MsBox<MsBoxStandardView, MsBoxStandardViewModel, ButtonResult>(view, viewModel);
                        }
                    """,
                "MessageBox.Avalonia use in ShowBox"
            },
            {
                "SetValue of WindowStateProperty is flagged",
                "src/Yaat.Client/Views/SomeWindow.axaml.cs",
                """
                        private void OnMaximize()
                        {
                            SetValue(Window.WindowStateProperty, WindowState.Maximized);
                        }
                    """,
                "WindowState assignment in OnMaximize is not in the allow-list"
            },
            {
                "SetCurrentValue of WindowStateProperty is flagged",
                "src/Yaat.Client/Views/SomeWindow.axaml.cs",
                """
                        private static void Unminimize(Window window)
                        {
                            window.SetCurrentValue(Window.WindowStateProperty, WindowState.Normal);
                        }
                    """,
                "WindowState assignment in Unminimize is not in the allow-list"
            },
            {
                "a window declared Maximized in XAML is flagged",
                "src/Yaat.Client/Views/SomeWindow.axaml",
                """
                    <Window xmlns="https://github.com/avaloniaui" Title="Radar"
                            WindowState="Maximized">
                    </Window>
                    """,
                "SomeWindow.axaml:2 WindowState=\"Maximized\" in XAML"
            },
            {
                "a window declared Minimized in XAML is flagged",
                "src/Yaat.Client/Views/SomeWindow.axaml",
                """
                    <Window xmlns="https://github.com/avaloniaui" WindowState = "Minimized" />
                    """,
                "WindowState=\"Minimized\" in XAML"
            },
            {
                "a window declared Normal in XAML passes",
                "src/Yaat.Client/Views/SomeWindow.axaml",
                """
                    <Window xmlns="https://github.com/avaloniaui" WindowState="Normal" />
                    """,
                null
            },
            {
                "Close through the presenter, Close(null) and a Close member signature pass",
                "src/Yaat.Client/Views/SomeWindow.axaml.cs",
                """
                        public static void Close(MainViewModel vm)
                        {
                            DialogPresenter.Close(this, BuildResult());
                            Close(null);
                            Close();
                        }
                    """,
                null
            },
        };

    [Theory]
    [MemberData(nameof(ScannerCases))]
    public void Scan_FlagsUngatedUses(string caseName, string relativePath, string source, string? expectedFragment)
    {
        List<string> violations = Scan(relativePath, source);

        if (expectedFragment is null)
        {
            Assert.True(violations.Count == 0, $"{caseName}: " + string.Join(Environment.NewLine, violations));
        }
        else
        {
            Assert.True(violations.Any(v => v.Contains(expectedFragment)), $"{caseName}: got [{string.Join(" | ", violations)}]");
        }
    }

    /// <summary>Avalonia's picker is constructed only in the factory, so automation mode's injected picker can answer every dialog.</summary>
    [Fact]
    public void NoAvaloniaFilePickerConstructedOutsideFactory() =>
        AssertOnlyFileContains("new AvaloniaFilePickerService(", "src/Yaat.Client/Services/FilePickerFactory.cs");

    /// <summary>The storage provider is reached only from AvaloniaFilePickerService, so no call site bypasses the factory.</summary>
    [Fact]
    public void NoStorageProviderOutsideAvaloniaFilePickerService() =>
        AssertOnlyFileContains("StorageProvider.", "src/Yaat.Client/Services/AvaloniaFilePickerService.cs");

    // Asserts that "needle" appears in exactly one file under src/Yaat.Client, the allow-listed "allowed".
    private static void AssertOnlyFileContains(string needle, string allowed)
    {
        string root = FindRepoRoot();
        string clientRoot = Path.Combine(root, "src", "Yaat.Client");
        List<string> files = [];
        foreach (string file in Directory.EnumerateFiles(clientRoot, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/obj/") || relative.Contains("/bin/"))
            {
                continue;
            }

            if (File.ReadAllText(file).Contains(needle, StringComparison.Ordinal))
            {
                files.Add(relative);
            }
        }

        Assert.True((files.Count == 1) && (files[0] == allowed), $"Files containing '{needle}': {string.Join(", ", files)} (want only {allowed})");
    }

    /// <summary>
    /// Returns one violation line per ungated <c>Activate</c> use, <c>Topmost</c> assignment,
    /// <c>ShowDialog</c> call or <c>WindowState</c> change (an assignment, or <c>SetValue</c> /
    /// <c>SetCurrentValue</c> of <c>Window.WindowStateProperty</c>), per receiver-less
    /// <c>Close(result)</c>, and per MessageBox.Avalonia use outside <c>MessageBoxPresenter</c>, in
    /// <paramref name="source"/>. For an <c>.axaml</c> file it returns one line per window declared
    /// <c>WindowState="Maximized"</c>, <c>"Minimized"</c> or <c>"FullScreen"</c>.
    /// </summary>
    public static List<string> Scan(string relativePath, string source)
    {
        string[] lines = source.ReplaceLineEndings("\n").Split('\n');
        if (relativePath.EndsWith(".axaml", StringComparison.Ordinal))
        {
            return ScanAxaml(relativePath, lines);
        }

        List<string> violations = [];
        for (int i = 0; i < lines.Length; i++)
        {
            string code = CodeOf(lines[i]);
            if (ActivateUse().IsMatch(code))
            {
                CheckUse(relativePath, lines, i, "Activate", GatedActivateMembers, violations);
            }

            if (TopmostAssignment().IsMatch(code))
            {
                CheckUse(relativePath, lines, i, "Topmost assignment", GatedTopmostMembers, violations);
            }

            if (ShowDialogCall().IsMatch(code))
            {
                CheckUse(relativePath, lines, i, "ShowDialog", GatedShowDialogMembers, violations);
            }

            if (WindowStateAssignment().IsMatch(code))
            {
                CheckUse(relativePath, lines, i, "WindowState assignment", GatedWindowStateMembers, violations);
            }

            if (MessageBoxUse().IsMatch(code) && (relativePath != MessageBoxSeam))
            {
                (string? member, _) = FindEnclosingMember(lines, i);
                violations.Add(
                    $"{relativePath}:{i + 1} MessageBox.Avalonia use in {member ?? "<unknown member>"}: boxes must open through MessageBoxPresenter"
                );
            }

            if (ResultClose().IsMatch(code) && !MemberSignature().IsMatch(code))
            {
                (string? member, _) = FindEnclosingMember(lines, i);
                violations.Add(
                    $"{relativePath}:{i + 1} Close(<result>) in {member ?? "<unknown member>"}: result dialogs must close through DialogPresenter.Close"
                );
            }
        }

        return violations;
    }

    private static List<string> ScanAxaml(string relativePath, string[] lines)
    {
        List<string> violations = [];
        for (int i = 0; i < lines.Length; i++)
        {
            Match match = AxamlNonNormalWindowState().Match(lines[i]);
            if (match.Success)
            {
                violations.Add(
                    $"{relativePath}:{i + 1} WindowState=\"{match.Groups["state"].Value}\" in XAML: a window first shown non-Normal "
                        + "activates even with ShowActivated off; leave it Normal and let WindowGeometryHelper restore the saved state"
                );
            }
        }

        return violations;
    }

    private static void CheckUse(string relativePath, string[] lines, int useLine, string kind, HashSet<string> allowList, List<string> violations)
    {
        (string? member, int signatureLine) = FindEnclosingMember(lines, useLine);
        string where = $"{relativePath}:{useLine + 1} {kind} in {member ?? "<unknown member>"}";
        if (!allowList.Contains($"{relativePath}::{member}"))
        {
            violations.Add($"{where} is not in the allow-list");
            return;
        }

        string? failure = GateFailure(lines, signatureLine, useLine);
        if (failure is not null)
        {
            violations.Add($"{where}: {failure}");
        }
    }

    private static string? GateFailure(string[] lines, int signatureLine, int useLine)
    {
        if (HasEarlyReturnGuard(lines, signatureLine, useLine))
        {
            return null;
        }

        int pendingCloses = 0;
        for (int i = useLine - 1; i > signatureLine; i--)
        {
            string code = CodeOf(lines[i]);
            pendingCloses += code.Count(c => c == '}') - code.Count(c => c == '{');
            if (pendingCloses >= 0)
            {
                continue;
            }

            // This line opens a block that encloses the use; classify the block by its header.
            pendingCloses = 0;
            (string header, int headerLine) = BlockHeader(lines, i, signatureLine);
            if (header.StartsWith("else", StringComparison.Ordinal) && !header.StartsWith("else if", StringComparison.Ordinal))
            {
                return "sits in an else branch";
            }

            if (header.StartsWith("if", StringComparison.Ordinal) || header.StartsWith("else if", StringComparison.Ordinal))
            {
                bool gated = NegatedGateTokens.Any(header.Contains) && !header.Contains("||");
                return gated ? null : $"its nearest enclosing if does not test {string.Join(" or ", NegatedGateTokens)} (alone or with &&)";
            }

            i = headerLine;
        }

        return "has no automation gate before it";
    }

    // The header of the block opened at openLine: the code before "{" on that line, or the
    // statement lines above it back to the one that starts with a block keyword.
    private static (string Header, int Line) BlockHeader(string[] lines, int openLine, int signatureLine)
    {
        string onLine = CodeOf(lines[openLine]).Trim();
        string beforeBrace = onLine[..onLine.IndexOf('{')].Trim();
        if (beforeBrace.Length > 0)
        {
            return (beforeBrace, openLine);
        }

        List<string> parts = [];
        for (int j = openLine - 1; j > signatureLine; j--)
        {
            string code = CodeOf(lines[j]).Trim();
            if (code.Length == 0)
            {
                continue;
            }

            parts.Insert(0, code);
            if (
                BlockKeywords.Any(k =>
                    code == k || code.StartsWith(k + " ", StringComparison.Ordinal) || code.StartsWith(k + "(", StringComparison.Ordinal)
                )
            )
            {
                return (string.Join(" ", parts), j);
            }

            if (code.EndsWith(';') || code.EndsWith('}') || code.EndsWith('{'))
            {
                break;
            }
        }

        return (string.Join(" ", parts), openLine - 1);
    }

    // True when the member body, at its top level and before the use, returns early on the gate.
    private static bool HasEarlyReturnGuard(string[] lines, int signatureLine, int useLine)
    {
        int depth = 0;
        for (int i = signatureLine; i < useLine; i++)
        {
            string code = CodeOf(lines[i]).Trim();
            if ((depth == 1) && EarlyReturnGuard().IsMatch(code))
            {
                IEnumerable<string> next = lines[(i + 1)..useLine].Select(l => CodeOf(l).Trim()).Where(l => l.Length > 0).Take(3);
                if (next.Any(l => l.StartsWith("return", StringComparison.Ordinal)))
                {
                    return true;
                }
            }

            depth += code.Count(c => c == '{') - code.Count(c => c == '}');
        }

        return false;
    }

    private static (string? Member, int Line) FindEnclosingMember(string[] lines, int useLine)
    {
        for (int i = useLine; i >= 0; i--)
        {
            Match match = MemberSignature().Match(CodeOf(lines[i]));
            if (match.Success)
            {
                return (match.Groups[1].Value, i);
            }
        }

        return (null, 0);
    }

    // The line's code, or empty for a comment line, so a gate named only in a comment never counts.
    private static string CodeOf(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("*", StringComparison.Ordinal) ? string.Empty : line;
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "yaat.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException($"yaat.slnx not found above {AppContext.BaseDirectory}");
    }
}
