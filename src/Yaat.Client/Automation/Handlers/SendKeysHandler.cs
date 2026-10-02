using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.Automation.Tree;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.Automation.Handlers;

/// <summary>
/// <c>send_keys</c>: keystrokes in the client driver's SendKeys syntax (<see cref="SendKeysParser"/>) to the focused
/// element. A typed character is a text-input event, so a TextBox inserts it at the caret and per-keystroke handlers run;
/// a key is a key-down then a key-up with its modifiers, which route to the window's hotkeys and <c>OnKeyDown</c>
/// overrides. Each stroke goes to the element focused at that moment, in its own UI-thread turn.
/// <list type="bullet">
/// <item>Params: <c>keys</c>; optionally the target as <c>nodeId</c> or <c>selector</c>, focused first (the keys go to
/// it alone when it cannot take the focus). Without one, the focused element of the active window, else of the first
/// window that has one.</item>
/// <item>A malformed string is <c>INVALID_PARAM</c> with the fault's position; a key matching the push-to-talk binding is
/// <c>UNSUPPORTED_OPERATION</c>; a disabled target is <c>ELEMENT_DISABLED</c>. All are refused before any stroke is sent
/// and before a given target is focused.</item>
/// </list>
/// </summary>
public sealed class SendKeysHandler(NodeRegistry registry, TargetResolver targets) : IRequestHandler
{
    private const string KeysHint =
        "Use SendKeys syntax: plain characters, {ENTER} {ESC} {TAB} {BS} {DEL} {HOME} {END} {LEFT} {RIGHT} {UP} {DOWN} {F1}-{F12}, "
        + "^ (Ctrl), + (Shift) and % (Alt) before a key, letter or digit, and a literal + ^ % ~ ( ) { } [ ] in braces.";

    private sealed record SendKeysParams(ElementTarget Target, string Keys, List<KeyStroke> Strokes);

    /// <summary>
    /// Where the strokes go: the focused element of <see cref="Root"/>, or <see cref="Initial"/> alone when focus is not
    /// followed. <see cref="NodeId"/> is <see cref="Initial"/>'s, registered on the UI thread.
    /// </summary>
    private sealed record KeyTarget(TopLevel Root, Interactive Initial, int NodeId, bool FollowFocus);

    public string Method => ProtocolMethods.SendKeys;

