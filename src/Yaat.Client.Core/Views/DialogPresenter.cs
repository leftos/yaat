using System.Runtime.CompilerServices;
using Avalonia.Controls;

namespace Yaat.Client.Views;

/// <summary>
/// Opens every client dialog. Outside automation mode it is plain <see cref="Window.ShowDialog(Window)"/>.
/// In automation mode a modal dialog would activate itself and re-activate its owner on close, so the
/// dialog shows never-activated and non-modal instead, and the owner is locked by hand while any of
/// its dialogs is open: it is disabled, and the user's request to close it (its title bar) is
/// cancelled, as with a modal dialog. Code calling <see cref="Window.Close()"/>, application
/// shutdown and the owner's own owner closing still go through, closing the dialogs with it. A dialog that returns a value closes through
/// <see cref="Close"/>, because Avalonia hands the value given to <see cref="Window.Close(object?)"/>
/// only to a <c>ShowDialog</c> caller.
/// </summary>
public static class DialogPresenter
{
    private static readonly ConditionalWeakTable<Window, StrongBox<object?>> Results = [];

    private static readonly ConditionalWeakTable<Window, OwnerLock> OwnerLocks = [];

    /// <summary>Shows <paramref name="dialog"/> over <paramref name="owner"/> and completes when it closes.</summary>
    public static Task ShowModalAsync(Window dialog, Window owner)
    {
        if (!AutomationGate.SuppressActivation)
        {
            return dialog.ShowDialog(owner);
        }

        return ShowModeless<object>(dialog, owner);
    }

    /// <summary>
    /// Shows <paramref name="dialog"/> over <paramref name="owner"/> and completes with the value the
    /// dialog passed to <see cref="Close"/>, or <c>default</c> when it closed without one.
    /// </summary>
    public static Task<T?> ShowModalAsync<T>(Window dialog, Window owner)
    {
        if (!AutomationGate.SuppressActivation)
        {
            return dialog.ShowDialog<T?>(owner);
        }

        return ShowModeless<T>(dialog, owner);
    }

    /// <summary>
    /// Closes <paramref name="dialog"/> with <paramref name="result"/>, which <see cref="ShowModalAsync{T}"/>
    /// returns in either mode. Dialogs call this instead of <see cref="Window.Close(object?)"/>.
    /// </summary>
    public static void Close(Window dialog, object? result)
    {
        Results.AddOrUpdate(dialog, new StrongBox<object?>(result));
        dialog.Close(result);
    }

    private static Task<T?> ShowModeless<T>(Window dialog, Window owner)
    {
        TaskCompletionSource<T?> completion = new();
        dialog.ShowActivated = false;
        dialog.Closed += OnClosed;
        LockOwner(owner);
        try
        {
            dialog.Show(owner);
        }
        catch
        {
            dialog.Closed -= OnClosed;
            UnlockOwner(owner);
            throw;
        }

        return completion.Task;

        void OnClosed(object? sender, EventArgs e)
        {
            dialog.Closed -= OnClosed;
            UnlockOwner(owner);
            try
            {
                completion.SetResult(TakeResult<T>(dialog));
            }
            catch (InvalidCastException ex)
            {
                completion.SetException(ex);
            }
        }
    }

    // The first open dialog records the owner's enabled state, disables it and starts cancelling
    // its close requests; the last one to close undoes all three, so overlapping dialogs closing in
    // any order leave the owner as it was.
    private static void LockOwner(Window owner)
    {
        OwnerLock ownerLock = OwnerLocks.GetOrCreateValue(owner);
        if (ownerLock.Count == 0)
        {
            ownerLock.WasEnabled = owner.IsEnabled;
            owner.IsEnabled = false;
            owner.Closing += CancelOwnerClose;
        }

        ownerLock.Count++;
    }

    private static void UnlockOwner(Window owner)
    {
        if (!OwnerLocks.TryGetValue(owner, out OwnerLock? ownerLock) || (ownerLock.Count == 0))
        {
            throw new InvalidOperationException($"DialogPresenter: unlocking owner '{owner.Title}' that holds no dialog lock.");
        }

        ownerLock.Count--;
        if (ownerLock.Count == 0)
        {
            owner.Closing -= CancelOwnerClose;
            owner.IsEnabled = ownerLock.WasEnabled;
            OwnerLocks.Remove(owner);
        }
    }

    /// <summary>
    /// True when an owner with an open dialog must refuse to close: only the user's own close request
    /// (its title bar). Code calling <see cref="Window.Close()"/>, application or OS shutdown and the
    /// owner's own owner closing all go through, as they do past a modal <c>ShowDialog</c>.
    /// </summary>
    public static bool ShouldCancelOwnerClose(WindowCloseReason reason, bool isProgrammatic) =>
        (reason == WindowCloseReason.WindowClosing) && !isProgrammatic;

    private static void CancelOwnerClose(object? sender, WindowClosingEventArgs e)
    {
        if (ShouldCancelOwnerClose(e.CloseReason, e.IsProgrammatic))
        {
            e.Cancel = true;
        }
    }

    private static T? TakeResult<T>(Window dialog)
    {
        if (!Results.TryGetValue(dialog, out StrongBox<object?>? box))
        {
            return default;
        }

        Results.Remove(dialog);
        return box.Value is null ? default : (T)box.Value;
    }

    private sealed class OwnerLock
    {
        public int Count { get; set; }

        public bool WasEnabled { get; set; }
    }
}
