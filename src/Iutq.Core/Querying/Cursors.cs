using System.Runtime.InteropServices;
using Iutq.Core.Primitives;

namespace Iutq.Core.Querying;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct TimelineCursor
{
    public readonly TimelineIndex Timeline;
    public readonly int Tick;
    public readonly TimelineDirection Direction;

    public TimelineCursor(TimelineIndex timeline, int tick, TimelineDirection direction)
    {
        Timeline = timeline;
        Tick = tick;
        Direction = direction;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public readonly struct TimelineSpan
{
    public readonly TimelineIndex Timeline;
    public readonly long PreviousRawTick;
    public readonly long CurrentRawTick;

    public TimelineSpan(TimelineIndex timeline, long previousRawTick, long currentRawTick)
    {
        Timeline = timeline;
        PreviousRawTick = previousRawTick;
        CurrentRawTick = currentRawTick;
    }
}
