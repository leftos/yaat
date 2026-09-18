using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using Rect = System.Windows.Rect;

namespace Yaat.ClientDriver.Mcp;

/// <summary>Finding, describing and walking UI Automation elements — the read side every tool shares.</summary>
internal static class UiaQuery
{
    private static readonly Dictionary<string, ControlType> ControlTypesByName = BuildControlTypes();

    /// <summary>One line per element: registry id, control type, name, automation id, enabled state and screen rectangle.</summary>
    internal static string Describe(AutomationElement element, string id)
    {
        AutomationElement.AutomationElementInformation info = element.Current;
        string identity = $"{id} | {info.ControlType.ProgrammaticName} | {info.Name}";
        return $"{identity} | id={info.AutomationId} | enabled={info.IsEnabled} | rect={FormatRect(info.BoundingRectangle)}";
    }

    internal static string FormatRect(Rect rect)
    {
        if (rect.IsEmpty || double.IsInfinity(rect.X) || double.IsInfinity(rect.Y))
        {
            return "offscreen";
        }

        return $"({rect.X:0},{rect.Y:0} {rect.Width:0}x{rect.Height:0})";
    }

    /// <summary>Turns the ways UI Automation fails mid-call into messages the agent can act on, logging each cause to stderr.</summary>
    internal static T Guarded<T>(ILogger logger, string tool, string subject, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (ElementNotAvailableException ex)
        {
            logger.LogDebug(ex, "{Tool} touched an element that no longer exists: {Subject}", tool, subject);
            throw new McpException(
                $"{tool} on '{subject}': the element disappeared mid-call — its window or process closed. Call list_windows or find_elements again for fresh ids"
            );
        }
        catch (ElementNotEnabledException ex)
        {
            logger.LogDebug(ex, "{Tool} acted on a disabled element: {Subject}", tool, subject);
            throw new McpException($"{tool} on '{subject}': the element is disabled — it accepts no input until the application enables it");
        }
        catch (COMException ex)
        {
            logger.LogDebug(ex, "{Tool} hit a UI Automation provider failure on {Subject}", tool, subject);
            throw new McpException(
                $"{tool} on '{subject}': the UI Automation provider did not respond: 0x{ex.HResult:X8}. The target is busy, shutting down, or running at a higher integrity level"
            );
        }
    }

    internal static List<AutomationElement> TopLevelWindows(int processId)
    {
        PropertyCondition condition = new(AutomationElement.ProcessIdProperty, processId);
        AutomationElementCollection found = AutomationElement.RootElement.FindAll(TreeScope.Children, condition);
        List<AutomationElement> windows = [];
        foreach (AutomationElement window in found)
        {
            windows.Add(window);
        }

        return windows;
    }

    internal static List<AutomationElement> FindDescendants(AutomationElement root, string name, string automationId, string controlType, int limit)
    {
        Condition condition = BuildCondition(name, automationId, controlType);
        AutomationElementCollection found = root.FindAll(TreeScope.Descendants, condition);
        List<AutomationElement> matches = [];
        foreach (AutomationElement element in found)
        {
            if (matches.Count >= limit)
            {
                break;
            }

            matches.Add(element);
        }

        return matches;
    }

    /// <summary>Control-view walk from an element, one indented line per node, stopping at the depth or line cap.</summary>
    internal static string DumpTree(AutomationElement root, int maxDepth, int maxLines, ElementRegistry registry)
    {
        List<string> lines = [];
        if (Walk(root, 0))
        {
            lines.Add($"… truncated at {maxLines} lines — narrow the walk with a deeper element id or a smaller maxDepth");
        }

        return string.Join(Environment.NewLine, lines);

        bool Walk(AutomationElement element, int depth)
        {
            if (lines.Count >= maxLines)
            {
                return true;
            }

            lines.Add(new string(' ', depth * 2) + Describe(element, registry.Register(element)));
            if (depth >= maxDepth)
            {
                return false;
            }

            TreeWalker walker = TreeWalker.ControlViewWalker;
            AutomationElement? child = walker.GetFirstChild(element);
            while (child is not null)
            {
                if (Walk(child, depth + 1))
                {
                    return true;
                }

                child = walker.GetNextSibling(child);
            }

            return false;
        }
    }

    /// <summary>The window an element belongs to: the ancestor whose parent is the desktop.</summary>
    internal static AutomationElement TopLevelWindowOf(AutomationElement element)
    {
        AutomationElement desktop = AutomationElement.RootElement;
        if (Automation.Compare(element, desktop))
        {
            return element;
        }

        TreeWalker walker = TreeWalker.ControlViewWalker;
        AutomationElement current = element;
        AutomationElement? parent = walker.GetParent(current);
        while ((parent is not null) && !Automation.Compare(parent, desktop))
        {
            current = parent;
            parent = walker.GetParent(current);
        }

        return current;
    }

    internal static nint WindowHandle(AutomationElement element) => element.Current.NativeWindowHandle;

    internal static ControlType ParseControlType(string suffix)
    {
        if (ControlTypesByName.TryGetValue(suffix, out ControlType? controlType))
        {
            return controlType;
        }

        string valid = string.Join(", ", ControlTypesByName.Keys.OrderBy(key => key, StringComparer.Ordinal));
        throw new McpException($"Unknown controlType '{suffix}'. Valid values: {valid}");
    }

    private static Condition BuildCondition(string name, string automationId, string controlType)
    {
        List<Condition> conditions = [];
        if (!string.IsNullOrEmpty(name))
        {
            conditions.Add(new PropertyCondition(AutomationElement.NameProperty, name));
        }

        if (!string.IsNullOrEmpty(automationId))
        {
            conditions.Add(new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
        }

        if (!string.IsNullOrEmpty(controlType))
        {
            conditions.Add(new PropertyCondition(AutomationElement.ControlTypeProperty, ParseControlType(controlType)));
        }

        return conditions.Count switch
        {
            0 => Condition.TrueCondition,
            1 => conditions[0],
            _ => new AndCondition([.. conditions]),
        };
    }

    private static Dictionary<string, ControlType> BuildControlTypes()
    {
        Dictionary<string, ControlType> map = new(StringComparer.OrdinalIgnoreCase);
        foreach (FieldInfo field in typeof(ControlType).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is ControlType controlType)
            {
                map[field.Name] = controlType;
            }
        }

        return map;
    }
}
