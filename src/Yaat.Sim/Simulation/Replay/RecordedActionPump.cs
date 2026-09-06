namespace Yaat.Sim.Simulation.Replay;

/// <summary>
/// Walks a recorded action log forward in step with the spine — the one pump behind a Sim replay, the server's
/// reconstruction and its tape playback. Two passes per second: <see cref="ApplyPreTick"/> lands the actions that
/// happened in pre-physics live (aircraft spawns, live-traffic samples — <see cref="SimulationEngine.IsPreTickAction"/>)
/// after the clock increment and before physics, without moving the cursor; <see cref="ApplyThrough"/> then applies
/// everything else at or before the completed second and advances the cursor past all of it, skipping what the
/// pre-tick pass already did. <see cref="SeekTo"/> repositions after a jump in time (a fresh load, a snapshot restore,
/// a fast-forward); <see cref="Reseat"/> adopts a cursor kept elsewhere (the room's playback cursor) and forgets the
/// pre-tick bookkeeping when it moved.
/// </summary>
public sealed class RecordedActionPump(List<RecordedAction> actions)
{
    private readonly HashSet<int> _preTickApplied = [];

    public List<RecordedAction> Actions { get; } = actions;

    /// <summary>Index of the next action <see cref="ApplyThrough"/> has not applied.</summary>
    public int Cursor { get; private set; }

    /// <summary>Points the cursor just past every action at or before <paramref name="seconds"/>, so only later ones are pending.</summary>
    public void SeekTo(double seconds)
    {
        Cursor = 0;
        _preTickApplied.Clear();
        while ((Cursor < Actions.Count) && (Actions[Cursor].ElapsedSeconds <= seconds))
        {
            Cursor++;
        }
    }

    /// <summary>Adopts a cursor decided elsewhere; a cursor that moved discards the pre-tick bookkeeping, which indexed the old position.</summary>
    public void Reseat(int cursor)
    {
        if (cursor == Cursor)
        {
            return;
        }

        Cursor = cursor;
        _preTickApplied.Clear();
    }

    /// <summary>Applies the pre-tick actions at or before <paramref name="second"/> not yet applied; the cursor stays where it is.</summary>
    public void ApplyPreTick(double second, Action<RecordedAction> applier)
    {
        for (int i = Cursor; (i < Actions.Count) && (Actions[i].ElapsedSeconds <= second); i++)
        {
            if (SimulationEngine.IsPreTickAction(Actions[i]) && _preTickApplied.Add(i))
            {
                applier(Actions[i]);
            }
        }
    }

    /// <summary>Applies every action at or before <paramref name="second"/> the pre-tick pass did not, advancing the cursor past them all.</summary>
    public void ApplyThrough(double second, Action<RecordedAction> applier)
    {
        while ((Cursor < Actions.Count) && (Actions[Cursor].ElapsedSeconds <= second))
        {
            if (!_preTickApplied.Remove(Cursor))
            {
                applier(Actions[Cursor]);
            }

            Cursor++;
        }
    }
}
