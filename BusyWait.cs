using System.Diagnostics;

namespace BitFab.KW1281Test;

public static class BusyWait
{
    public static void Delay(long ms)
    {
        var maxTick = Stopwatch.GetTimestamp() + ms * TicksPerMs;
        while (Stopwatch.GetTimestamp() < maxTick)
        {
        }
    }

    private static readonly long TicksPerMs = Stopwatch.Frequency / 1000;
}