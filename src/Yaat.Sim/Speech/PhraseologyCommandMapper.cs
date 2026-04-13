namespace Yaat.Sim.Speech;

/// <summary>
/// Non-static adapter that lets the rule-based <see cref="PhraseologyMapper"/> be consumed through
/// the <see cref="ISpeechCommandMapper"/> interface alongside the LLM fallback. Exists solely so the
/// speech pipeline can hold a list of <see cref="ISpeechCommandMapper"/> and try them in order.
/// The underlying static <see cref="PhraseologyMapper.Map(string, MapContext)"/> call remains the
/// source of truth for rule-based matching — this wrapper just makes it async-compatible.
/// </summary>
public sealed class PhraseologyCommandMapper : ISpeechCommandMapper
{
    public Task<MapResult?> MapAsync(string transcript, MapContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(PhraseologyMapper.Map(transcript, context));
    }
}
