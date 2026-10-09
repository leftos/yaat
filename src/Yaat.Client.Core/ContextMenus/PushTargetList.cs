namespace Yaat.Client.ContextMenus;

/// <summary>
/// The Push back to… targets of one aircraft, which may still be filling: a host answers at once with what it has (the
/// shipped seed, or nothing while it computes) and replaces it in place when its background live plan lands, raising
/// <see cref="Changed"/> on the UI thread. A list built <see cref="Ready"/> is settled from the start: nothing is being
/// or will be planned for it.
/// </summary>
public sealed class PushTargetList
{
    private readonly TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private PushTargetList(IReadOnlyList<MenuPushTarget> targets, bool computing)
    {
        Targets = targets;
        Computing = computing;
    }

    /// <summary>The targets to show now.</summary>
    public IReadOnlyList<MenuPushTarget> Targets { get; private set; }

    /// <summary>True while there is nothing to show yet because the live plan has not landed and there was no seed.</summary>
    public bool Computing { get; private set; }

    /// <summary>Completes once the live plan has been applied, or at once for a list that has none to wait for.</summary>
    public Task Settled => _settled.Task;

    /// <summary>Raised on the UI thread when the live plan replaces <see cref="Targets"/>.</summary>
    public event Action? Changed;

    /// <summary>A settled list over <paramref name="targets"/>: no live plan follows.</summary>
    /// <param name="targets">The targets.</param>
    /// <returns>The list.</returns>
    public static PushTargetList Ready(IReadOnlyList<MenuPushTarget> targets)
    {
        var list = new PushTargetList(targets, computing: false);
        list._settled.SetResult();
        return list;
    }

    /// <summary>
    /// A list a live plan will fill: it shows <paramref name="seed"/> until then, or is <see cref="Computing"/> when
    /// there is no seed.
    /// </summary>
    /// <param name="seed">The seed's targets, already checked against the aircraft about; null when there is no seed.</param>
    /// <returns>The list.</returns>
    public static PushTargetList Pending(IReadOnlyList<MenuPushTarget>? seed) => new(seed ?? [], computing: seed is null);

    /// <summary>
    /// Replaces the targets with the live plan's, ends <see cref="Computing"/>, raises <see cref="Changed"/> and settles
    /// the list. Called once, on the UI thread.
    /// </summary>
    /// <param name="targets">The live plan's targets.</param>
    /// <exception cref="InvalidOperationException">The list is already settled.</exception>
    internal void Apply(IReadOnlyList<MenuPushTarget> targets)
    {
        if (_settled.Task.IsCompleted)
        {
            throw new InvalidOperationException("This push-target list is already settled; a live plan is applied to it once.");
        }

        Targets = targets;
        Computing = false;
        Changed?.Invoke();
        _settled.SetResult();
    }
}