    public async Task<object> Handle(AutomationRequest request, CancellationToken cancellationToken)
    {
        object parsed = ParseParams(request.Params);
        if (parsed is not SendKeysParams parameters)
        {
            return parsed;
        }

        object prepared = await Dispatcher.UIThread.InvokeAsync(() => Prepare(parameters));
        if (prepared is not KeyTarget target)
        {
            return prepared;
        }

        foreach (KeyStroke stroke in parameters.Strokes)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Send(target, stroke), DispatcherPriority.Input);
        }

        return new SendKeysResult(target.NodeId, parameters.Strokes.Count);
    }

    private static object ParseParams(JsonElement? raw)
    {
        (JsonElement element, HandlerErrorResult? objectError) = InputParams.RequireObject(raw);
        if (objectError is not null)
        {
            return objectError;
        }

        (ElementTarget target, HandlerErrorResult? targetError) = InputParams.ReadTarget(element, "nodeId", "selector");
        (string keys, HandlerErrorResult? keysError) = InputParams.ReadRequiredString(element, "keys");
        HandlerErrorResult? error = targetError ?? keysError;
        if (error is not null)
        {
            return error;
        }

        if (keys.Length == 0)
        {
            return HandlerResult.InvalidParam("keys", "'keys' is empty.");
        }

        if (!SendKeysParser.TryParse(keys, out List<KeyStroke>? strokes, out KeysFault? fault))
        {
            return HandlerResult.Error(
                AutomationErrorCodes.InvalidParam,
                $"Malformed 'keys': {fault.Message}",
                KeysHint,
                new KeysErrorDetails(keys, fault.Position)
            );
        }

        return new SendKeysParams(target, keys, strokes);
    }

    /// <summary>
    /// The <see cref="KeyTarget"/>, or the error refusing the request. Every refusal is decided before a given target is
    /// focused, so a refused request moves nothing.
    /// </summary>
    private object Prepare(SendKeysParams parameters)
    {
        object resolved = parameters.Target.IsGiven ? ResolveTarget(parameters.Target) : FindFocused();
        if (resolved is not KeyTarget target)
        {
            return resolved;
        }

        HandlerErrorResult? refusal = PushToTalkRefusal(target.Root, parameters);
        if (refusal is not null)
        {
            return refusal;
        }

        return parameters.Target.IsGiven ? FocusTarget(target) : target;
    }

    private object ResolveTarget(ElementTarget elementTarget)
    {
        if (!targets.TryResolve(elementTarget, out Visual? visual, out HandlerErrorResult? error))
        {
            return error;
        }

        if ((visual is not InputElement element) || (TopLevel.GetTopLevel(visual) is not { } root))
        {
            return HandlerResult.Unsupported(ProtocolMethods.SendKeys, visual.GetType().Name);
        }

        int nodeId = registry.GetOrRegister(element);
        if (!element.IsEffectivelyEnabled)
        {
            return HandlerResult.ElementDisabled(nodeId, element.GetType().Name);
        }

        return new KeyTarget(root, element, nodeId, false);
    }

    /// <summary>Focuses a given target; the strokes follow the focus from there when it took it, else go to it alone.</summary>
    private static KeyTarget FocusTarget(KeyTarget target)
    {
        bool focused = (target.Initial is InputElement { Focusable: true } element) && element.Focus();
        return target with { FollowFocus = focused };
    }

    private object FindFocused()
    {
        IEnumerable<TopLevel> roots = registry.GetRoots().OrderByDescending(root => root is WindowBase { IsActive: true });
        foreach (TopLevel root in roots)
        {
            if (root.FocusManager?.GetFocusedElement() is Interactive focused)
            {
                return new KeyTarget(root, focused, registry.GetOrRegister(focused), true);
            }
        }

        return HandlerResult.InvalidParam(
            "selector",
            "No element has the keyboard focus. Give the target as 'nodeId' or 'selector', or focus one first."
        );
    }

    /// <summary>The refusal for a key matching the push-to-talk binding, which automation does not send; null when none does.</summary>
    private static HandlerErrorResult? PushToTalkRefusal(TopLevel root, SendKeysParams parameters)
    {
        MainViewModel? vm = WindowHotkeys.ResolveMainViewModel(root);
        if ((vm is null) || !KeybindHelper.ParseKeybind(vm.Preferences.PttKey, out Key pttKey, out KeyModifiers pttModifiers))
        {
            return null;
        }

        // The binding matches as MainWindow matches it: a modifier-only key (the RightCtrl default) by the key alone.
        bool matches = parameters.Strokes.Any(stroke =>
            (stroke.Text is null) && KeybindHelper.MatchesKeybind(stroke.Key, stroke.Modifiers, pttKey, pttModifiers)
        );
        if (!matches)
        {
            return null;
        }

        return HandlerResult.Error(
            AutomationErrorCodes.UnsupportedOperation,
            $"'{parameters.Keys}' holds the push-to-talk binding ({vm.Preferences.PttKey}); no keystroke was sent.",
            "Push-to-talk is out of reach in automation mode.",
            new UnsupportedOperationDetails(ProtocolMethods.SendKeys, "push-to-talk")
        );
    }

    private static void Send(KeyTarget target, KeyStroke stroke)
    {
        if (stroke.Text is not null)
        {
            Current(target).RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = stroke.Text });
            return;
        }

        Current(target)
            .RaiseEvent(
                new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = stroke.Key,
                    KeyModifiers = stroke.Modifiers,
                }
            );
        Current(target)
            .RaiseEvent(
                new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyUpEvent,
                    Key = stroke.Key,
                    KeyModifiers = stroke.Modifiers,
                }
            );
    }

    private static Interactive Current(KeyTarget target) =>
        (target.FollowFocus && (target.Root.FocusManager?.GetFocusedElement() is Interactive focused)) ? focused : target.Initial;
}
