using System.Text.RegularExpressions;
using Xunit;

namespace Yaat.Client.UI.Tests;

/// <summary>
/// Automation mode must never activate a window or pin one on top, so every client
/// <c>Activate()</c> call (or <c>Activate</c> method group) and every <c>Topmost</c> assignment
/// has to sit in an allow-listed member, under a gate: either its nearest enclosing <c>if</c>
/// tests <c>!AutomationGate.SuppressActivation</c> / <c>!AutomationMode.IsEnabled</c>, or the
/// member returns early on <c>if (AutomationGate.SuppressActivation)</c> /
/// <c>if (AutomationMode.IsEnabled)</c> before the call.
/// </summary>
public partial class AutomationModeSourceTests
{
    // "relative/path.cs::Member" for each member allowed to call Activate() behind the gate.
    private static readonly HashSet<string> GatedActivateMembers =
    [
        "src/Yaat.Client.Core/Views/WindowActivationExtensions.cs::RestoreAndActivate",
        "src/Yaat.Client.Core/Views/WindowGeometryHelper.cs::ApplyGeometryToWindow",
        "src/Yaat.Client/Views/MainWindow.axaml.cs::ReclaimFocusAfterProfileApply",
        "src/Yaat.Client/Views/MainWindow.axaml.cs::FocusActiveCommandInput",
        "src/Yaat.Client/Views/TerminalWindow.axaml.cs::FocusCommandInput",
    ];

    // "relative/path.cs::Member" for each member allowed to assign Window.Topmost behind the gate.
    private static readonly HashSet<string> GatedTopmostMembers =
    [
        "src/Yaat.Client.Core/Views/WindowGeometryHelper.cs::SetTopmost",
        "src/Yaat.Client.Core/Views/WindowGroupRaiser.cs::RaiseAll",
    ];

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
            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains("/obj/") || relative.Contains("/bin/"))
                {
                    continue;
                }

                violations.AddRange(Scan(relative, File.ReadAllText(file)));
            }
        }

        Assert.True(violations.Count == 0, "Ungated activation or Topmost:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
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

    /// <summary>Returns one violation line per ungated <c>Activate</c> use or <c>Topmost</c> assignment in <paramref name="source"/>.</summary>
    public static List<string> Scan(string relativePath, string source)
    {
        string[] lines = source.ReplaceLineEndings("\n").Split('\n');
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
