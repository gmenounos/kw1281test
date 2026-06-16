using System.Diagnostics;

namespace BitFab.KW1281Test;

public class BusyWait
{
    private readonly long _ticksPerCycle;
    private long? _nextTickTimestamp;

    public BusyWait(long msPerCycle)
    {
        _ticksPerCycle = msPerCycle * TicksPerMs;
    }

    public void DelayUntilNextCycle()
    {
        _nextTickTimestamp ??= Stopwatch.GetTimestamp() + _ticksPerCycle;

        while (Stopwatch.GetTimestamp() < _nextTickTimestamp)
        {
        }
        _nextTickTimestamp += _ticksPerCycle;
    }

    public static void Delay(long ms)
    {
        var waiter = new BusyWait(ms);
        waiter.DelayUntilNextCycle();
    }

    private static readonly long TicksPerMs = Stopwatch.Frequency / 1000;
}