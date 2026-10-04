namespace Yaat.Client.ContextMenus;

/// <summary>
/// An int value with the text a list popup shows for it, such as an altitude listed as <c>FL350</c>. The catalog's
/// list pickers unwrap it to the value before the pick reaches the command.
/// </summary>
internal sealed record MenuLabeledValue(string Label, int Value)
{
    public override string ToString() => Label;
}
