using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.Automation.Tree;

namespace Yaat.Client.Automation.Handlers;

/// <summary>
/// <c>set_text</c>: replaces a TextBox's text and puts the caret at its end. The target (<c>nodeId</c> or
/// <c>selector</c>) is the TextBox or an element holding one, whose first TextBox is written; anything else is
/// <c>INVALID_PARAM</c>; a disabled or read-only TextBox is <c>ELEMENT_DISABLED</c>. Params: the target and <c>text</c>.
/// </summary>
public sealed class SetTextHandler(NodeRegistry registry, TargetResolver targets) : IRequestHandler
{
    private sealed record SetTextParams(ElementTarget Target, string Text);

    public string Method => ProtocolMethods.SetText;

    public async Task<object> Handle(AutomationRequest request, CancellationToken cancellationToken)
    {
        (JsonElement element, HandlerErrorResult? objectError) = InputParams.RequireObject(request.Params);
        if (objectError is not null)
        {
            return objectError;
        }

        (ElementTarget target, HandlerErrorResult? targetError) = InputParams.ReadTarget(element, "nodeId", "selector");
        (string text, HandlerErrorResult? textError) = InputParams.ReadRequiredString(element, "text");
        HandlerErrorResult? error = targetError ?? textError;
        if (error is not null)
        {
            return error;
        }

        return await Dispatcher.UIThread.InvokeAsync(() => SetText(new SetTextParams(target, text)));
    }

    private object SetText(SetTextParams parameters)
    {
        if (!targets.TryResolve(parameters.Target, out Visual? visual, out HandlerErrorResult? error))
        {
            return error;
        }

        TextBox? textBox = (visual as TextBox) ?? visual.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
        if (textBox is null)
        {
            string param = parameters.Target.ParamName;
            return HandlerResult.InvalidParam(param, $"'{param}' names a {visual.GetType().Name}, which is not a TextBox and holds none.");
        }

        if (!textBox.IsEffectivelyEnabled)
        {
            return HandlerResult.ElementDisabled(registry.GetOrRegister(textBox), textBox.GetType().Name);
        }

        if (textBox.IsReadOnly)
        {
            return HandlerResult.ElementDisabled(registry.GetOrRegister(textBox), textBox.GetType().Name, "It is read-only.");
        }

        textBox.Text = parameters.Text;
        textBox.CaretIndex = parameters.Text.Length;
        textBox.ClearSelection();
        return new SetTextResult(registry.GetOrRegister(textBox), textBox.Text ?? "");
    }
}
