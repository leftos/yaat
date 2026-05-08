namespace Yaat.Sim.Pilot;

public enum FrequencyActivityLevel
{
    Quiet,
    Moderate,
    Busy,
    Saturated,
}

public sealed class FrequencyActivityMeter
{
    private const double WindowSeconds = 60.0;
    private readonly Queue<double> _transmissionTimes = [];

    public FrequencyActivityLevel Level { get; private set; } = FrequencyActivityLevel.Quiet;

    public int Count => _transmissionTimes.Count;

    public void Record(double elapsedSeconds)
    {
        _transmissionTimes.Enqueue(elapsedSeconds);
        Trim(elapsedSeconds);
        Level = Classify(_transmissionTimes.Count);
    }

    public void Trim(double elapsedSeconds)
    {
        while (_transmissionTimes.Count > 0 && (elapsedSeconds - _transmissionTimes.Peek()) >= WindowSeconds)
        {
            _transmissionTimes.Dequeue();
        }

        Level = Classify(_transmissionTimes.Count);
    }

    private static FrequencyActivityLevel Classify(int count) =>
        count switch
        {
            < 5 => FrequencyActivityLevel.Quiet,
            <= 12 => FrequencyActivityLevel.Moderate,
            <= 20 => FrequencyActivityLevel.Busy,
            _ => FrequencyActivityLevel.Saturated,
        };
}
