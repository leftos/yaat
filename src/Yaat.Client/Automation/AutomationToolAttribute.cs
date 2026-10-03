namespace Yaat.Client.Automation;

/// <summary>
/// Marks a public method of <see cref="Tools.AutomationTools"/> as an app tool the automation pipe lists (<c>list_app_tools</c>)
/// and calls by <paramref name="name"/> (<c>call_app_tool</c>). The method returns <c>Task&lt;AppToolOutcome&gt;</c>, and every
/// parameter is required, of type <c>string</c>, <c>int</c>, <c>double</c> or <c>bool</c>, and carries a
/// <see cref="System.ComponentModel.DescriptionAttribute"/>; the parameter's own name is its argument name.
/// </summary>
/// <param name="name">The tool's snake_case name, e.g. <c>set_sim_rate</c>.</param>
/// <param name="description">One line saying what the tool does.</param>
/// <param name="availability">
/// The name of the public parameterless <see cref="Tools.AutomationTools"/> method that returns why the tool cannot run now, or
/// null when it can.
/// </param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class AutomationToolAttribute(string name, string description, string availability) : Attribute
{
    public string Name { get; } = name;

    public string Description { get; } = description;

    public string Availability { get; } = availability;
}
